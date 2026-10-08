using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TodoTracker.Core.Vault;

/// <summary>
/// The task board stored as an Obsidian-compatible markdown vault (one file per top-level task; see
/// <c>docs/vault-format.md</c>). The files are the source of truth and the app is a careful guest:
/// <list type="bullet">
/// <item>Reading never writes. Hand-written files get stable ids derived from their path and are only rewritten
/// when the app changes that task (or reacts to an edit, e.g. checking a step unlocks the next one).</item>
/// <item>A change rewrites only the tasks it touched. Saves are all-or-nothing: every file is checked against what the
/// app last read (by content) before anything is written, and a failure part-way puts every file back.</item>
/// <item>Several processes (the app, the <c>tt</c> CLI, an MCP server) can share a vault: each change runs under a
/// per-vault lock kept in local app data (never in the synced folder) and re-reads the disk first.</item>
/// </list>
/// </summary>
public sealed partial class VaultBoardStore : IBoardStore, IDisposable
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    private readonly VaultOptions _options;
    private readonly string _root;
    private readonly string _lockPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _timerLock = new();
    private readonly List<VaultProblem> _problems = [];
    private readonly List<ActivityEntry> _activity = [];
    private Caches _caches = new();
    private PathIndex _index = PathIndex.Empty;
    private Dictionary<Guid, bool> _acked = [];
    private Dictionary<string, ((DateTime Time, long Size) Stamp, DateTimeOffset Seen)> _firstSeen = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<Guid, bool>? _mutationBaseline;
    private volatile bool _reactionPending;
    private List<ConfigGroup> _dormantGroups = [];
    private FileSystemWatcher? _watcher;
    private Timer? _rescan;
    private Timer? _debounce;
    private TaskBoard _board = TaskBoard.CreateEmpty();
    private string _configText = string.Empty;
    private string _stateText = string.Empty;
    private long _activityLength;
    private volatile bool _dirty = true;
    private volatile bool _forceContent;
    private bool _loadedOnce;
    private bool _disposed;

    private VaultBoardStore(VaultOptions options)
    {
        _options = options;
        _root = Path.GetFullPath(options.Root);
        _lockPath = VaultFiles.LockPath(options.LockDirectory ?? VaultFiles.DefaultLockDirectory(), _root);
        HistoryPath = Path.ChangeExtension(
            VaultFiles.LockPath(options.HistoryDirectory ?? (options.LockDirectory is { } locks ? Path.Combine(locks, "history") : DefaultHistoryDirectory()), _root),
            ".git");
    }

    /// <summary>This vault's private history repository (outside the vault, on this device).</summary>
    public string HistoryPath { get; }

    public static string DefaultHistoryDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "TodoTracker", "history");

    public event EventHandler? Changed;

    public string RootPath => _root;

    /// <summary>The format guide written to <c>AGENTS.md</c> (for AI tools that work with the files directly).</summary>
    public static string Guide => VaultGuide.Text.ReplaceLineEndings("\n");

    /// <summary>Files that couldn't be read (their last good version stays visible and they are never overwritten).</summary>
    public IReadOnlyList<VaultProblem> Problems
    {
        get
        {
            lock (_problems)
            {
                return _legacyProblem is null ? _problems.ToList() : [.. _problems, _legacyProblem];
            }
        }
    }

    /// <summary>An old board.json that couldn't be brought in (its tasks aren't here; the file is left as it is).</summary>
    private VaultProblem? _legacyProblem;

    private DateTimeOffset Now => _options.Time.GetUtcNow();

    private string MetaDir => Path.Combine(_root, VaultFiles.MetaFolder);

    public static VaultBoardStore Open(VaultOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var store = new VaultBoardStore(options);
        try
        {
            store.Initialize();
        }
        catch
        {
            store.Dispose();
            throw;
        }

        return store;
    }

    /// <summary>Something may have changed on disk; the next read re-scans the vault (and re-reads file contents).</summary>
    public void MarkDirty()
    {
        _dirty = true;
        _forceContent = true;
    }

    /// <summary>Vault-relative path (forward slashes) of the file holding the task (its top-level task's file).</summary>
    public string? PathOf(Guid id)
    {
        var index = _index;
        return index.Paths.GetValueOrDefault(index.RootOf.GetValueOrDefault(id, id));
    }

    public async Task<T> ReadAsync<T>(Func<TaskBoard, T> read, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        bool changed;
        T result;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            changed = await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);
            result = read(_board);
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    public Task<T> UpdateAsync<T>(Func<TaskBoard, T> mutate, CancellationToken cancellationToken = default) =>
        UpdateAsync(mutate, afterCommit: null, cancellationToken);

    /// <summary>Runs <paramref name="action"/> while no process can change the vault (e.g. to snapshot it).</summary>
    internal async Task<T> WithLockAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var fileLock = await VaultFiles.AcquireLockAsync(_lockPath, LockTimeout, cancellationToken).ConfigureAwait(false);
            return action();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Replaces the file of the task's top-level task with <paramref name="text"/> (an earlier version).</summary>
    internal async Task RestoreRootFileAsync(Guid taskId, string text, Actor actor, string summary)
    {
        // One change: the audit entry is saved, then (still under the lock) the file is put back.
        await UpdateAsync(
            b =>
            {
                var root = b.Get(taskId).Root;
                b.LogExternal(root.Id, ActivityKind.Updated, $"{summary} of \"{root.Title}\"", actor, Now);
                return _caches.Paths[root.Id];
            },
            rel =>
            {
                VaultFiles.WriteAtomic(VaultFiles.Full(_root, rel), text);
                MarkDirty();
            }).ConfigureAwait(false);
        await ReadAsync(_ => 0).ConfigureAwait(false);
    }

    /// <summary>Applies a change and saves it; <paramref name="afterCommit"/> runs (still under the lock) once it's saved.</summary>
    internal async Task<T> UpdateAsync<T>(Func<TaskBoard, T> mutate, Action<T>? afterCommit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        T result;
        var changed = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var fileLock = await VaultFiles.AcquireLockAsync(_lockPath, LockTimeout, cancellationToken).ConfigureAwait(false);
            for (var attempt = 1; ; attempt++)
            {
                if (_dirty || _reactionPending || DiskDiffers())
                {
                    changed |= Reload();
                }

                var snapshot = BoardSerializer.Serialize(_board);
                try
                {
                    // Only what this change did to completion is "handled"; edits still settling stay pending.
                    _mutationBaseline = _board.AllItems().ToDictionary(i => i.Id, i => i.IsDone);
                    result = mutate(_board);
                    changed |= Persist();
                    afterCommit?.Invoke(result);
                    break;
                }
                catch (VaultConflictException) when (attempt < 3)
                {
                    // Someone saved a file between our read and our write (nothing was written): re-read, re-apply.
                    _board = BoardSerializer.Deserialize(snapshot);
                    MarkDirty();
                }
                catch
                {
                    _board = BoardSerializer.Deserialize(snapshot);
                    MarkDirty();
                    throw;
                }
                finally
                {
                    _mutationBaseline = null;
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    public void Dispose()
    {
        lock (_timerLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _watcher?.Dispose();
            _rescan?.Dispose();
            _debounce?.Dispose();
        }

        // _gate is intentionally not disposed: a call that is still finishing must be able to release it.
    }

    // ---- Startup ------------------------------------------------------------------------

    private void Initialize()
    {
        using (VaultFiles.AcquireLock(_lockPath, LockTimeout))
        {
            Directory.CreateDirectory(_root);
            var fresh = !File.Exists(Path.Combine(MetaDir, "config.json")) && !EnumerateTaskFiles().Any();
            if (fresh && _options.LegacyBoardPath is { } legacy && ReadLegacy(legacy) is { } legacyBoard)
            {
                Migrate(legacy, legacyBoard);
            }
            else if (fresh)
            {
                // Seed the default tabs in this order (folders alone would sort alphabetically). Their ids come from the
                // folder name, like any folder's: two new vaults that later sync share the same tabs.
                var seeded = new TaskBoard();
                var config = new ConfigDocument(2, seeded.Groups.Select(g => new ConfigGroup(VaultText.StableGuid("group|" + g.Name.ToUpperInvariant()), g.Name, g.Color)).ToList(), [], []);
                foreach (var group in seeded.Groups)
                {
                    Directory.CreateDirectory(Path.Combine(_root, group.Name));
                }

                VaultFiles.WriteAtomic(Path.Combine(MetaDir, "config.json"), JsonSerializer.Serialize(config, Json) + "\n");
            }

            var guide = Path.Combine(_root, "AGENTS.md");
            var existing = File.Exists(guide) ? File.ReadAllText(guide) : null;
            // A new vault gets the guide; a folder of existing notes isn't changed (only an older guide of ours is updated).
            if ((existing is null && fresh) || (existing is not null && existing.StartsWith("<!-- todo-tracker-guide v", StringComparison.Ordinal) && !existing.StartsWith(VaultGuide.Marker, StringComparison.Ordinal)))
            {
                VaultFiles.WriteAtomic(guide, Guide + "\n");
            }

            PurgeOldTrash();
            Reload();
            if (!fresh && _options.ImportLegacyIntoExisting && _options.LegacyBoardPath is { } older && ReadLegacy(older) is { } olderBoard)
            {
                ImportBeside(older, olderBoard);
            }
        }

        if (_options.Watch)
        {
            StartWatching();
        }
    }

    /// <summary>An old board file, or null when there's none or it can't be read (then it's left where it is, untouched).</summary>
    private TaskBoard? ReadLegacy(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return BoardSerializer.Deserialize(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            // Said where problems are shown (the dashboard's "needs fixing" line), not skipped in silence.
            _legacyProblem = new VaultProblem(path, $"These tasks couldn't be brought into the tasks folder: {ex.Message}");
            return null;
        }
    }

    private void Migrate(string legacyPath, TaskBoard legacy)
    {
        _board = legacy;
        var folders = new Dictionary<Guid, string>();
        foreach (var group in _board.Groups)
        {
            var folder = VaultFiles.UniqueStem(VaultFiles.SafeFolderName(group.Name), f => folders.Values.Any(v => string.Equals(v, f, StringComparison.OrdinalIgnoreCase)));
            folders[group.Id] = folder;
            Directory.CreateDirectory(Path.Combine(_root, folder));
        }

        _caches = new Caches { GroupFolders = folders };
        _loadedOnce = true;
        Persist();
        SetAside(legacyPath);
        _loadedOnce = false;
    }

    /// <summary>
    /// The tasks an app kept on its own before it used this folder (the Mac app's board.json) when the folder already
    /// has tasks (another computer's, synced): added beside them, never replacing any (a task already here is kept as
    /// it is), into the tab of the same name (else the first one). Then the file is set aside.
    /// </summary>
    private void ImportBeside(string legacyPath, TaskBoard legacy)
    {
        foreach (var root in legacy.Items.ToList())
        {
            if (_board.HasAnyId(root))
            {
                continue;
            }

            var tab = legacy.Groups.FirstOrDefault(g => g.Id == root.GroupId)?.Name;
            var group = _board.Groups.FirstOrDefault(g => string.Equals(g.Name, tab, StringComparison.OrdinalIgnoreCase)) ?? _board.Groups[0];
            _board.AttachLoaded(root, group.Id);
        }

        Persist();
        SetAside(legacyPath);
    }

    /// <summary>The old board file and its backups are renamed <c>.migrated</c>: kept, and never read again.</summary>
    private static void SetAside(string legacyPath)
    {
        foreach (var path in new[] { legacyPath, legacyPath + ".bak", legacyPath + ".restoring" })
        {
            if (File.Exists(path))
            {
                File.Move(path, path + ".migrated", overwrite: true);
            }
        }
    }

    private void StartWatching()
    {
        _watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        FileSystemEventHandler onChange = (_, e) => OnFileEvent(e.FullPath);
        _watcher.Changed += onChange;
        _watcher.Created += onChange;
        _watcher.Deleted += onChange;
        _watcher.Renamed += (_, e) => OnFileEvent(e.FullPath);
        _watcher.Error += (_, _) => OnFileEvent(_root, contentCheck: true);
        _watcher.EnableRaisingEvents = true;

        // Cloud folders and network shares can miss notifications (or keep timestamps): re-check contents now and then.
        _rescan = new Timer(_ => OnFileEvent(_root, contentCheck: true), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private void OnFileEvent(string path, bool contentCheck = false)
    {
        if (path != _root)
        {
            var rel = VaultFiles.Relative(_root, path);
            var hidden = rel.Split('/').Any(s => s.StartsWith('.') && !string.Equals(s, VaultFiles.MetaFolder, StringComparison.OrdinalIgnoreCase));
            if (hidden || rel.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        ScheduleRefresh(TimeSpan.FromMilliseconds(300), contentCheck);
    }

    private void ScheduleRefresh(TimeSpan delay, bool contentCheck)
    {
        _dirty = true;
        _forceContent |= contentCheck;
        lock (_timerLock)
        {
            if (_disposed || !_options.Watch)
            {
                return;
            }

            _debounce ??= new Timer(_ => _ = RefreshInBackgroundAsync());
            _debounce.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task RefreshInBackgroundAsync()
    {
        try
        {
            if (!_disposed)
            {
                await ReadAsync(_ => 0).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or InvalidOperationException)
        {
            // The next read retries.
        }
    }

    // ---- Reading the vault --------------------------------------------------------------

    /// <summary>Re-reads the vault if it may have changed. Returns whether the board changed.</summary>
    private async Task<bool> RefreshIfNeededAsync(CancellationToken cancellationToken)
    {
        // Without a watcher every read checks the disk; with one, only after a change notification.
        if (_options.Watch && !_dirty)
        {
            return false;
        }

        if (!_forceContent && !_reactionPending && !DiskDiffers())
        {
            _dirty = false;
            return false;
        }

        using var fileLock = await VaultFiles.AcquireLockAsync(_lockPath, LockTimeout, cancellationToken).ConfigureAwait(false);
        return Reload();
    }

    /// <summary>Cheap check (names, sizes, times) for changes since the last read.</summary>
    private bool DiskDiffers()
    {
        var caches = _caches;
        var seen = 0;
        foreach (var file in EnumerateTaskFiles())
        {
            seen++;
            if (!caches.Files.TryGetValue(VaultFiles.Relative(_root, file), out var state) || state.Stamp != Stamp(file))
            {
                return true;
            }
        }

        if (seen != caches.Files.Count || !TopFolders().ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(caches.GroupFolders.Values))
        {
            return true;
        }

        return ReadOrEmpty(Path.Combine(MetaDir, "config.json")) != _configText
            || ReadOrEmpty(Path.Combine(MetaDir, "state.json")) != _stateText
            || FileLength(Path.Combine(MetaDir, "activity.jsonl")) != _activityLength;
    }

    /// <summary>Rebuilds the board from disk (caller holds the lock). Returns whether anything changed.</summary>
    private bool Reload()
    {
        var forceContent = _forceContent;
        _dirty = false;
        _forceContent = false;
        var initial = !_loadedOnce;
        _loadedOnce = true;
        var before = BoardSerializer.Serialize(_board);
        var oldBoard = _board;
        var oldCaches = _caches;
        var config = ReadConfig();
        var folders = TopFolders();

        // Read every task file (unchanged files come from the cache).
        var files = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        var parsedByFolder = folders.ToDictionary(f => f, _ => new List<(string Rel, ParsedTaskFile Parsed)>(), StringComparer.OrdinalIgnoreCase);
        var problems = new List<VaultProblem>();
        foreach (var folder in folders)
        {
            foreach (var file in EnumerateTaskFiles(Path.Combine(_root, folder)))
            {
                var rel = VaultFiles.Relative(_root, file);
                var state = ReadFile(file, rel, oldCaches, forceContent);
                files[rel] = state;
                if (state.Error is { } error)
                {
                    problems.Add(new VaultProblem(rel, error));
                }

                if (state.Good is not null)
                {
                    parsedByFolder[folder].Add((rel, ParseText(state.Good, rel)));
                }
            }
        }

        // Groups: config order; a renamed folder keeps its group (recognized by the tasks inside); new folders append.
        // Config groups whose folder is missing stay dormant (a sync client may deliver the folder later).
        var groups = new List<(Guid Id, string Folder, string? Color)>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dormant = new List<ConfigGroup>();
        foreach (var g in config.Groups)
        {
            var folder = folders.FirstOrDefault(f => string.Equals(f, g.Folder, StringComparison.OrdinalIgnoreCase) && !claimed.Contains(f));
            if (folder is null && !groups.Exists(x => x.Id == g.Id))
            {
                folder = folders.FirstOrDefault(f => !claimed.Contains(f) && !config.Groups.Exists(c => string.Equals(c.Folder, f, StringComparison.OrdinalIgnoreCase))
                    && parsedByFolder[f].Exists(p => oldBoard.Find(p.Parsed.Root.Id)?.GroupId == g.Id));
            }

            if (folder is null || groups.Exists(x => x.Id == g.Id))
            {
                if (folder is null && !groups.Exists(x => x.Id == g.Id))
                {
                    dormant.Add(g);
                }

                continue;
            }

            claimed.Add(folder);
            groups.Add((g.Id, folder, g.Color));
        }

        foreach (var folder in folders.Where(f => !claimed.Contains(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            claimed.Add(folder);
            groups.Add((VaultText.StableGuid("group|" + folder.ToUpperInvariant()), folder, null));
        }

        if (groups.Count == 0)
        {
            Directory.CreateDirectory(Path.Combine(_root, "Inbox"));
            groups.Add((VaultText.StableGuid("group|INBOX"), "Inbox", null));
            parsedByFolder["Inbox"] = [];
        }

        var board = TaskBoard.CreateEmpty();
        var groupFolders = new Dictionary<Guid, string>();
        foreach (var (id, folder, color) in groups)
        {
            board.AddLoadedGroup(id, folder, color);
            groupFolders[id] = folder;
        }

        foreach (var label in config.Labels)
        {
            board.AddLoadedLabel(label.Name, label.Color);
        }

        // Tasks in the remembered order. A copied file's ids are taken: it gets ids derived from its path (stable,
        // without rewriting it); the file that had them before keeps them.
        var order = config.Order.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var paths = new Dictionary<Guid, string>();
        var fileIds = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        var layouts = new Dictionary<string, ParsedTaskFile>(StringComparer.OrdinalIgnoreCase);
        var candidates = groups.SelectMany(g => parsedByFolder[g.Folder].Select(p => (Group: g.Id, p.Rel, p.Parsed)))
            .OrderByDescending(p => oldCaches.Paths.TryGetValue(p.Parsed.Root.Id, out var old) && string.Equals(old, p.Rel, StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => p.Rel, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var attached = new List<(Guid Group, string Rel, WorkItem Root)>();
        foreach (var (groupId, rel, parsed) in candidates)
        {
            var root = parsed.Root;
            if (board.HasAnyId(root))
            {
                foreach (var item in root.SelfAndDescendants().ToList())
                {
                    if (item == root || board.Find(item.Id) is not null)
                    {
                        item.Id = VaultText.StableGuid($"{rel}|copy|{item.Id}");
                    }
                }

                parsed.BlockIds.Clear();
            }

            board.AttachLoaded(root, groupId);
            attached.Add((groupId, rel, root));
            paths[root.Id] = rel;
            fileIds[rel] = root.SelfAndDescendants().Select(i => i.Id).ToHashSet();
            layouts[rel] = parsed;
        }

        board.ReorderRoots(r => order.TryGetValue(r.Id, out var i) ? i : int.MaxValue);

        LoadActivity(board);
        LoadState(board);

        var blocked = new HashSet<Guid>();
        foreach (var (rel, state) in files)
        {
            if (state.Error is not null && paths.FirstOrDefault(p => string.Equals(p.Value, rel, StringComparison.OrdinalIgnoreCase)).Key is var id && id != Guid.Empty)
            {
                blocked.Add(id);
            }
        }

        _board = board;
        _dormantGroups = dormant;
        _caches = new Caches
        {
            Files = files,
            Paths = paths,
            GroupFolders = groupFolders,
            FileIds = fileIds,
            Layouts = layouts,
            Signatures = board.Items.ToDictionary(r => r.Id, Signature),
            Blocked = blocked,
        };
        PublishIndex();
        lock (_problems)
        {
            _problems.Clear();
            _problems.AddRange(problems);
        }

        // React to edits made outside the app (completing a step advances its sequence), once per change.
        var reacted = initial ? false : ReactToExternalEdits(files);

        // Tasks waiting for one finished or deleted outside the app (in Obsidian, by sync, or while it was closed) come
        // back with a reminder, once the files are quiet.
        if (!_reactionPending && _board.ReleaseWaiters(Now))
        {
            reacted = true;
        }
        if (initial)
        {
            _acked = board.AllItems().ToDictionary(i => i.Id, i => i.IsDone);
        }

        var changed = reacted ? Persist() : WriteMetadataIfChanged();
        return changed | BoardSerializer.Serialize(_board) != before;
    }

    /// <summary>Applies what the app would have done for edits made in the files. Returns whether the board changed.</summary>
    private bool ReactToExternalEdits(Dictionary<string, FileState> files)
    {
        var actor = new Actor(ActorKind.Vault);
        var now = Now;
        var changed = false;
        var unsettled = false;
        var logged = _board.Activity.Select(a => a.ItemId).ToHashSet();

        // Whether a task's last completion entry (from any process: the activity log is shared) already says this.
        var lastToggle = _board.Activity.Where(a => a.Kind is ActivityKind.Completed or ActivityKind.Reopened)
            .GroupBy(a => a.ItemId).ToDictionary(g => g.Key, g => g.Last().Kind);
        bool Recorded(WorkItem item) => lastToggle.GetValueOrDefault(item.Id) == (item.IsDone ? ActivityKind.Completed : ActivityKind.Reopened);

        var acked = new Dictionary<Guid, bool>();
        var seen = new Dictionary<string, ((DateTime Time, long Size) Stamp, DateTimeOffset Seen)>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _board.Items.ToList())
        {
            var rel = _caches.Paths[root.Id];
            var stamp = files[rel].Stamp;
            var firstSeen = _firstSeen.TryGetValue(rel, out var s) && s.Stamp == stamp ? s.Seen : now;
            seen[rel] = (stamp, firstSeen);
            var items = root.SelfAndDescendants().ToList();
            var flipped = items.Where(i => _acked.TryGetValue(i.Id, out var was) && was != i.IsDone).ToList();
            if (flipped.Count > 0 && now - firstSeen < _options.EditSettleTime)
            {
                // Someone may still be typing: show the edit, act on it once the file is quiet.
                unsettled = true;
                foreach (var item in items)
                {
                    if (_acked.TryGetValue(item.Id, out var was))
                    {
                        acked[item.Id] = was;
                    }
                }

                continue;
            }

            foreach (var item in items.Where(i => !_acked.ContainsKey(i.Id) && !logged.Contains(i.Id)))
            {
                _board.LogExternal(item.Id, ActivityKind.Created, $"Created \"{item.Title}\"", actor, now);
                changed = true;
            }

            // A completion cascades to subtasks (and finishing the last step completes its parent): when a related task
            // that flipped the same way is already recorded, this one was part of that change, not a new edit.
            foreach (var item in flipped)
            {
                var explained = Recorded(item) || flipped.Exists(other => other != item && other.IsDone == item.IsDone && Recorded(other)
                    && (item.Ancestors().Contains(other) || other.Ancestors().Contains(item)));
                if (explained)
                {
                    continue;
                }

                if (item.IsDone)
                {
                    _board.CompleteExternally(item, actor, now);
                }
                else
                {
                    _board.LogExternal(item.Id, ActivityKind.Reopened, $"Reopened \"{item.Title}\"", actor, now);
                }

                lastToggle[item.Id] = item.IsDone ? ActivityKind.Completed : ActivityKind.Reopened;
                changed = true;
            }

            foreach (var item in root.SelfAndDescendants())
            {
                acked[item.Id] = item.IsDone;
            }
        }

        _acked = acked;
        _firstSeen = seen;
        _reactionPending = unsettled;
        if (unsettled)
        {
            ScheduleRefresh(_options.EditSettleTime, contentCheck: false);
        }

        return changed;
    }

    private FileState ReadFile(string full, string rel, Caches caches, bool forceContent)
    {
        var stamp = Stamp(full);
        var cached = caches.Files.GetValueOrDefault(rel);
        if (cached is not null && cached.Stamp == stamp && !forceContent)
        {
            return cached;
        }

        var text = File.ReadAllText(full);
        if (cached is not null && cached.Text == text)
        {
            return cached with { Stamp = stamp };
        }

        try
        {
            ParseText(text, rel);
            return new FileState(text, text, stamp, null);
        }
        catch (VaultFormatException ex)
        {
            return new FileState(text, cached?.Good, stamp, ex.Message);
        }
    }

    private ParsedTaskFile ParseText(string text, string rel) =>
        TaskMarkdown.Parse(text, Path.GetFileNameWithoutExtension(rel), Context(rel));

    private TaskMarkdownContext Context(string rel)
    {
        var dir = Path.GetDirectoryName(rel.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/') ?? string.Empty;
        return new TaskMarkdownContext(_options.TimeZone, Now, dir, rel);
    }

    private static string Signature(WorkItem root) => $"{root.GroupId}|{BoardSerializer.SerializeItem(root)}";

    private void PublishIndex()
    {
        var rootOf = new Dictionary<Guid, Guid>();
        foreach (var root in _board.Items)
        {
            foreach (var item in root.SelfAndDescendants())
            {
                rootOf[item.Id] = root.Id;
            }
        }

        _index = new PathIndex(new Dictionary<Guid, string>(_caches.Paths), rootOf);
    }

    // ---- Writing the vault --------------------------------------------------------------

    /// <summary>
    /// Saves what changed (caller holds the lock): plans every file operation, verifies each touched file still has
    /// the content the app last read, then commits with an undo log so a failure part-way leaves the vault as it was.
    /// </summary>
    private bool Persist()
    {
        var plan = Plan();
        if (plan.IsEmpty)
        {
            return WriteMetadataIfChanged();
        }

        // Verify before touching anything: a file that changed since we read it means re-read and re-apply.
        foreach (var rel in plan.Touched)
        {
            var full = VaultFiles.Full(_root, rel);
            var known = _caches.Files.GetValueOrDefault(rel);
            if (known is null ? File.Exists(full) : !File.Exists(full) || File.ReadAllText(full) != known.Text)
            {
                throw new VaultConflictException(rel);
            }
        }

        var undo = new Stack<Action>();
        var previousCaches = _caches;
        try
        {
            foreach (var op in plan.Operations)
            {
                op(undo);
            }

            // Metadata (groups, order) describes the new state, so it is written from the new caches.
            _caches = plan.Caches;
            WriteMetadataIfChanged(undo);
        }
        catch
        {
            _caches = previousCaches;
            while (undo.Count > 0)
            {
                try
                {
                    undo.Pop()();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best effort: keep undoing the rest.
                }
            }

            _pendingStamps.Clear();
            throw;
        }

        ApplyPendingStamps();
        PublishIndex();
        if (_mutationBaseline is { } before)
        {
            foreach (var item in _board.AllItems())
            {
                if (!before.TryGetValue(item.Id, out var was) || was != item.IsDone)
                {
                    _acked[item.Id] = item.IsDone;
                }
            }
        }

        return true;
    }

    private sealed class SavePlan
    {
        public List<Action<Stack<Action>>> Operations { get; } = [];

        public HashSet<string> Touched { get; } = new(StringComparer.OrdinalIgnoreCase);

        public required Caches Caches { get; init; }

        public bool IsEmpty => Operations.Count == 0;
    }

    private SavePlan Plan()
    {
        var current = _caches;
        var next = current.Clone();
        var plan = new SavePlan { Caches = next };
        var folders = next.GroupFolders;

        // Groups: create, rename (moving files and links with them), or retire their folders.
        var renames = new List<(string From, string To)>();
        foreach (var group in _board.Groups)
        {
            var wanted = VaultFiles.SafeFolderName(group.Name);
            if (folders.TryGetValue(group.Id, out var folder))
            {
                if (!string.Equals(VaultFiles.SafeFolderName(folder), wanted, StringComparison.Ordinal))
                {
                    var target = VaultFiles.UniqueStem(wanted, f => (folders.Values.Any(v => string.Equals(v, f, StringComparison.OrdinalIgnoreCase)) || Directory.Exists(Path.Combine(_root, f))) && !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
                    renames.Add((folder, target));
                    folders[group.Id] = target;
                }
            }
            else
            {
                var target = VaultFiles.UniqueStem(wanted, f => folders.Values.Any(v => string.Equals(v, f, StringComparison.OrdinalIgnoreCase)) || Directory.Exists(Path.Combine(_root, f)));
                folders[group.Id] = target;
                plan.Operations.Add(undo => CreateFolder(target, undo));
            }
        }

        var retired = folders.Where(g => _board.Groups.All(x => x.Id != g.Key)).ToList();
        var relocations = new List<(string From, string To)>();
        foreach (var (id, folder) in retired)
        {
            folders.Remove(id);
            var target = VaultFiles.UniqueStem($"{VaultFiles.AttachmentsFolder}/{folder}", f => Directory.Exists(VaultFiles.Full(_root, f)));
            relocations.Add((folder, target));
        }

        // A renamed folder carries its task files along; a retired folder keeps only non-task files (its tasks move
        // to their new group first), so only attachment links into it are remapped.
        foreach (var (from, to) in renames)
        {
            RemapPrefix(next, from, to, includeTasks: true);
        }

        foreach (var (from, to) in relocations)
        {
            RemapPrefix(next, from, to, includeTasks: false);
        }

        foreach (var (from, to) in renames)
        {
            plan.Operations.Insert(0, undo => MoveFolder(from, to, undo));
        }

        // Tasks: write the ones that changed (or moved); a different group keeps the file name and subfolder.
        var writes = new List<(string Rel, string Text, bool Gained)>();
        var reserved = new HashSet<string>(next.Paths.Values, StringComparer.OrdinalIgnoreCase);
        foreach (var root in _board.Items)
        {
            var folder = folders[root.GroupId];
            var signature = Signature(root);
            var known = next.Paths.TryGetValue(root.Id, out var rel);
            if (!known)
            {
                rel = NewFilePath(folder, VaultFiles.SafeName(root.Title), reserved);
                reserved.Add(rel);
            }
            else if (!string.Equals(rel!.Split('/')[0], folder, StringComparison.OrdinalIgnoreCase))
            {
                var source = rel;
                var name = Path.GetFileNameWithoutExtension(rel);
                var sub = Path.GetDirectoryName(rel.Replace('/', Path.DirectorySeparatorChar))!.Replace('\\', '/');
                sub = sub.Contains('/', StringComparison.Ordinal) ? sub[(sub.IndexOf('/', StringComparison.Ordinal) + 1)..] + "/" : string.Empty;
                rel = NewFilePath(sub.Length == 0 ? folder : folder + "/" + sub.TrimEnd('/'), name, reserved);
                reserved.Add(rel);
                MoveEntry(next, source, rel);
                var target = rel;
                plan.Touched.Add(source);
                plan.Operations.Add(undo => MoveTaskFile(source, target, undo));
            }

            if (current.Blocked.Contains(root.Id))
            {
                if (current.Signatures.GetValueOrDefault(root.Id) != signature)
                {
                    throw new InvalidOperationException($"\"{rel}\" can't be read right now ({Problems.FirstOrDefault(p => p.Path == rel)?.Message}). Fix the file, then try again.");
                }

                continue;
            }

            next.Paths[root.Id] = rel!;
            if (known && current.Signatures.GetValueOrDefault(root.Id) == signature && rel == current.Paths[root.Id])
            {
                continue;
            }

            var text = TaskMarkdown.Render(root, Context(rel!), Layout(next, rel!));
            var ids = root.SelfAndDescendants().Select(i => i.Id).ToHashSet();
            var gained = !next.FileIds.TryGetValue(rel!, out var had) || !ids.IsSubsetOf(had);
            next.Signatures[root.Id] = signature;
            next.FileIds[rel!] = ids;
            if (next.Files.GetValueOrDefault(rel!)?.Text != text)
            {
                writes.Add((rel!, text, gained));
            }
        }

        // Files that gain tasks are written before files that lose them: a crash in between duplicates, never loses.
        foreach (var (rel, text, _) in writes.OrderByDescending(w => w.Gained))
        {
            plan.Touched.Add(rel);
            next.Files[rel] = new FileState(text, text, default, null);
            next.Layouts.Remove(rel);
            plan.Operations.Add(undo => WriteTaskFile(rel, text, undo));
        }

        // Top-level tasks that are gone (deleted, or now a subtask) go to the trash.
        var live = _board.Items.Select(i => i.Id).ToHashSet();
        foreach (var (id, rel) in next.Paths.Where(p => !live.Contains(p.Key)).ToList())
        {
            if (current.Blocked.Contains(id))
            {
                throw new InvalidOperationException($"\"{rel}\" can't be read right now, so it can't be removed. Fix the file first.");
            }

            if (_board.Find(id) is not null && HasFileOnlyContent(rel))
            {
                throw new InvalidOperationException($"\"{rel}\" has content that only a top-level task can keep (a rich version, extra properties, or sections). Move that content first.");
            }

            next.Paths.Remove(id);
            next.Signatures.Remove(id);
            next.FileIds.Remove(rel);
            next.Files.Remove(rel);
            next.Layouts.Remove(rel);
            plan.Touched.Add(rel);
            plan.Operations.Add(undo => TrashTaskFile(rel, undo));
        }

        foreach (var (from, to) in relocations)
        {
            plan.Operations.Add(undo => RetireFolder(from, to, undo));
        }

        return plan;
    }

    /// <summary>A folder moved: task paths, cached files, and attachment links under it move along.</summary>
    private void RemapPrefix(Caches caches, string from, string to, bool includeTasks)
    {
        static string? Remap(string path, string from, string to) =>
            path.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase) ? to + path[from.Length..] : null;

        foreach (var (id, rel) in includeTasks ? caches.Paths.ToList() : [])
        {
            if (Remap(rel, from, to) is { } moved)
            {
                MoveEntry(caches, rel, moved);
                caches.Paths[id] = moved;
            }
        }

        foreach (var item in _board.AllItems())
        {
            foreach (var attachment in item.Attachments)
            {
                if (Remap(attachment.Path, from, to) is { } moved)
                {
                    attachment.Path = moved;
                }
            }
        }
    }

    private static void MoveEntry(Caches caches, string from, string to)
    {
        if (caches.Files.Remove(from, out var state))
        {
            caches.Files[to] = state;
        }

        if (caches.FileIds.Remove(from, out var ids))
        {
            caches.FileIds[to] = ids;
        }

        if (caches.Layouts.Remove(from, out var layout))
        {
            caches.Layouts[to] = layout;
        }
    }

    private ParsedTaskFile? Layout(Caches caches, string rel)
    {
        if (caches.Layouts.TryGetValue(rel, out var layout))
        {
            return layout;
        }

        if (caches.Files.GetValueOrDefault(rel)?.Good is not { } good)
        {
            return null;
        }

        try
        {
            layout = ParseText(good, rel);
            caches.Layouts[rel] = layout;
            return layout;
        }
        catch (VaultFormatException)
        {
            return null;
        }
    }

    /// <summary>Content that would be lost if the file stopped being a top-level task.</summary>
    private bool HasFileOnlyContent(string rel)
    {
        if (File.Exists(Path.ChangeExtension(VaultFiles.Full(_root, rel), ".html")))
        {
            return true;
        }

        var layout = Layout(_caches, rel);
        return layout is not null && (layout.Layout.Exists(l => l.Kind is null) || layout.Leading.Count > 0 || layout.SubtasksTrailer is not null
            || layout.NotesPreamble is not null || layout.AttachmentsPreamble is not null || layout.TimePreamble is not null
            || layout.Frontmatter.Blocks.Any(b => b.Key is null || !IsOwnedKey(b.Key, layout.IdKey)));
    }

    private static bool IsOwnedKey(string key, string idKey) =>
        string.Equals(key, idKey, StringComparison.OrdinalIgnoreCase) || TaskMarkdown.IsOwnedProperty(key);

    private string NewFilePath(string folder, string stem, HashSet<string> reserved)
    {
        var unique = VaultFiles.UniqueStem(stem, s =>
        {
            var rel = $"{folder}/{s}.md";
            return reserved.Contains(rel) || File.Exists(VaultFiles.Full(_root, rel)) || File.Exists(VaultFiles.Full(_root, $"{folder}/{s}.html"));
        });
        return $"{folder}/{unique}.md";
    }

    private void CreateFolder(string folder, Stack<Action> undo)
    {
        var full = Path.Combine(_root, folder);
        if (!Directory.Exists(full))
        {
            Directory.CreateDirectory(full);
            undo.Push(() => Directory.Delete(full));
        }
    }

    private void MoveFolder(string from, string to, Stack<Action> undo)
    {
        var source = Path.Combine(_root, from);
        var target = Path.Combine(_root, to);
        if (!Directory.Exists(source))
        {
            CreateFolder(to, undo);
            return;
        }

        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            // Case-only rename: go through a temporary name (case-insensitive file systems).
            var temp = target + ".rename-" + Guid.NewGuid().ToString("N")[..6];
            Directory.Move(source, temp);
            Directory.Move(temp, target);
        }
        else
        {
            Directory.Move(source, target);
        }

        undo.Push(() => Directory.Move(target, source));
    }

    /// <summary>A deleted group's folder: whatever is left in it (images, other notes) moves under _attachments.</summary>
    private void RetireFolder(string from, string to, Stack<Action> undo)
    {
        var source = Path.Combine(_root, from);
        if (!Directory.Exists(source))
        {
            return;
        }

        if (!Directory.EnumerateFileSystemEntries(source).Any())
        {
            Directory.Delete(source);
            undo.Push(() => Directory.CreateDirectory(source));
            return;
        }

        var target = VaultFiles.Full(_root, to);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(source, target);
        undo.Push(() => Directory.Move(target, source));
    }

    private void WriteTaskFile(string rel, string text, Stack<Action> undo)
    {
        _options.BeforeWrite?.Invoke(rel);
        var full = VaultFiles.Full(_root, rel);
        var previous = File.Exists(full) ? File.ReadAllText(full) : null;
        VaultFiles.WriteAtomic(full, text);
        undo.Push(() =>
        {
            if (previous is null)
            {
                File.Delete(full);
            }
            else
            {
                VaultFiles.WriteAtomic(full, previous);
            }
        });
        PlanStamp(rel, full);
    }

    /// <summary>Records the stamp of a file the app just wrote, in the caches the commit will publish.</summary>
    private void PlanStamp(string rel, string full) => _pendingStamps[rel] = Stamp(full);

    private readonly Dictionary<string, (DateTime Time, long Size)> _pendingStamps = new(StringComparer.OrdinalIgnoreCase);

    private void MoveTaskFile(string from, string to, Stack<Action> undo)
    {
        var source = VaultFiles.Full(_root, from);
        var target = VaultFiles.Full(_root, to);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(source))
        {
            File.Move(source, target);
            undo.Push(() => File.Move(target, source));
        }

        var html = Path.ChangeExtension(source, ".html");
        if (File.Exists(html))
        {
            var htmlTarget = Path.ChangeExtension(target, ".html");
            File.Move(html, htmlTarget);
            undo.Push(() => File.Move(htmlTarget, html));
        }

        PlanStamp(to, target);
    }

    private void TrashTaskFile(string rel, Stack<Action> undo)
    {
        var full = VaultFiles.Full(_root, rel);
        foreach (var file in new[] { full, Path.ChangeExtension(full, ".html") })
        {
            if (File.Exists(file))
            {
                var trash = TrashPath(Path.GetFileName(file));
                File.Move(file, trash);
                undo.Push(() => File.Move(trash, file));
            }
        }
    }

    private string TrashPath(string name)
    {
        var trash = Path.Combine(MetaDir, "trash");
        Directory.CreateDirectory(trash);
        var stamp = Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var candidate = Path.Combine(trash, $"{stamp} {name}");
        for (var n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++)
        {
            candidate = Path.Combine(trash, $"{stamp} {n} {name}");
        }

        return candidate;
    }

    private void PurgeOldTrash()
    {
        var trash = Path.Combine(MetaDir, "trash");
        if (!Directory.Exists(trash))
        {
            return;
        }

        foreach (var entry in new DirectoryInfo(trash).EnumerateFileSystemInfos())
        {
            if (entry.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-30) && entry.CreationTimeUtc < DateTime.UtcNow.AddDays(-30))
            {
                try
                {
                    if (entry is DirectoryInfo dir)
                    {
                        dir.Delete(recursive: true);
                    }
                    else
                    {
                        entry.Delete();
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Try again next start.
                }
            }
        }
    }

    // ---- Settings, timer state, activity ------------------------------------------------

    private sealed record ConfigGroup(Guid Id, string Folder, string? Color);

    private sealed record ConfigLabel(string Name, string Color);

    private sealed record ConfigDocument(int SchemaVersion, List<ConfigGroup> Groups, List<ConfigLabel> Labels, List<Guid> Order);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly JsonSerializerOptions JsonLine = new(Json) { WriteIndented = false };

    private ConfigDocument ReadConfig()
    {
        var text = ReadOrEmpty(Path.Combine(MetaDir, "config.json"));
        _configText = text;
        try
        {
            var doc = text.Length == 0 ? null : JsonSerializer.Deserialize<ConfigDocument>(text, Json);
            return doc is null
                ? new ConfigDocument(2, [], [], [])
                : doc with { Groups = doc.Groups ?? [], Labels = doc.Labels ?? [], Order = doc.Order ?? [] };
        }
        catch (JsonException)
        {
            return new ConfigDocument(2, [], [], []);
        }
    }

    private bool WriteMetadataIfChanged(Stack<Action>? undo = null)
    {
        var changed = WriteIfChanged(Path.Combine(MetaDir, "config.json"), ConfigText(), ref _configText, undo);
        changed |= WriteIfChanged(Path.Combine(MetaDir, "state.json"), StateText(), ref _stateText, undo);
        changed |= AppendActivity(undo);
        return changed;
    }

    private void ApplyPendingStamps()
    {
        foreach (var (rel, stamp) in _pendingStamps)
        {
            if (_caches.Files.TryGetValue(rel, out var state))
            {
                _caches.Files[rel] = state with { Stamp = stamp };
            }
        }

        _pendingStamps.Clear();
    }

    private string ConfigText()
    {
        var folders = _caches.GroupFolders;
        var doc = new ConfigDocument(
            2,
            _board.Groups.Where(g => folders.ContainsKey(g.Id)).Select(g => new ConfigGroup(g.Id, folders[g.Id], g.Color)).Concat(_dormantGroups).ToList(),
            _board.Labels.Select(l => new ConfigLabel(l.Name, l.Color)).ToList(),
            _board.Items.Select(i => i.Id).ToList());
        return JsonSerializer.Serialize(doc, Json) + "\n";
    }

    private static bool WriteIfChanged(string path, string text, ref string known, Stack<Action>? undo)
    {
        if (text == known)
        {
            return false;
        }

        var previous = known;
        VaultFiles.WriteAtomic(path, text);
        known = text;
        undo?.Push(() =>
        {
            if (previous.Length == 0)
            {
                File.Delete(path);
            }
            else
            {
                VaultFiles.WriteAtomic(path, previous);
            }
        });
        return true;
    }

    private sealed record StateDocument(PomodoroSettings? Settings, PomodoroPhase Phase, DateTimeOffset? EndsAt, int? PausedRemainingSeconds, Guid? ItemId, int CompletedFocusCount, List<Guid>? NowOrder = null);

    private void LoadState(TaskBoard board)
    {
        var text = ReadOrEmpty(Path.Combine(MetaDir, "state.json"));
        _stateText = text;
        try
        {
            if (text.Length > 0 && JsonSerializer.Deserialize<StateDocument>(text, Json) is { } s)
            {
                board.Pomodoro = new PomodoroTimer(s.Settings);
                board.Pomodoro.Restore(s.Phase, s.EndsAt, s.PausedRemainingSeconds is { } secs ? TimeSpan.FromSeconds(secs) : null, s.ItemId is { } id && board.Find(id) is not null ? id : null, s.CompletedFocusCount);
                board.NowOrderList.Clear();
                board.NowOrderList.AddRange((s.NowOrder ?? []).Where(i => board.Find(i) is not null).Distinct());
            }
        }
        catch (JsonException)
        {
            // A damaged timer state just means an idle timer.
        }
    }

    private string StateText()
    {
        var p = _board.Pomodoro;
        var doc = new StateDocument(p.Settings, p.Phase, p.EndsAt, p.PausedRemaining is { } r ? (int)Math.Round(r.TotalSeconds) : null, p.ItemId, p.CompletedFocusCount, _board.NowOrder.Count == 0 ? null : [.. _board.NowOrder]);
        return JsonSerializer.Serialize(doc, Json) + "\n";
    }

    private sealed record ActivityLine(DateTimeOffset At, Guid ItemId, ActivityKind Kind, string Summary, ActorKind ActorKind, string? Actor);

    /// <summary>Reads only what other processes appended since the last read; a shorter file means it was replaced.</summary>
    private void LoadActivity(TaskBoard board)
    {
        var path = Path.Combine(MetaDir, "activity.jsonl");
        var length = FileLength(path);
        if (length < _activityLength)
        {
            _activity.Clear();
            _activityLength = 0;
        }

        if (length > _activityLength)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(_activityLength, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            var complete = text.LastIndexOf('\n');
            if (complete >= 0)
            {
                foreach (var line in text[..complete].Split('\n'))
                {
                    try
                    {
                        if (line.Length > 0 && JsonSerializer.Deserialize<ActivityLine>(line, JsonLine) is { } a)
                        {
                            _activity.Add(new ActivityEntry(a.At, a.ItemId, a.Kind, a.Summary, new Actor(a.ActorKind, a.Actor)));
                        }
                    }
                    catch (JsonException)
                    {
                        // A torn line (crash mid-append) is skipped.
                    }
                }

                _activityLength += Encoding.UTF8.GetByteCount(text[..(complete + 1)]);
            }
        }

        foreach (var entry in _activity)
        {
            board.AddLoadedActivity(entry);
        }
    }

    private bool AppendActivity(Stack<Action>? undo)
    {
        var pending = _board.Activity.Skip(_activity.Count).ToList();
        if (pending.Count == 0)
        {
            return false;
        }

        var path = Path.Combine(MetaDir, "activity.jsonl");
        Directory.CreateDirectory(MetaDir);
        var sb = new StringBuilder();
        var length = FileLength(path);
        if (length > 0)
        {
            // Never glue a new entry onto a torn last line.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() != '\n')
            {
                sb.Append('\n');
            }
        }

        foreach (var a in pending)
        {
            sb.Append(JsonSerializer.Serialize(new ActivityLine(a.At, a.ItemId, a.Kind, a.Summary, a.Actor.Kind, a.Actor.Name), JsonLine)).Append('\n');
        }

        File.AppendAllText(path, sb.ToString());
        undo?.Push(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            stream.SetLength(length);
        });
        _activity.AddRange(pending);
        _activityLength = FileLength(path);
        return true;
    }

    // ---- File system helpers ------------------------------------------------------------

    private List<string> TopFolders() =>
        Directory.Exists(_root)
            ? Directory.EnumerateDirectories(_root).Select(Path.GetFileName).OfType<string>().Where(IsGroupFolder).ToList()
            : [];

    private static bool IsGroupFolder(string name) => name.Length > 0 && name[0] is not ('.' or '_');

    private IEnumerable<string> EnumerateTaskFiles() => TopFolders().SelectMany(f => EnumerateTaskFiles(Path.Combine(_root, f)));

    private static IEnumerable<string> EnumerateTaskFiles(string folder)
    {
        if (!Directory.Exists(folder))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.md"))
        {
            if (!Path.GetFileName(file).StartsWith('.'))
            {
                yield return file;
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(folder))
        {
            if (IsGroupFolder(Path.GetFileName(dir)))
            {
                foreach (var file in EnumerateTaskFiles(dir))
                {
                    yield return file;
                }
            }
        }
    }

    private static (DateTime Time, long Size) Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (info.LastWriteTimeUtc, info.Length) : (DateTime.MinValue, -1);
    }

    private static long FileLength(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static string ReadOrEmpty(string path) => File.Exists(path) ? File.ReadAllText(path) : string.Empty;

    /// <summary>What we last saw of a file: its text, the last version that parsed, and why it doesn't parse now.</summary>
    private sealed record FileState(string Text, string? Good, (DateTime Time, long Size) Stamp, string? Error);

    /// <summary>Everything the store remembers about the files; replaced as a whole after a successful save.</summary>
    private sealed class Caches
    {
        public Dictionary<string, FileState> Files { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<Guid, string> Paths { get; init; } = [];

        public Dictionary<Guid, string> GroupFolders { get; init; } = [];

        public Dictionary<string, HashSet<Guid>> FileIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, ParsedTaskFile> Layouts { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<Guid, string> Signatures { get; init; } = [];

        public HashSet<Guid> Blocked { get; init; } = [];

        public Caches Clone() => new()
        {
            Files = new(Files, StringComparer.OrdinalIgnoreCase),
            Paths = new(Paths),
            GroupFolders = new(GroupFolders),
            FileIds = new(FileIds, StringComparer.OrdinalIgnoreCase),
            Layouts = new(Layouts, StringComparer.OrdinalIgnoreCase),
            Signatures = new(Signatures),
            Blocked = [.. Blocked],
        };
    }

    /// <summary>Immutable lookup published after each load/save, so <see cref="PathOf"/> never races a change.</summary>
    private sealed record PathIndex(IReadOnlyDictionary<Guid, string> Paths, IReadOnlyDictionary<Guid, Guid> RootOf)
    {
        public static PathIndex Empty { get; } = new(new Dictionary<Guid, string>(), new Dictionary<Guid, Guid>());
    }

    private sealed class VaultConflictException(string path) : IOException($"\"{path}\" changed while saving.");
}
