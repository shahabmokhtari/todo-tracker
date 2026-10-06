using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TodoTracker.Core.Sync;
using TodoTracker.Core.Vault;

namespace TodoTracker.Server.Plugins.Sync;

public sealed record SyncProviderView(string Id, string Name, bool Available, string Detail, bool Active);

/// <summary>A clash; <paramref name="Ready"/> once both computers have the merged task (a side can be chosen then).</summary>
public sealed record SyncConflictView(string Key, string Path, string PeerName, DateTimeOffset At, bool Ready);

public sealed record MergeTool(string Id, string Name, string Path);

/// <summary>
/// What the Sync panel shows. State: <c>off</c>, <c>unavailable</c> (nothing to sync with), <c>idle</c>,
/// <c>syncing</c>, <c>error</c>. <paramref name="Library"/> names this set of tasks: computers sync when they use the
/// same place and the same library (a second tasks folder gets its own).
/// </summary>
public sealed record SyncView(
    string Choice,
    string State,
    string? Provider,
    string? Where,
    DateTimeOffset? LastSync,
    string? Problem,
    string Library,
    IReadOnlyList<SyncProviderView> Providers,
    IReadOnlyList<SyncDevice> Devices,
    IReadOnlyList<SyncConflictView> Conflicts,
    IReadOnlyList<MergeTool> MergeTools);

/// <summary>
/// Keeps the tasks folder in sync with the person's other devices through the chosen provider (by default the first
/// one available: OneDrive, then iCloud Drive). Every provider is a plugin; this only picks one and runs the engine.
/// </summary>
public sealed class SyncService : IDisposable
{
    private readonly VaultBoardStore _vault;
    private readonly IReadOnlyList<ISyncProvider> _providers;
    private readonly string _dir;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _trigger = new(0, 1);
    private (string Key, SyncEngine Engine)? _engine;
    private bool _syncing;
    private string? _problem;

    public SyncService(VaultBoardStore vault, IEnumerable<ISyncProvider> providers, TodoTrackerServerOptions options, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _providers = [.. (providers ?? []).OrderBy(p => p.Order)];
        _time = time ?? TimeProvider.System;

        // Per tasks folder: a computer can sync more than one.
        var vaultKey = Hash(Path.GetFullPath(vault.RootPath).ToUpperInvariant());
        _dir = Path.Combine(options.DataDirectory, "sync", vaultKey);
        Directory.CreateDirectory(_dir);
    }

    /// <summary>Raised after a sync (or a change of settings), for screens that show the status.</summary>
    public event EventHandler? Changed;

    /// <summary>"auto" (the first available provider), "off", or a provider id.</summary>
    public string Choice => Setting("provider") ?? "auto";

    /// <summary>Which set of tasks this folder is (the folder's name unless changed): one folder or gist each.</summary>
    public string Library => Setting("library") is { Length: > 0 } library ? library : DefaultLibrary(_vault.RootPath);

    public IReadOnlyList<ISyncProvider> Providers => _providers;

