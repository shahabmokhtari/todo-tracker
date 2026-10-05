using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TodoTracker.Core.Vault;

/// <summary>One saved version of the vault (or of a task file).</summary>
public sealed record VaultVersion(string Id, DateTimeOffset At, string Message, string? Path = null);

/// <summary>
/// Version history for the vault, kept in a private git repository in local app data (never in the synced vault, and
/// separate from any repository the user has) so every change, in the app or outside it, can be looked at and restored.
/// Requires <c>git</c> on the PATH; without it there is simply no history.
/// </summary>
public sealed partial class VaultHistory : IDisposable
{
    private const string Separator = "\u001f";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly VaultBoardStore _store;
    private readonly string _git;
    private readonly string _gitDir;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private VaultHistory(VaultBoardStore store, string git)
    {
        _store = store;
        _git = git;
        _gitDir = store.HistoryPath;
    }

    private string EmptyConfig => Path.Combine(_gitDir, "todo-tracker-empty-config");

    /// <summary>History for <paramref name="store"/>, or null when git isn't available.</summary>
    public static VaultHistory? TryCreate(VaultBoardStore store, string git = "git")
    {
        ArgumentNullException.ThrowIfNull(store);
        var history = new VaultHistory(store, git);
        try
        {
            if (history.Run(["--version"]).Code != 0)
            {
                return null;
            }

            Directory.CreateDirectory(history._gitDir);
            if (!File.Exists(history.EmptyConfig))
            {
                File.WriteAllText(history.EmptyConfig, string.Empty);
            }

            if (!Directory.Exists(Path.Combine(history._gitDir, "objects")))
            {
                history.Git("init", "--quiet");
            }

            // Attachments are large and never edited in place, so they aren't versioned.
            var exclude = Path.Combine(history._gitDir, "info", "exclude");
            Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
            File.WriteAllText(exclude, $"/{VaultFiles.MetaFolder}/\n/{VaultFiles.AttachmentsFolder}/\n.obsidian/\n.trash/\n.git\n*.tmp\n.DS_Store\n");
            return history;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Saves a version if anything changed since the last one. Returns whether a version was saved.</summary>
    public async Task<bool> CommitAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var activity = await _store.ReadAsync(b => b.Activity.ToList(), cancellationToken).ConfigureAwait(false);
            RemoveStaleIndexLock();

            // Hash new content without holding the vault lock (that can take a while); the add under the lock is then quick.
            Run(Args("add", "--all", "--", "."));
            return await _store.WithLockAsync(() =>
            {
                AddAll();
                if (Run(Args("diff", "--cached", "--quiet")).Code == 0)
                {
                    MarkSeen(activity);
                    return false;
                }

                var changed = Git("diff", "--cached", "--name-only").Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var message = Message(activity.Skip(FirstUnseen(activity)).ToList(), changed);
                Git("commit", "--quiet", "--no-verify", "-m", message);
                MarkSeen(activity);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Newest first; for one file when <paramref name="path"/> is given (following renames).</summary>
    public Task<IReadOnlyList<VaultVersion>> LogAsync(string? path, int limit = 100) => Task.Run(() =>
    {
        if (Run(Args("rev-parse", "--verify", "--quiet", "HEAD")).Code != 0)
        {
            return (IReadOnlyList<VaultVersion>)[];
        }

        var args = new List<string> { "log", $"-n{Math.Clamp(limit, 1, 1000)}", $"--format=%H{Separator}%aI{Separator}%s", "--name-only" };
        if (path is not null)
        {
            args.AddRange(["--follow", "--", path]);
        }

        var versions = new List<VaultVersion>();
        foreach (var block in Git([.. args]).Split('\n'))
        {
            var parts = block.Split(Separator);
            if (parts.Length == 3)
            {
                versions.Add(new VaultVersion(parts[0], DateTimeOffset.Parse(parts[1], CultureInfo.InvariantCulture), parts[2]));
            }
            else if (block.Length > 0 && versions.Count > 0 && path is not null && versions[^1].Path is null)
            {
                versions[^1] = versions[^1] with { Path = block.Trim() };
            }
        }

        return versions;
    });

    /// <summary>Versions of the file that holds the task (its top-level task's file).</summary>
    public async Task<IReadOnlyList<VaultVersion>> TaskHistoryAsync(Guid taskId)
    {
        var path = await PathOfAsync(taskId).ConfigureAwait(false);
        return await LogAsync(path).ConfigureAwait(false);
    }

    /// <summary>The task file's markdown as it was in <paramref name="versionId"/>; null if it didn't exist then.</summary>
    public async Task<string?> TaskVersionAsync(Guid taskId, string versionId)
    {
        ValidateVersion(versionId);
        var versions = await TaskHistoryAsync(taskId).ConfigureAwait(false);
        var path = versions.FirstOrDefault(v => v.Id.StartsWith(versionId, StringComparison.OrdinalIgnoreCase))?.Path ?? await PathOfAsync(taskId).ConfigureAwait(false);
        var result = Run(Args("show", $"{versionId}:{path}"));
        return result.Code == 0 ? result.Output : null;
    }

    /// <summary>Puts the task's file back as it was in <paramref name="versionId"/> (then saves that as a new version).</summary>
    public async Task RestoreTaskAsync(Guid taskId, string versionId, Actor actor)
    {
        var text = await TaskVersionAsync(taskId, versionId).ConfigureAwait(false) ?? throw new TaskNotFoundException($"Version {versionId} of this task was not found.");

        // Keep what is there now (edits from the last few seconds may not be saved as a version yet).
        await CommitAsync().ConfigureAwait(false);
        await _store.RestoreRootFileAsync(taskId, text, actor, $"Restored an earlier version ({versionId[..Math.Min(7, versionId.Length)]})").ConfigureAwait(false);
        await CommitAsync().ConfigureAwait(false);
    }

    public void Dispose() => _gate.Dispose();

    private async Task<string> PathOfAsync(Guid taskId)
    {
        await _store.ReadAsync(b => b.Get(taskId)).ConfigureAwait(false);
        return _store.PathOf(taskId) ?? throw TaskNotFoundException.ForTask(taskId);
    }

    // The last activity entry already described by a version, kept next to the repository so every process
    // (app, tt, tt mcp) describes only what is new.
    private string SeenPath => Path.Combine(_gitDir, "todo-tracker-seen");

    private int FirstUnseen(List<ActivityEntry> activity)
    {
        string marker;
        try
        {
            marker = File.ReadAllText(SeenPath).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        for (var i = activity.Count - 1; i >= 0; i--)
        {
            if (Marker(activity[i]) == marker)
            {
                return i + 1;
            }
        }

        return 0;
    }

    private void MarkSeen(List<ActivityEntry> activity)
    {
        if (activity.Count > 0)
        {
            try
            {
                File.WriteAllText(SeenPath, Marker(activity[^1]));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static string Marker(ActivityEntry entry) =>
        $"{entry.At.UtcTicks.ToString(CultureInfo.InvariantCulture)} {entry.ItemId:N} {entry.Kind} {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(entry.Summary)))[..16]}";

    /// <summary>Stages everything; another process's quick git command may briefly hold the index, so retry.</summary>
    private void AddAll()
    {
        for (var attempt = 1; ; attempt++)
        {
            var (code, _, error) = Run(Args("add", "--all", "--", "."));
            if (code == 0)
            {
                return;
            }

            if (attempt == 5 || !error.Contains("index.lock", StringComparison.Ordinal))
            {
                throw new IOException($"git add failed: {error.Trim()}");
            }

            Thread.Sleep(200 * attempt);
        }
    }

    /// <summary>A git that was killed (or a crash) leaves index.lock behind, which would stop history for good.</summary>
    private void RemoveStaleIndexLock()
    {
        var path = Path.Combine(_gitDir, "index.lock");
        try
        {
            if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > Timeout + Timeout)
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void ValidateVersion(string versionId)
    {
        if (string.IsNullOrEmpty(versionId) || !VersionId().IsMatch(versionId))
        {
            throw new ArgumentException("That isn't a version id.", nameof(versionId));
        }
    }

    private static string Message(List<ActivityEntry> entries, string[] files)
    {
        if (entries.Count == 0)
        {
            var names = files.Select(f => Path.GetFileNameWithoutExtension(f)).Distinct().ToList();
            return $"Edited outside the app: {string.Join(", ", names.Take(5))}{(names.Count > 5 ? $" (+{names.Count - 5} more)" : string.Empty)}";
        }

        var summaries = entries.Select(e => $"{e.Summary} ({e.Actor.DisplayName})").Distinct().ToList();
        var first = summaries[0].Length > 120 ? summaries[0][..117] + "…" : summaries[0];
        var body = string.Join('\n', summaries.Take(50));
        return summaries.Count == 1 ? first : $"{first} (+{summaries.Count - 1} more)\n\n{body}";
    }

    private string[] Args(params string[] args) =>
    [
        "-c", "core.autocrlf=false", "-c", "core.quotepath=off", "-c", "core.longpaths=true", "-c", "commit.gpgsign=false",
        "-c", "user.name=Todo Tracker", "-c", "user.email=todo-tracker@localhost", "-c", "core.safecrlf=false",
        "-c", "core.fsmonitor=false", "-c", "core.hooksPath=" + Path.Combine(_gitDir, "no-hooks"),
        $"--git-dir={_gitDir}", $"--work-tree={_store.RootPath}", .. args,
    ];

    private string Git(params string[] args)
    {
        var (code, output, error) = Run(Args(args));
        return code == 0 ? output : throw new IOException($"git {args[0]} failed: {error.Trim()}");
    }

    private (int Code, string Output, string Error) Run(IReadOnlyList<string> args)
    {
        var start = new ProcessStartInfo(_git)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _store.RootPath,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        // Ignore the user's and the system's git settings (pagers, hooks, credential helpers, fsmonitor...).
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = EmptyConfig;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Couldn't start git.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(Timeout))
        {
            process.Kill(entireProcessTree: true);
            throw new IOException("git took too long.");
        }

        return (process.ExitCode, output.Result, error.Result);
    }

    [GeneratedRegex("^[0-9a-fA-F]{7,64}$")]
    private static partial Regex VersionId();
}
