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

public sealed record SyncConflictView(string Key, string Path, string PeerName, DateTimeOffset At);

public sealed record MergeTool(string Id, string Name, string Path);

/// <summary>
/// What the Sync panel shows. State: <c>off</c>, <c>unavailable</c> (nothing to sync with), <c>idle</c>,
/// <c>syncing</c>, <c>error</c>.
/// </summary>
public sealed record SyncView(
    string Choice,
    string State,
    string? Provider,
    string? Where,
    DateTimeOffset? LastSync,
    string? Problem,
    IReadOnlyList<SyncProviderView> Providers,
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
    private readonly SemaphoreSlim _trigger = new(0);
    private (string Provider, SyncEngine Engine)? _engine;
    private bool _syncing;
    private string? _problem;

    public SyncService(VaultBoardStore vault, IEnumerable<ISyncProvider> providers, TodoTrackerServerOptions options, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _providers = [.. (providers ?? []).OrderBy(p => p.Order)];
        _time = time ?? TimeProvider.System;

        // Per tasks folder: a device can sync more than one vault.
        var vaultKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(vault.RootPath).ToUpperInvariant())))[..16];
        _dir = Path.Combine(options.DataDirectory, "sync", vaultKey);
        Directory.CreateDirectory(_dir);
    }

    /// <summary>Raised after a sync (or a change of settings), for screens that show the status.</summary>
    public event EventHandler? Changed;

    /// <summary>"auto" (the first available provider), "off", or a provider id.</summary>
    public string Choice
    {
        get
        {
            try
            {
                return File.Exists(SettingsPath) ? JsonNode.Parse(File.ReadAllText(SettingsPath))?["provider"]?.GetValue<string>() ?? "auto" : "auto";
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
            {
                return "auto";
            }
        }
    }

    public IReadOnlyList<ISyncProvider> Providers => _providers;

    public SyncView View
    {
        get
        {
            var choice = Choice;
            var active = Active(out var reason);
            var status = active is null ? SyncStatus.Empty : StateFor(active).Status;
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

            var state = choice == "off" ? "off" : active is null ? "unavailable" : syncing ? "syncing" : problem is not null ? "error" : "idle";
            return new SyncView(
                choice,
                state,
                active?.Name,
                active is null ? reason : active.Check(_vault.RootPath).Detail,
                status.LastSync,
                active is null ? null : problem,
                views,
                [.. status.Conflicts.Select(c => new SyncConflictView(c.Key, c.Path, c.PeerName, c.At))],
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

        File.WriteAllText(SettingsPath, new JsonObject { ["provider"] = choice }.ToJsonString());
        lock (_lock)
        {
            _problem = null;
        }

        Trigger();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Asks the background loop to sync soon.</summary>
    public void Trigger() => _trigger.Release();

    internal Task<bool> WaitForTriggerAsync(TimeSpan timeout, CancellationToken cancellationToken) => _trigger.WaitAsync(timeout, cancellationToken);

    /// <summary>Syncs now with the chosen provider (nothing when sync is off or no provider is available).</summary>
    public async Task<SyncView> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        if (EngineFor(Active(out _)) is not { } engine)
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
            await engine.SyncAsync(cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                _problem = null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or HttpRequestException or SyncStaleException or TimeoutException)
        {
            lock (_lock)
            {
                _problem = ex.Message;
            }
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

    /// <summary>Settles a conflict: keep this device's version, the other device's, or keep the merged file as it is.</summary>
    public async Task<SyncView> ResolveAsync(string key, string choice, CancellationToken cancellationToken = default)
    {
        if (choice is not ("mine" or "theirs" or "merged"))
        {
            throw new ArgumentException("Choose mine, theirs or merged.", nameof(choice));
        }

        var engine = EngineFor(Active(out _)) ?? throw new InvalidOperationException("Sync is off.");
        var conflict = engine.State.Status.Conflicts.FirstOrDefault(c => c.Key == key) ?? throw new InvalidOperationException("That conflict was already settled.");
        if (choice != "merged")
        {
            var content = engine.State.ReadBlob(choice == "mine" ? conflict.Mine : conflict.Theirs)
                ?? throw new InvalidOperationException("That version isn't kept any more.");
            var local = await _vault.ReadSyncAsync(cancellationToken).ConfigureAwait(false);
            var current = local.Entries.GetValueOrDefault(key) ?? throw new InvalidOperationException("That file isn't in the tasks folder any more.");
            await _vault.ApplySyncAsync([new SyncOp(SyncOpKind.Write, current.Path, current.Hash, content)], cancellationToken).ConfigureAwait(false);
        }

        engine.Dismiss(key);
        Trigger();
        Changed?.Invoke(this, EventArgs.Empty);
        return View;
    }

    /// <summary>Opens the other device's version next to the file in a merge tool (the file is edited in place).</summary>
    public async Task OpenMergeToolAsync(string key, string toolId, CancellationToken cancellationToken = default)
    {
        var tool = MergeTools.Find().FirstOrDefault(t => t.Id == toolId) ?? throw new ArgumentException($"{toolId} isn't installed.", nameof(toolId));
        var engine = EngineFor(Active(out _)) ?? throw new InvalidOperationException("Sync is off.");
        var conflict = engine.State.Status.Conflicts.FirstOrDefault(c => c.Key == key) ?? throw new InvalidOperationException("That conflict was already settled.");
        var theirs = engine.State.ReadBlob(conflict.Theirs) ?? throw new InvalidOperationException("The other version isn't kept any more.");
        var local = await _vault.ReadSyncAsync(cancellationToken).ConfigureAwait(false);
        var current = local.Entries.GetValueOrDefault(key) ?? throw new InvalidOperationException("That file isn't in the tasks folder any more.");
        var compare = Path.Combine(_dir, "compare");
        Directory.CreateDirectory(compare);
        var other = Path.Combine(compare, $"{Path.GetFileNameWithoutExtension(current.Path)} (from {Sanitize(conflict.PeerName)}){Path.GetExtension(current.Path)}");
        await File.WriteAllBytesAsync(other, theirs, cancellationToken).ConfigureAwait(false);
        MergeTools.Open(tool, other, Path.GetFullPath(Path.Combine(_vault.RootPath, current.Path.Replace('/', Path.DirectorySeparatorChar))));
    }

    public void Dispose()
    {
        _engine?.Engine.Dispose();
        _trigger.Dispose();
    }

    private static string Sanitize(string name) => string.Concat(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));

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

    private SyncEngine? EngineFor(ISyncProvider? provider)
    {
        if (provider is null)
        {
            return null;
        }

        lock (_lock)
        {
            if (_engine is { } current && current.Provider == provider.Id)
            {
                return current.Engine;
            }

            _engine?.Engine.Dispose();
            var engine = new SyncEngine(_vault, provider.CreateRemote(_vault.RootPath), StateFor(provider), Path.Combine(_dir, provider.Id + ".lock"), _time);
            _engine = (provider.Id, engine);
            return engine;
        }
    }

    private SyncState StateFor(ISyncProvider provider) => new(Path.Combine(_dir, provider.Id), _dir);
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
                var view = await sync.SyncNowAsync(stoppingToken).ConfigureAwait(false);
                if (view.Problem is { } problem)
                {
                    LogSyncFailed(logger, problem);
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
            if (candidates.FirstOrDefault(c => c is not null && (File.Exists(c) || OnPath(c) is not null)) is { } hit)
            {
                found.Add(new MergeTool(id, name, File.Exists(hit) ? hit : OnPath(hit)!));
            }
        }

        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (OperatingSystem.IsWindows())
        {
            Add("bcompare", "Beyond Compare", Path.Combine(programs, "Beyond Compare 5", "BCompare.exe"), Path.Combine(programs, "Beyond Compare 4", "BCompare.exe"), "BCompare.exe");
            Add("winmerge", "WinMerge", Path.Combine(programs, "WinMerge", "WinMergeU.exe"), Path.Combine(local, "Programs", "WinMerge", "WinMergeU.exe"), "WinMergeU.exe");
            Add("vscode", "Visual Studio Code", Path.Combine(local, "Programs", "Microsoft VS Code", "bin", "code.cmd"), "code.cmd");
        }
        else
        {
            Add("bcompare", "Beyond Compare", "/usr/local/bin/bcomp", "bcomp");
            Add("vscode", "Visual Studio Code", "/usr/local/bin/code", "code");
        }

        return found;
    }

    /// <summary>Opens <paramref name="other"/> (read-only, left) next to <paramref name="file"/> (edited in place, right).</summary>
    public static void Open(MergeTool tool, string other, string file)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var start = new ProcessStartInfo(tool.Path) { UseShellExecute = false };
        if (tool.Path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(tool.Path);
        }

        var args = tool.Id switch
        {
            "winmerge" => new[] { "/u", "/wl", "/dl", "Other device", "/dr", "This device", other, file },
            "vscode" => ["--diff", other, file],
            _ => [other, file],
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start);
    }

    private static string? OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(dir => Path.Combine(dir.Trim('"'), name))
            .FirstOrDefault(File.Exists);
}
