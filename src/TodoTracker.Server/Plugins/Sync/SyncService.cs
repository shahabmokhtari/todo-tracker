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

/// <summary>
/// A clash; <paramref name="Ready"/> once both computers have the merged task (a side can be chosen then).
/// <paramref name="Place"/>: the sync place it came through (with more than one).
/// </summary>
public sealed record SyncConflictView(string Key, string Path, string PeerName, DateTimeOffset At, bool Ready, string Place = "");

/// <summary>
/// A place to sync through and how it's used: <c>both</c> (read and write), <c>read</c> (the others' changes come in),
/// <c>write</c> (this computer's tasks go out) or <c>off</c>. <paramref name="OtherDevices"/>: computers seen there
/// for this library (null: not looked yet).
/// </summary>
public sealed record SyncPlaceView(string Id, string Name, bool Available, string Detail, string Mode, int? OtherDevices);

public sealed record MergeTool(string Id, string Name, string Path);

/// <summary>
/// What the Sync panel shows. State: <c>off</c>, <c>unavailable</c> (nothing to sync with), <c>idle</c>,
/// <c>syncing</c>, <c>error</c>. <paramref name="Library"/> names this set of tasks: computers sync when they use the
/// same place and the same library (a second tasks folder gets its own). <paramref name="Warning"/>: the tasks are in
/// more than one place and only some are used (choose how to use each in <paramref name="Places"/>).
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
    IReadOnlyList<MergeTool> MergeTools,
    IReadOnlyList<SyncPlaceView>? Places = null,
    string? Warning = null);

/// <summary>
/// Keeps the tasks folder in sync with the person's other devices. By default through one place (the first available:
/// OneDrive, then iCloud Drive). With the tasks in more than one place, each place can be read (its computers' changes
/// are merged in, clashes listed to settle), written (this computer's tasks go there), or both. Every place is a
/// plugin; this only picks them and runs one engine per place.
/// </summary>
public sealed class SyncService : IDisposable
{
    private static readonly TimeSpan LookAgain = TimeSpan.FromMinutes(10);

    private readonly VaultBoardStore _vault;
    private readonly IReadOnlyList<ISyncProvider> _providers;
    private readonly string _dir;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Lock _settingsLock = new();
    private readonly SemaphoreSlim _trigger = new(0, 1);
    private readonly Dictionary<string, (string Key, SyncEngine Engine)> _engines = [];
    private readonly Dictionary<string, (string Library, int Count, DateTimeOffset At)> _seen = [];
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
            var uses = Uses(out var reason);
            var primary = uses.Count > 0 ? uses[0].Provider : null;
            var states = uses.Select(u => (u.Provider, u.Mode, State: StateFor(u.Provider))).ToList();
            var status = states.Count > 0 ? states[0].State.Status : SyncStatus.Empty;
            var checks = _providers.ToDictionary(p => p.Id, p => p.Check(_vault.RootPath));
            var views = _providers.Select(p => new SyncProviderView(p.Id, p.Name, checks[p.Id].Available, checks[p.Id].Detail, p == primary)).ToList();
            bool syncing;
            string? problem;
            lock (_lock)
            {
                syncing = _syncing;
                problem = _problem;
            }

            var places = _providers.Select(p =>
            {
                var use = uses.FirstOrDefault(u => u.Provider == p);
                var others = use.Provider is not null ? StateFor(p).Status.Devices.Count : Seen(p);
                return new SyncPlaceView(p.Id, p.Name, checks[p.Id].Available, checks[p.Id].Detail, use.Provider is null ? "off" : ModeName(use.Mode), others);
            }).ToList();