    public SyncView View
    {
        get
        {
            var choice = Choice;
            var active = Active(out var reason);
            var state = active is null ? null : StateFor(active);
            var status = state?.Status ?? SyncStatus.Empty;
            var views = _providers.Select(p =>
            {
                var check = p.Check(_vault.RootPath);
                return new SyncProviderView(p.Id, p.Name, check.Available, check.Detail, p == active);
            }).ToList();
            bool syncing;
            string? problem;
            lock (_lock)
            {
                syncing = _syncing;
                problem = _problem;
            }

            var shown = choice == "off" ? "off" : active is null ? "unavailable" : syncing ? "syncing" : problem is not null ? "error" : "idle";
            return new SyncView(
                choice,
                shown,
                active?.Name,
                active is null ? reason : active.Check(_vault.RootPath).Detail,
                status.LastSync,
                active is null ? null : problem,
                Library,
                views,
                status.Devices,
                [.. status.Conflicts.Select(c => new SyncConflictView(c.Key, c.Path, c.PeerName, c.At, c.Result is null || state!.LoadBase(c.Peer).GetValueOrDefault(c.Key)?.Hash == c.Result))],
                MergeTools.Find());
        }
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    /// <summary>Picks a provider ("auto", "off" or an id) and syncs with it from now on.</summary>
    public void Choose(string choice)
    {
        if (choice is not ("auto" or "off") && !_providers.Any(p => p.Id == choice))
        {
            throw new ArgumentException($"There is no sync provider \"{choice}\".", nameof(choice));
        }

        Save("provider", choice);
    }

    /// <summary>Names this folder's set of tasks (computers using the same name sync together).</summary>
    public void SetLibrary(string library)
    {
        var name = CleanLibrary(library);
        if (name.Length == 0)
        {
            throw new ArgumentException("Give it a name (letters, digits, spaces).", nameof(library));
        }

        Save("library", name);
    }

    /// <summary>Asks the background loop to sync soon.</summary>
    public void Trigger()
    {
        try
        {
            _trigger.Release();
        }
        catch (SemaphoreFullException)
        {
            // A sync is already asked for.
        }
    }

    internal Task<bool> WaitForTriggerAsync(TimeSpan timeout, CancellationToken cancellationToken) => _trigger.WaitAsync(timeout, cancellationToken);

    /// <summary>Syncs now with the chosen provider (nothing when sync is off or no provider is available).</summary>
    public async Task<SyncView> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        SyncEngine? engine;
        try
        {
            engine = EngineFor(Active(out _));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetProblem(ex.Message);
            return View;
        }

        if (engine is null)
        {
            return View;
        }

        lock (_lock)
        {
            _syncing = true;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            var result = await engine.SyncAsync(cancellationToken).ConfigureAwait(false);
            SetProblem(result.Problem);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever happened (a timeout, odd data from another computer), sync says so and tries again later.
            SetProblem(ex is OperationCanceledException ? "The sync took too long (no connection?)." : ex.Message);
        }
        finally
        {
            lock (_lock)
            {
                _syncing = false;
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        return View;
    }

    /// <summary>
    /// Settles a conflict: "mine" or "theirs" redoes the merge keeping that side only where the two clashed (every
    /// other change from both stays); "merged" keeps the task as it is now.
    /// </summary>
    public async Task<SyncView> ResolveAsync(string key, string choice, CancellationToken cancellationToken = default)
    {
        if (choice is not ("mine" or "theirs" or "merged"))
        {
            throw new ArgumentException("Choose mine, theirs or merged.", nameof(choice));
        }

        var engine = EngineFor(Active(out _)) ?? throw new InvalidOperationException("Sync is off.");
        var conflict = Newest(engine, key);
        if (choice != "merged")
        {
            var local = await _vault.ReadSyncAsync(cancellationToken).ConfigureAwait(false);
            var current = local.Entries.GetValueOrDefault(key) ?? throw new InvalidOperationException("That file isn't in the tasks folder any more.");
            if (conflict.Result is { } result && current.Hash != result)
            {
                throw new InvalidOperationException("The task changed since; compare the versions to fix it by hand.");
            }

            var mine = engine.State.ReadBlob(conflict.Mine);
            var theirs = engine.State.ReadBlob(conflict.Theirs);
            if (mine is null || theirs is null)
            {
                throw new InvalidOperationException("That version isn't kept any more.");
            }

            // Choosing a side before the other computer has the merged task too would be undone by its merge.
            if (!Ready(engine, conflict))
            {
                throw new InvalidOperationException($"Wait until {conflict.PeerName} has synced too (usually within a minute), then choose.");
            }

            var content = conflict.Base is { } baseHash && engine.State.ReadBlob(baseHash) is { } @base
                ? Encoding.UTF8.GetBytes(TextMerge.Merge(
                    Encoding.UTF8.GetString(@base),
                    Encoding.UTF8.GetString(mine),
                    Encoding.UTF8.GetString(theirs),
                    SyncKeys.KindOf(key) == SyncKind.Task ? SyncPlanner.TaskLineKeys : null,
                    choice == "mine" ? ConflictPolicy.First : ConflictPolicy.Second).Text)
                : choice == "mine" ? mine : theirs;
            await _vault.ApplySyncAsync([new SyncOp(SyncOpKind.Write, current.Path, current.Hash, content)], cancellationToken).ConfigureAwait(false);
        }

        await engine.DismissAsync(key, cancellationToken).ConfigureAwait(false);
        Trigger();
        Changed?.Invoke(this, EventArgs.Empty);
        return View;
    }

    /// <summary>Stops merging a device (a computer that's gone, or this computer's old tasks folder).</summary>
    public async Task<SyncView> ForgetAsync(string device, CancellationToken cancellationToken = default)
    {
        var engine = EngineFor(Active(out _)) ?? throw new InvalidOperationException("Sync is off.");
        await engine.ForgetAsync(device, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
        return View;
    }

    /// <summary>Opens the other device's version next to the file in a merge tool (the file is edited in place).</summary>
    public async Task OpenMergeToolAsync(string key, string toolId, CancellationToken cancellationToken = default)
    {
        var tool = MergeTools.Find().FirstOrDefault(t => t.Id == toolId) ?? throw new ArgumentException($"{toolId} isn't installed.", nameof(toolId));
        var engine = EngineFor(Active(out _)) ?? throw new InvalidOperationException("Sync is off.");
        var conflict = Newest(engine, key);
        var theirs = engine.State.ReadBlob(conflict.Theirs) ?? throw new InvalidOperationException("The other version isn't kept any more.");
        var local = await _vault.ReadSyncAsync(cancellationToken).ConfigureAwait(false);
        var current = local.Entries.GetValueOrDefault(key) ?? throw new InvalidOperationException("That file isn't in the tasks folder any more.");

        // The other version gets a plain name of our own (never one from another computer).
        var compare = Path.Combine(_dir, "compare");
        Directory.CreateDirectory(compare);
        var other = Path.Combine(compare, $"other-{Hash(key)}{Path.GetExtension(current.Path)}");
        await File.WriteAllBytesAsync(other, theirs, cancellationToken).ConfigureAwait(false);
        MergeTools.Open(tool, other, Path.GetFullPath(Path.Combine(_vault.RootPath, current.Path.Replace('/', Path.DirectorySeparatorChar))));
    }

    public void Dispose()
    {
        _engine?.Engine.Dispose();
        _trigger.Dispose();
    }

    internal static string DefaultLibrary(string vaultRoot) =>
        CleanLibrary(Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(vaultRoot)))) is { Length: > 0 } name ? name : "Todo Tracker";

    private static string CleanLibrary(string name)
    {
        var clean = new string([.. (name ?? string.Empty).Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_')]).Trim();
        return clean.Length > 60 ? clean[..60].Trim() : clean;
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    private static SyncConflictRecord Newest(SyncEngine engine, string key) =>
        engine.State.Status.Conflicts.Where(c => c.Key == key).OrderByDescending(c => c.At).FirstOrDefault()
            ?? throw new InvalidOperationException("That conflict was already settled.");

    /// <summary>Both computers have the merged task (it's what they last agreed on): a side can be chosen now.</summary>
    private static bool Ready(SyncEngine engine, SyncConflictRecord conflict) =>
        conflict.Result is null || engine.State.LoadBase(conflict.Peer).GetValueOrDefault(conflict.Key)?.Hash == conflict.Result;

    private string? Setting(string name)
    {
        try
        {
            return File.Exists(SettingsPath) ? JsonNode.Parse(File.ReadAllText(SettingsPath))?[name]?.GetValue<string>() : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private void Save(string name, string value)
    {
        JsonObject settings;
        try
        {
            settings = File.Exists(SettingsPath) ? JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ?? [] : [];
        }
        catch (JsonException)
        {
            settings = [];
        }

        settings[name] = value;
        File.WriteAllText(SettingsPath, settings.ToJsonString());
        SetProblem(null);
        Trigger();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SetProblem(string? problem)
    {
        lock (_lock)
        {
            _problem = problem;
        }
    }

    private ISyncProvider? Active(out string reason)
    {
        var choice = Choice;
        if (choice == "off")
        {
            reason = "Sync is off.";
            return null;
        }

        var candidates = choice == "auto" ? _providers.Where(p => p.Automatic) : _providers.Where(p => p.Id == choice);
        string? why = null;
        foreach (var provider in candidates)
        {
            var check = provider.Check(_vault.RootPath);
            if (check.Available)
            {
                reason = string.Empty;
                return provider;
            }

            why ??= check.Detail;
        }

        reason = why ?? (_providers.Count == 0 ? "No sync plugin is on." : "Nothing to sync with on this computer.");
        return null;
    }

    /// <summary>The engine for this provider, place and library (a new one when any of them changed).</summary>
    private SyncEngine? EngineFor(ISyncProvider? provider)
    {
        if (provider is null)
        {
            return null;
        }

        var library = Library;
        var key = $"{provider.Id}|{provider.Check(_vault.RootPath).Detail}|{library}";
        lock (_lock)
        {
            if (_engine is { } current && current.Key == key)
            {
                return current.Engine;
            }

            _engine?.Engine.Dispose();
            var engine = new SyncEngine(_vault, provider.CreateRemote(_vault.RootPath, library), StateFor(provider), Path.Combine(_dir, provider.Id + ".lock"), _time);
            _engine = (key, engine);
            return engine;
        }
    }

    private SyncState StateFor(ISyncProvider provider) => new(Path.Combine(_dir, $"{provider.Id}-{Hash(Library)}"), _dir);
}

/// <summary>Syncs when the app starts, a few seconds after changes settle, and every minute (to pick up other devices' changes).</summary>
public sealed partial class SyncLoop(SyncService sync, VaultBoardStore vault, TodoTrackerServerOptions options, ILogger<SyncLoop> logger) : BackgroundService
{
    internal static TimeSpan Quiet { get; set; } = TimeSpan.FromSeconds(5);

    internal static TimeSpan Periodic { get; set; } = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.EnableBackgroundLoop)
        {
            return;
        }

        void OnChanged(object? sender, EventArgs e) => sync.Trigger();
        vault.Changed += OnChanged;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Off the caller's thread: hashing the folder and reading a cloud folder are synchronous in places.
                    var view = await Task.Run(() => sync.SyncNowAsync(stoppingToken), stoppingToken).ConfigureAwait(false);
                    if (view.Problem is { } problem)
                    {
                        LogSyncFailed(logger, problem);
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested && ex is not OutOfMemoryException)
                {
                    // Sync must never take the app down with it.
                    LogSyncFailed(logger, ex.Message);
                }

                await sync.WaitForTriggerAsync(Periodic, stoppingToken).ConfigureAwait(false);

                // A burst of edits becomes one sync.
                while (await sync.WaitForTriggerAsync(Quiet, stoppingToken).ConfigureAwait(false))
                {
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            vault.Changed -= OnChanged;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sync didn't finish: {Problem}")]
    private static partial void LogSyncFailed(ILogger logger, string problem);
}

/// <summary>Merge tools to compare two versions of a file: Beyond Compare, WinMerge, Visual Studio Code.</summary>
public static class MergeTools
{
    public static IReadOnlyList<MergeTool> Find()
    {
        var found = new List<MergeTool>();
        void Add(string id, string name, params string?[] candidates)
        {
            if (candidates.Select(c => c is null ? null : File.Exists(c) ? c : OnPath(c)).FirstOrDefault(p => p is not null) is { } hit)
            {
                found.Add(new MergeTool(id, name, hit));
            }
        }

        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (OperatingSystem.IsWindows())
        {
            Add("bcompare", "Beyond Compare", Path.Combine(programs, "Beyond Compare 5", "BCompare.exe"), Path.Combine(programs, "Beyond Compare 4", "BCompare.exe"), "BCompare.exe");
            Add("winmerge", "WinMerge", Path.Combine(programs, "WinMerge", "WinMergeU.exe"), Path.Combine(local, "Programs", "WinMerge", "WinMergeU.exe"), "WinMergeU.exe");

            // Code.exe itself (code.cmd would go through cmd.exe, which mangles quotes and runs & in file names).
            var codeCmd = OnPath("code.cmd");
            Add("vscode", "Visual Studio Code", Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"), Path.Combine(programs, "Microsoft VS Code", "Code.exe"), codeCmd is null ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(codeCmd)!, "..", "Code.exe")));
        }
        else
        {
            Add("bcompare", "Beyond Compare", "/usr/local/bin/bcomp", "bcomp");
            Add("vscode", "Visual Studio Code", "/usr/local/bin/code", "code");
        }

        return found;
    }

    /// <summary>The program and its arguments: <paramref name="other"/> (read-only, left) next to <paramref name="file"/> (edited in place, right).</summary>
    public static ProcessStartInfo StartInfo(MergeTool tool, string other, string file)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var start = new ProcessStartInfo(tool.Path) { UseShellExecute = false };
        var args = tool.Id switch
        {
            "winmerge" => new[] { "/u", "/wl", "/dl", "Other computer", "/dr", "This computer", other, file },
            "vscode" => ["--diff", other, file],
            _ => [other, file],
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        return start;
    }

    public static void Open(MergeTool tool, string other, string file)
    {
        using var process = Process.Start(StartInfo(tool, other, file));
    }

    private static string? OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(dir => Path.Combine(dir.Trim('"'), name))
            .FirstOrDefault(File.Exists);
}