            var shown = choice == "off" ? "off" : uses.Count == 0 ? "unavailable" : syncing ? "syncing" : problem is not null ? "error" : "idle";
            return new SyncView(
                choice,
                shown,
                uses.Count == 0 ? null : string.Join(" + ", uses.Select(u => u.Provider.Name)),
                primary is null ? reason : checks[primary.Id].Detail,
                states.Select(s => s.State.Status.LastSync).Where(t => t is not null).DefaultIfEmpty(status.LastSync).Max(),
                uses.Count == 0 ? null : problem,
                Library,
                views,
                [.. states.SelectMany(s => s.State.Status.Devices).GroupBy(d => d.Device).Select(g => g.MaxBy(d => d.At)!)],
                [.. states.SelectMany(s => s.State.Status.Conflicts.Select(c => new SyncConflictView(c.Key, c.Path, c.PeerName, c.At, Ready(s.State, c) || !s.Mode.HasFlag(SyncMode.Write), s.Provider.Id)))],
                MergeTools.Find(),
                places,
                choice == "off" ? null : Warning(places));
        }
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    /// <summary>Picks one provider ("auto", "off" or an id) and syncs with it from now on (any per-place choice is cleared).</summary>
    public async Task ChooseAsync(string choice, CancellationToken cancellationToken = default)
    {
        if (choice is not ("auto" or "off") && !_providers.Any(p => p.Id == choice))
        {
            throw new ArgumentException($"There is no sync provider \"{choice}\".", nameof(choice));
        }

        var writtenBefore = Written();
        Save(new() { ["modes"] = string.Empty, ["provider"] = choice });
        await RetireAsync(writtenBefore, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// How one place is used: "both", "read", "write" or "off". The first such choice turns the automatic pick into
    /// per-place choices (the place used until now keeps reading and writing).
    /// </summary>
    public async Task SetModeAsync(string provider, string mode, CancellationToken cancellationToken = default)
    {
        if (!_providers.Any(p => p.Id == provider))
        {
            throw new ArgumentException($"There is no sync provider \"{provider}\".", nameof(provider));
        }

        var parsed = ParseMode(mode) ?? throw new ArgumentException("Choose both, read, write or off.", nameof(mode));
        var modes = Modes();
        if (modes.Count == 0)
        {
            foreach (var (place, current) in Uses(out _))
            {
                modes[place.Id] = current;
            }
        }

        var writtenBefore = Written();
        modes[provider] = parsed;
        var changes = new Dictionary<string, string> { ["modes"] = string.Join(';', modes.Select(m => $"{m.Key}={ModeName(m.Value)}")) };
        if (Choice == "off")
        {
            changes["provider"] = "auto";
        }

        Save(changes);
        await RetireAsync(writtenBefore, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The places this computer writes to now.</summary>
    private List<ISyncProvider> Written() => [.. Uses(out _).Where(u => u.Mode.HasFlag(SyncMode.Write)).Select(u => u.Provider)];

    /// <summary>
    /// A place no longer written: this computer's snapshot there goes (else it would stay a computer the others merge
    /// for months, and one starting to read the place later would bring back what was deleted here since).
    /// </summary>
    private async Task RetireAsync(List<ISyncProvider> writtenBefore, CancellationToken cancellationToken)
    {
        var writtenNow = Written();
        foreach (var provider in writtenBefore.Where(p => !writtenNow.Contains(p)))
        {
            try
            {
                var state = StateFor(provider);
                await provider.CreateRemote(_vault.RootPath, Library).ForgetAsync(state.DeviceId, cancellationToken).ConfigureAwait(false);
                state.Published = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                SetProblem($"Couldn't take this computer's tasks out of {provider.Name} ({ex.Message}).");
            }
        }
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

    /// <summary>
    /// Syncs now through every place in use: the ones read first (so what they bring is in the tasks folder), then the
    /// ones only written. Places not in use are looked at now and then, to say when tasks are there too.
    /// </summary>
    public async Task<SyncView> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        List<(ISyncProvider Provider, SyncMode Mode, SyncEngine Engine)> engines;
        try
        {
            engines = [.. Uses(out _)
                .OrderBy(u => u.Mode == SyncMode.Read ? 0 : u.Mode == SyncMode.Both ? 1 : 2)
                .Select(u => (u.Provider, u.Mode, EngineFor(u.Provider, u.Mode)))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetProblem(ex.Message);
            return View;
        }

        if (engines.Count > 0)
        {
            lock (_lock)
            {
                _syncing = true;
            }

            Changed?.Invoke(this, EventArgs.Empty);
            var problems = new List<string>();
            try
            {
                // With more than one place: when a later place brought changes, the places before it get them too
                // (a second round; nothing is read or written where nothing changed).
                for (var round = 0; round < (engines.Count > 1 ? 2 : 1); round++)
                {
                    problems.Clear();
                    var brought = false;
                    foreach (var (provider, _, engine) in engines)
                    {
                        var (problem, changes) = await SyncPlaceAsync(engine, cancellationToken).ConfigureAwait(false);
                        brought |= changes > 0 && provider != engines[0].Provider;
                        if (problem is not null)
                        {
                            problems.Add(engines.Count == 1 ? problem : $"{provider.Name}: {problem}");
                        }
                    }

                    if (!brought)
                    {
                        break;
                    }
                }
            }
            finally
            {
                SetProblem(problems.Count == 0 ? null : string.Join(' ', problems));
                lock (_lock)
                {
                    _syncing = false;
                }
            }
        }

        if (Choice != "off")
        {
            await LookAroundAsync(engines.Select(e => e.Provider).ToHashSet(), cancellationToken).ConfigureAwait(false);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return View;
    }

    private static async Task<(string? Problem, int Changes)> SyncPlaceAsync(SyncEngine engine, CancellationToken cancellationToken)
    {
        try
        {
            var result = await engine.SyncAsync(cancellationToken).ConfigureAwait(false);
            return (result.Problem, result.Changes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever happened (a timeout, odd data from another computer), sync says so and tries again later.
            return (ex is OperationCanceledException ? "The sync took too long (no connection?)." : ex.Message, 0);
        }
    }

    /// <summary>The places not in use that are here: are this library's tasks there too? (looked at every 10 minutes)</summary>
    private async Task LookAroundAsync(HashSet<ISyncProvider> used, CancellationToken cancellationToken)
    {
        var library = Library;
        var now = _time.GetUtcNow();
        foreach (var provider in _providers.Where(p => !used.Contains(p) && p.Check(_vault.RootPath).Available))
        {
            lock (_lock)
            {
                if (_seen.TryGetValue(provider.Id, out var seen) && seen.Library == library && now - seen.At < LookAgain)
                {
                    continue;
                }
            }

            try
            {
                // Only a look (nothing is created there, nothing downloaded): places that can't be looked at aren't.
                if (provider.CreateRemote(_vault.RootPath, library) is not ISyncPeek peek)
                {
                    continue;
                }

                var self = StateFor(provider).DeviceId;
                var count = (await peek.PeekAsync(self, cancellationToken).ConfigureAwait(false)).Count(d => now - d.At < SyncLimits.Memory);
                lock (_lock)
                {
                    _seen[provider.Id] = (library, count, now);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException or ArgumentException)
            {
                // Can't look now: try again next time.
            }
        }
    }

    /// <summary>
    /// Settles a conflict: "mine" or "theirs" redoes the merge keeping that side only where the two clashed (every
    /// other change from both stays); "merged" keeps the task as it is now. <paramref name="place"/>: where it came
    /// through (with more than one place; else the first that lists it).
    /// </summary>
    public async Task<SyncView> ResolveAsync(string key, string choice, string? place = null, CancellationToken cancellationToken = default)
    {
        if (choice is not ("mine" or "theirs" or "merged"))
        {
            throw new ArgumentException("Choose mine, theirs or merged.", nameof(choice));
        }

        var engine = EngineWith(key, place);
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
            if (engine.Mode.HasFlag(SyncMode.Write) && !Ready(engine.State, conflict))
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

    /// <summary>Stops merging a device (a computer that's gone, or this computer's old tasks folder), wherever it's seen.</summary>
    public async Task<SyncView> ForgetAsync(string device, CancellationToken cancellationToken = default)
    {
        var engines = Uses(out _).Select(u => EngineFor(u.Provider, u.Mode)).ToList();
        if (engines.Count == 0)
        {
            throw new InvalidOperationException("Sync is off.");
        }

        foreach (var engine in engines.Where(e => e.State.Status.Devices.Any(d => d.Device == device)).DefaultIfEmpty(engines[0]))
        {
            await engine.ForgetAsync(device, cancellationToken).ConfigureAwait(false);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return View;
    }

    /// <summary>Opens the other device's version next to the file in a merge tool (the file is edited in place).</summary>
    public async Task OpenMergeToolAsync(string key, string toolId, string? place = null, CancellationToken cancellationToken = default)
    {
        var tool = MergeTools.Find().FirstOrDefault(t => t.Id == toolId) ?? throw new ArgumentException($"{toolId} isn't installed.", nameof(toolId));
        var engine = EngineWith(key, place);
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
        foreach (var (_, engine) in _engines.Values)
        {
            engine.Dispose();
        }

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

    private static string ModeName(SyncMode mode) => mode switch
    {
        SyncMode.Both => "both",
        SyncMode.Read => "read",
        SyncMode.Write => "write",
        _ => "off",
    };

    private static SyncMode? ParseMode(string? text) => text switch
    {
        "both" => SyncMode.Both,
        "read" => SyncMode.Read,
        "write" => SyncMode.Write,
        "off" => SyncMode.None,
        _ => null,
    };

    private static SyncConflictRecord Newest(SyncEngine engine, string key) =>
        engine.State.Status.Conflicts.Where(c => c.Key == key).OrderByDescending(c => c.At).FirstOrDefault()
            ?? throw new InvalidOperationException("That conflict was already settled.");

    /// <summary>Both computers have the merged task (it's what they last agreed on): a side can be chosen now.</summary>
    private static bool Ready(SyncState state, SyncConflictRecord conflict) =>
        conflict.Result is null || state.LoadBase(conflict.Peer).GetValueOrDefault(conflict.Key)?.Hash == conflict.Result;

    /// <summary>Tasks are in a place that isn't read (another computer syncs there): say so, and where.</summary>
    private static string? Warning(IReadOnlyList<SyncPlaceView> places)
    {
        var unread = places.Where(p => p.Available && p.Mode is "off" or "write" && p.OtherDevices > 0).Select(p => p.Name).ToList();
        if (unread.Count == 0)
        {
            return null;
        }

        var where = unread.Count == 1 ? unread[0] : string.Join(", ", unread[..^1]) + " and " + unread[^1];
        return $"Your tasks are also in {where}, which this computer doesn't read: changes made there don't show here. " +
            "Choose below where to read from and write to; what's read is merged here (clashes are listed to settle).";
    }

    /// <summary>The engine of the place the conflict came through (the given place, else the first that lists it).</summary>
    private SyncEngine EngineWith(string key, string? place)
    {
        var uses = Uses(out _);
        if (uses.Count == 0)
        {
            throw new InvalidOperationException("Sync is off.");
        }

        foreach (var (provider, mode) in uses.Where(u => string.IsNullOrEmpty(place) || u.Provider.Id == place))
        {
            var engine = EngineFor(provider, mode);
            if (engine.State.Status.Conflicts.Any(c => c.Key == key))
            {
                return engine;
            }
        }

        throw new InvalidOperationException("That conflict was already settled.");
    }

    private int? Seen(ISyncProvider provider)
    {
        lock (_lock)
        {
            return _seen.TryGetValue(provider.Id, out var seen) && seen.Library == Library ? seen.Count : null;
        }
    }

    private Dictionary<string, SyncMode> Modes()
    {
        var modes = new Dictionary<string, SyncMode>(StringComparer.Ordinal);
        foreach (var pair in (Setting("modes") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && ParseMode(parts[1]) is { } mode && _providers.Any(p => p.Id == parts[0]))
            {
                modes[parts[0]] = mode;
            }
        }

        return modes;
    }

    /// <summary>The places in use, and how: chosen per place, else the one chosen (or the first available) for both.</summary>
    private List<(ISyncProvider Provider, SyncMode Mode)> Uses(out string reason)
    {
        if (Choice == "off")
        {
            reason = "Sync is off.";
            return [];
        }

        var modes = Modes();
        if (modes.Count > 0)
        {
            var chosen = _providers.Where(p => modes.GetValueOrDefault(p.Id) != SyncMode.None).ToList();
            var here = chosen.Where(p => p.Check(_vault.RootPath).Available).Select(p => (p, modes[p.Id])).ToList();
            reason = here.Count > 0 ? string.Empty : chosen.Count == 0 ? "Every place is set to off." : "None of the places chosen is available here.";
            return here;
        }

        var active = Active(out reason);
        return active is null ? [] : [(active, SyncMode.Both)];
    }

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

    private void Save(string name, string value) => Save(new Dictionary<string, string> { [name] = value });

    /// <summary>All at once, and atomically (a sync reading the settings meanwhile sees the old or the new ones).</summary>
    private void Save(Dictionary<string, string> values)
    {
        lock (_settingsLock)
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

            foreach (var (name, value) in values)
            {
                settings[name] = value;
            }

            var temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, settings.ToJsonString());
            File.Move(temporary, SettingsPath, overwrite: true);
        }

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

    /// <summary>
    /// The engine for this place and library (a new one when either changed), set to how the place is used now (the
    /// same engine, so a sync running through it is never cut off).
    /// </summary>
    private SyncEngine EngineFor(ISyncProvider provider, SyncMode mode)
    {
        var library = Library;
        var key = $"{provider.Check(_vault.RootPath).Detail}|{library}";
        lock (_lock)
        {
            if (!_engines.TryGetValue(provider.Id, out var current) || current.Key != key)
            {
                // The one it replaces isn't disposed: a sync may still be running through it.
                current = (key, new SyncEngine(_vault, provider.CreateRemote(_vault.RootPath, library), StateFor(provider), Path.Combine(_dir, provider.Id + ".lock"), _time));
                _engines[provider.Id] = current;
            }

            current.Engine.Mode = mode;
            return current.Engine;
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
