using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TodoTracker.Core.Vault;

/// <summary>
/// The task board stored as an Obsidian-compatible markdown vault (one file per top-level task; see
/// <c>docs/vault-format.md</c>). The files are the source of truth: edits made outside the app (Obsidian, editors,
/// agents, sync clients) are picked up, normalized, and attributed; app changes rewrite only the files they touch.
/// Several processes (the app, the <c>tt</c> CLI, an MCP server) can share a vault: each change runs under a
/// per-vault lock kept in local app data (never in the synced folder) and re-reads the disk first.
/// </summary>
public sealed partial class VaultBoardStore : IBoardStore, IDisposable
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    private readonly VaultOptions _options;
    private readonly string _root;
    private readonly string _lockPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, FileState> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, string> _paths = [];
    private readonly Dictionary<Guid, string> _groupFolders = [];
    private readonly Dictionary<Guid, string> _blockedRenders = [];
    private readonly List<VaultProblem> _problems = [];
    private FileSystemWatcher? _watcher;
    private Timer? _rescan;
    private Timer? _debounce;
    private TaskBoard _board = TaskBoard.CreateEmpty();
    private string _configText = string.Empty;
    private string _stateText = string.Empty;
    private long _activityLength;
    private int _activityPersisted;
    private volatile bool _dirty = true;
    private bool _loadedOnce;
    private bool _disposed;
    private readonly Lock _debounceLock = new();

    private VaultBoardStore(VaultOptions options)
    {
        _options = options;
        _root = Path.GetFullPath(options.Root);
        _lockPath = VaultFiles.LockPath(options.LockDirectory ?? VaultFiles.DefaultLockDirectory(), _root);
    }

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
                return _problems.ToList();
            }
        }
    }

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

    /// <summary>Something may have changed on disk; the next read re-scans the vault.</summary>
    public void MarkDirty() => _dirty = true;

    /// <summary>Vault-relative path (forward slashes) of the file holding the task (its top-level task's file).</summary>
    public string? PathOf(Guid id)
    {
        lock (_paths)
        {
            var rootId = _board.Find(id)?.Root.Id ?? id;
            return _paths.GetValueOrDefault(rootId);
        }
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
            changed = RefreshIfNeeded(force: !_options.Watch);
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

    public async Task<T> UpdateAsync<T>(Func<TaskBoard, T> mutate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        T result;
        var changed = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var fileLock = VaultFiles.AcquireLock(_lockPath, LockTimeout);
            for (var attempt = 1; ; attempt++)
            {
                if (_dirty || DiskDiffers())
                {
                    changed |= Reload(force: true);
                }

                var snapshot = BoardSerializer.Serialize(_board);
                try
                {
                    result = mutate(_board);
                    changed |= Persist();
                    break;
                }
                catch (VaultConflictException) when (attempt < 3)
                {
                    // Someone saved a file between our read and our write: re-read and apply the change again.
                    _board = BoardSerializer.Deserialize(snapshot);
                    _dirty = true;
                }
                catch
                {
                    _board = BoardSerializer.Deserialize(snapshot);
                    throw;
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
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher?.Dispose();
        _rescan?.Dispose();
        _debounce?.Dispose();
        _gate.Dispose();
    }

    // ---- Startup ------------------------------------------------------------------------

    private void Initialize()
    {
        using (VaultFiles.AcquireLock(_lockPath, LockTimeout))
        {
            Directory.CreateDirectory(_root);
            var fresh = !File.Exists(Path.Combine(MetaDir, "config.json")) && !EnumerateTaskFiles().Any();
            if (fresh && _options.LegacyBoardPath is { } legacy && File.Exists(legacy))
            {
                Migrate(legacy);
            }
            else if (fresh)
            {
                // Seed the default tabs in this order (folders alone would sort alphabetically).
                var seeded = new TaskBoard();
                foreach (var group in seeded.Groups)
                {
                    Directory.CreateDirectory(Path.Combine(_root, group.Name));
                    _groupFolders[group.Id] = group.Name;
                }

                _board = TaskBoard.CreateEmpty();
                foreach (var group in seeded.Groups)
                {
                    _board.AddLoadedGroup(group.Id, group.Name, group.Color);
                }

                WriteConfigIfChanged();
                _board = TaskBoard.CreateEmpty();
                _groupFolders.Clear();
            }

            var guide = Path.Combine(_root, "AGENTS.md");
            if (!File.Exists(guide) || File.ReadAllText(guide).StartsWith("<!-- todo-tracker-guide v", StringComparison.Ordinal) && !File.ReadAllText(guide).StartsWith(VaultGuide.Marker, StringComparison.Ordinal))
            {
                VaultFiles.WriteAtomic(guide, VaultGuide.Text.ReplaceLineEndings("\n") + "\n");
            }

            Reload(force: true);
        }

        if (_options.Watch)
        {
            StartWatching();
        }
    }

    private void Migrate(string legacyPath)
    {
        var legacy = BoardSerializer.Deserialize(File.ReadAllText(legacyPath));
        _board = legacy;
        foreach (var group in legacy.Groups)
        {
            var folder = VaultFiles.UniqueStem(VaultFiles.SafeName(group.Name, 60), f => _groupFolders.ContainsValue(f));
            _groupFolders[group.Id] = folder;
            Directory.CreateDirectory(Path.Combine(_root, folder));
        }

        Persist();
        File.Move(legacyPath, legacyPath + ".migrated", overwrite: true);
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
        _watcher.Error += (_, _) => OnFileEvent(_root);
        _watcher.EnableRaisingEvents = true;

        // Cloud folders and network shares can miss notifications: also re-scan now and then.
        _rescan = new Timer(_ => OnFileEvent(_root), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private void OnFileEvent(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.StartsWith('.') && path != _root && !path.Contains(VaultFiles.MetaFolder, StringComparison.Ordinal))
        {
            return;
        }

        _dirty = true;
        lock (_debounceLock)
        {
            if (_disposed)
            {
                return;
            }

            _debounce ??= new Timer(_ => _ = RefreshInBackgroundAsync());
            _debounce.Change(TimeSpan.FromMilliseconds(300), Timeout.InfiniteTimeSpan);
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
    private bool RefreshIfNeeded(bool force)
    {
        if (!force && !_dirty)
        {
            return false;
        }

        if (!force && !DiskDiffers())
        {
            _dirty = false;
            return false;
        }

        if (force && !DiskDiffers())
        {
            return false;
        }

        using var fileLock = VaultFiles.AcquireLock(_lockPath, LockTimeout);
        return Reload(force: true);
    }

    /// <summary>Cheap check (names, sizes, times) for changes since the last read.</summary>
    private bool DiskDiffers()
    {
        var seen = 0;
        foreach (var file in EnumerateTaskFiles())
        {
            seen++;
            var rel = VaultFiles.Relative(_root, file);
            if (!_files.TryGetValue(rel, out var state) || state.Stamp != Stamp(file))
            {
                return true;
            }
        }

        if (seen != _files.Count || !TopFolders().ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(_groupFolders.Values))
        {
            return true;
        }

        return ReadOrEmpty(Path.Combine(MetaDir, "config.json")) != _configText
            || ReadOrEmpty(Path.Combine(MetaDir, "state.json")) != _stateText
            || FileLength(Path.Combine(MetaDir, "activity.jsonl")) != _activityLength;
    }

    /// <summary>Rebuilds the board from disk (caller holds the lock). Returns whether anything changed.</summary>
    private bool Reload(bool force)
    {
        if (!force && !_dirty)
        {
            return false;
        }

        _dirty = false;
        var initial = !_loadedOnce;
        _loadedOnce = true;
        var before = BoardSerializer.Serialize(_board);
        var oldBoard = _board;
        var oldPaths = new Dictionary<Guid, string>(_paths);
        var config = ReadConfig();
        var folders = TopFolders();

        // Groups: config order; renamed folders keep their group (matched by the tasks inside); new folders append.
        var groups = new List<(Guid Id, string Folder, string? Color)>();
        var unmatched = folders.Where(f => !config.Groups.Exists(g => string.Equals(g.Folder, f, StringComparison.OrdinalIgnoreCase))).ToList();
        var parsedByFolder = new Dictionary<string, List<(string Rel, ParsedTaskFile Parsed)>>(StringComparer.OrdinalIgnoreCase);
        var newFiles = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<VaultProblem>();
        _blockedRenders.Clear();
        foreach (var folder in folders)
        {
            parsedByFolder[folder] = [];
            foreach (var file in EnumerateTaskFiles(Path.Combine(_root, folder)))
            {
                var rel = VaultFiles.Relative(_root, file);
                var state = ReadFile(file, rel);
                newFiles[rel] = state;
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

        foreach (var g in config.Groups)
        {
            var folder = folders.FirstOrDefault(f => string.Equals(f, g.Folder, StringComparison.OrdinalIgnoreCase));
            if (folder is null)
            {
                // Folder renamed outside the app? Recognize it by the tasks that used to live in this group.
                folder = unmatched.FirstOrDefault(f => parsedByFolder[f].Exists(p => oldBoard.Find(p.Parsed.Root.Id)?.GroupId == g.Id));
                if (folder is null)
                {
                    continue;
                }

                unmatched.Remove(folder);
            }

            groups.Add((g.Id, folder, g.Color));
        }

        foreach (var folder in unmatched.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            groups.Add((Guid.NewGuid(), folder, null));
        }

        if (groups.Count == 0)
        {
            Directory.CreateDirectory(Path.Combine(_root, "Inbox"));
            groups.Add((Guid.NewGuid(), "Inbox", null));
            parsedByFolder["Inbox"] = [];
        }

        var board = TaskBoard.CreateEmpty();
        _groupFolders.Clear();
        foreach (var (id, folder, color) in groups)
        {
            board.AddLoadedGroup(id, folder, color);
            _groupFolders[id] = folder;
        }

        foreach (var label in config.Labels)
        {
            board.AddLoadedLabel(label.Name, label.Color);
        }

        // Tasks: a file whose ids are taken (a copy) is re-keyed; the file that had the id before keeps it.
        var toWrite = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newPaths = new Dictionary<Guid, string>();
        var ordered = groups.SelectMany(g => parsedByFolder[g.Folder].Select(p => (Group: g.Id, p.Rel, p.Parsed)))
            .OrderByDescending(p => oldPaths.TryGetValue(p.Parsed.Root.Id, out var old) && string.Equals(old, p.Rel, StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => p.Rel, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var (groupId, rel, parsed) in ordered)
        {
            var root = parsed.Root;
            if (board.HasAnyId(root))
            {
                foreach (var item in root.SelfAndDescendants().Where(i => board.Find(i.Id) is not null || i == root))
                {
                    item.Id = Guid.NewGuid();
                }

                parsed.BlockIds.Clear();
                parsed.NeedsWrite = true;
            }

            board.AttachLoaded(root, groupId);
            newPaths[root.Id] = rel;
            if (newFiles[rel].Error is null && parsed.NeedsWrite)
            {
                toWrite.Add(rel);
            }
        }

        LoadState(board);
        LoadActivity(board);

        if (!initial)
        {
            ApplyExternalEdits(oldBoard, board, newPaths, toWrite);
        }

        // Swap in the new state.
        _board = board;
        lock (_paths)
        {
            _paths.Clear();
            foreach (var (id, rel) in newPaths)
            {
                _paths[id] = rel;
            }
        }

        _files.Clear();
        foreach (var (rel, state) in newFiles)
        {
            _files[rel] = state;
            if (state.Error is not null && newPaths.FirstOrDefault(p => string.Equals(p.Value, rel, StringComparison.OrdinalIgnoreCase)).Key is var blocked && blocked != Guid.Empty)
            {
                _blockedRenders[blocked] = Render(board.Get(blocked), rel);
            }
        }

        lock (_problems)
        {
            _problems.Clear();
            _problems.AddRange(problems);
        }

        // Normalize files that were typed by hand (ids, timestamps, sections) and record new groups/labels.
        foreach (var rel in toWrite)
        {
            var root = board.Get(newPaths.First(p => string.Equals(p.Value, rel, StringComparison.OrdinalIgnoreCase)).Key);
            WriteTaskFile(rel, Render(root, rel), verify: true);
        }

        WriteConfigIfChanged();
        AppendActivity();
        return BoardSerializer.Serialize(_board) != before;
    }

    /// <summary>Turns hand edits into what the app would have done: completing a step advances its sequence.</summary>
    private void ApplyExternalEdits(TaskBoard oldBoard, TaskBoard board, Dictionary<Guid, string> paths, HashSet<string> rewritten)
    {
        var actor = new Actor(ActorKind.Vault);
        var now = Now;
        foreach (var root in board.Items.ToList())
        {
            // Only raw edits (files the app has to normalize) are attributed, so other app instances don't log twice.
            if (!rewritten.Contains(paths[root.Id]))
            {
                continue;
            }

            foreach (var item in root.SelfAndDescendants().ToList())
            {
                var old = oldBoard.Find(item.Id);
                if (old is null)
                {
                    board.LogExternal(item.Id, ActivityKind.Created, $"Created \"{item.Title}\"", actor, now);
                }
                else if (item.IsDone && !old.IsDone)
                {
                    board.CompleteExternally(item, actor, now);
                }
                else if (!item.IsDone && old.IsDone)
                {
                    board.LogExternal(item.Id, ActivityKind.Reopened, $"Reopened \"{item.Title}\"", actor, now);
                }
            }
        }
    }

    private FileState ReadFile(string full, string rel)
    {
        var stamp = Stamp(full);
        if (_files.TryGetValue(rel, out var cached) && cached.Stamp == stamp)
        {
            return cached;
        }

        var text = File.ReadAllText(full);
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
        return new TaskMarkdownContext(_options.TimeZone, Now, dir);
    }

    private string Render(WorkItem root, string rel)
    {
        ParsedTaskFile? previous = null;
        if (_files.TryGetValue(rel, out var state) && state.Good is { } good)
        {
            try
            {
                previous = ParseText(good, rel);
            }
            catch (VaultFormatException)
            {
                previous = null;
            }
        }

        return TaskMarkdown.Render(root, Context(rel), previous);
    }

    // ---- Writing the vault --------------------------------------------------------------

    /// <summary>Writes what the mutation changed (caller holds the lock). Returns whether anything changed.</summary>
    private bool Persist()
    {
        var changed = false;
        changed |= PersistGroups();

        var live = _board.Items.Select(i => i.Id).ToHashSet();
        Dictionary<Guid, string> paths;
        lock (_paths)
        {
            paths = new Dictionary<Guid, string>(_paths);
        }

        // Moves between groups (and promotions of subtasks to top level) first, so names are allocated correctly.
        foreach (var root in _board.Items)
        {
            var folder = _groupFolders[root.GroupId];
            if (paths.TryGetValue(root.Id, out var rel))
            {
                if (!string.Equals(rel.Split('/')[0], folder, StringComparison.OrdinalIgnoreCase))
                {
                    var target = NewFilePath(folder, root.Title, paths.Values);
                    MoveTaskFile(rel, target);
                    paths[root.Id] = target;
                    changed = true;
                }
            }
            else
            {
                paths[root.Id] = NewFilePath(folder, root.Title, paths.Values);
            }
        }

        foreach (var root in _board.Items)
        {
            var rel = paths[root.Id];
            var text = Render(root, rel);
            if (_blockedRenders.TryGetValue(root.Id, out var frozen))
            {
                if (text != frozen)
                {
                    throw new InvalidOperationException($"\"{rel}\" can't be read right now ({_problems.FirstOrDefault(p => p.Path == rel)?.Message}). Fix the file, then try again.");
                }

                continue;
            }

            if (!_files.TryGetValue(rel, out var state) || state.Text != text)
            {
                WriteTaskFile(rel, text, verify: true);
                changed = true;
            }
        }

        foreach (var (id, rel) in paths.Where(p => !live.Contains(p.Key)).ToList())
        {
            TrashTaskFile(rel);
            paths.Remove(id);
            changed = true;
        }

        lock (_paths)
        {
            _paths.Clear();
            foreach (var (id, rel) in paths)
            {
                _paths[id] = rel;
            }
        }

        changed |= RemoveDeletedGroupFolders();
        changed |= WriteConfigIfChanged();
        changed |= WriteStateIfChanged();
        changed |= AppendActivity();
        return changed;
    }

    private bool PersistGroups()
    {
        var changed = false;
        foreach (var group in _board.Groups)
        {
            var wanted = VaultFiles.SafeName(group.Name, 60);
            if (_groupFolders.TryGetValue(group.Id, out var folder))
            {
                if (!string.Equals(folder, wanted, StringComparison.Ordinal) && !string.Equals(VaultFiles.SafeName(folder, 60), wanted, StringComparison.Ordinal))
                {
                    var target = VaultFiles.UniqueStem(wanted, f => _groupFolders.Values.Any(v => string.Equals(v, f, StringComparison.OrdinalIgnoreCase)) && !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
                    MoveFolder(folder, target);
                    _groupFolders[group.Id] = target;
                    changed = true;
                }
            }
            else
            {
                var target = VaultFiles.UniqueStem(wanted, f => _groupFolders.Values.Any(v => string.Equals(v, f, StringComparison.OrdinalIgnoreCase)) || Directory.Exists(Path.Combine(_root, f)));
                Directory.CreateDirectory(Path.Combine(_root, target));
                _groupFolders[group.Id] = target;
                changed = true;
            }
        }

        return changed;
    }

    private bool RemoveDeletedGroupFolders()
    {
        var changed = false;
        foreach (var (id, folder) in _groupFolders.Where(g => _board.Groups.All(x => x.Id != g.Key)).ToList())
        {
            var full = Path.Combine(_root, folder);
            if (Directory.Exists(full))
            {
                if (Directory.EnumerateFileSystemEntries(full).Any())
                {
                    Directory.Move(full, TrashPath(folder));
                }
                else
                {
                    Directory.Delete(full);
                }
            }

            _groupFolders.Remove(id);
            changed = true;
        }

        return changed;
    }

    private void MoveFolder(string from, string to)
    {
        var source = Path.Combine(_root, from);
        var target = Path.Combine(_root, to);
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            // Case-only rename: go through a temporary name (case-insensitive file systems).
            var temp = target + ".rename-" + Guid.NewGuid().ToString("N")[..6];
            Directory.Move(source, temp);
            Directory.Move(temp, target);
        }
        else if (Directory.Exists(source))
        {
            Directory.Move(source, target);
        }
        else
        {
            Directory.CreateDirectory(target);
        }

        foreach (var key in _files.Keys.Where(k => k.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var state = _files[key];
            _files.Remove(key);
            var moved = to + key[from.Length..];
            _files[moved] = state with { Stamp = Stamp(VaultFiles.Full(_root, moved)) };
        }

        lock (_paths)
        {
            foreach (var (id, rel) in _paths.ToList())
            {
                if (rel.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase))
                {
                    _paths[id] = to + rel[from.Length..];
                }
            }
        }
    }

    private string NewFilePath(string folder, string title, IEnumerable<string> reserved)
    {
        var taken = reserved.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stem = VaultFiles.UniqueStem(VaultFiles.SafeName(title), s =>
        {
            var rel = $"{folder}/{s}.md";
            return taken.Contains(rel) || File.Exists(VaultFiles.Full(_root, rel)) || File.Exists(VaultFiles.Full(_root, $"{folder}/{s}.html"));
        });
        return $"{folder}/{stem}.md";
    }

    private void WriteTaskFile(string rel, string text, bool verify)
    {
        var full = VaultFiles.Full(_root, rel);
        if (verify && _files.TryGetValue(rel, out var state) && File.Exists(full) && Stamp(full) != state.Stamp && File.ReadAllText(full) != state.Text)
        {
            throw new VaultConflictException(rel);
        }

        VaultFiles.WriteAtomic(full, text);
        _files[rel] = new FileState(text, text, Stamp(full), null);
    }

    private void MoveTaskFile(string from, string to)
    {
        var source = VaultFiles.Full(_root, from);
        var target = VaultFiles.Full(_root, to);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(source))
        {
            File.Move(source, target);
        }

        var html = Path.ChangeExtension(source, ".html");
        if (File.Exists(html))
        {
            File.Move(html, Path.ChangeExtension(target, ".html"));
        }

        if (_files.Remove(from, out var state))
        {
            _files[to] = state with { Stamp = File.Exists(target) ? Stamp(target) : state.Stamp };
        }
    }

    private void TrashTaskFile(string rel)
    {
        var full = VaultFiles.Full(_root, rel);
        foreach (var file in new[] { full, Path.ChangeExtension(full, ".html") })
        {
            if (File.Exists(file))
            {
                File.Move(file, TrashPath(Path.GetFileName(file)));
            }
        }

        _files.Remove(rel);
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

    // ---- Settings, timer state, activity ------------------------------------------------

    private sealed record ConfigGroup(Guid Id, string Folder, string? Color);

    private sealed record ConfigLabel(string Name, string Color);

    private sealed record ConfigDocument(int SchemaVersion, List<ConfigGroup> Groups, List<ConfigLabel> Labels);

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
                ? new ConfigDocument(2, [], [])
                : doc with { Groups = doc.Groups ?? [], Labels = doc.Labels ?? [] };
        }
        catch (JsonException)
        {
            return new ConfigDocument(2, [], []);
        }
    }

    private bool WriteConfigIfChanged()
    {
        var doc = new ConfigDocument(
            2,
            _board.Groups.Select(g => new ConfigGroup(g.Id, _groupFolders[g.Id], g.Color)).ToList(),
            _board.Labels.Select(l => new ConfigLabel(l.Name, l.Color)).ToList());
        var text = JsonSerializer.Serialize(doc, Json) + "\n";
        if (text == _configText)
        {
            return false;
        }

        VaultFiles.WriteAtomic(Path.Combine(MetaDir, "config.json"), text);
        _configText = text;
        return true;
    }

    private sealed record StateDocument(PomodoroSettings? Settings, PomodoroPhase Phase, DateTimeOffset? EndsAt, int? PausedRemainingSeconds, Guid? ItemId, int CompletedFocusCount);

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
            }
        }
        catch (JsonException)
        {
            // A damaged timer state just means an idle timer.
        }
    }

    private bool WriteStateIfChanged()
    {
        var p = _board.Pomodoro;
        var doc = new StateDocument(p.Settings, p.Phase, p.EndsAt, p.PausedRemaining is { } r ? (int)Math.Round(r.TotalSeconds) : null, p.ItemId, p.CompletedFocusCount);
        var text = JsonSerializer.Serialize(doc, Json) + "\n";
        if (text == _stateText)
        {
            return false;
        }

        VaultFiles.WriteAtomic(Path.Combine(MetaDir, "state.json"), text);
        _stateText = text;
        return true;
    }

    private sealed record ActivityLine(DateTimeOffset At, Guid ItemId, ActivityKind Kind, string Summary, ActorKind ActorKind, string? Actor);

    private void LoadActivity(TaskBoard board)
    {
        var path = Path.Combine(MetaDir, "activity.jsonl");
        _activityLength = FileLength(path);
        var count = 0;
        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path))
            {
                try
                {
                    if (line.Length > 0 && JsonSerializer.Deserialize<ActivityLine>(line, JsonLine) is { } a)
                    {
                        board.AddLoadedActivity(new ActivityEntry(a.At, a.ItemId, a.Kind, a.Summary, new Actor(a.ActorKind, a.Actor)));
                        count++;
                    }
                }
                catch (JsonException)
                {
                    // A torn last line (crash mid-append) is skipped.
                }
            }
        }

        _activityPersisted = count;
    }

    private bool AppendActivity()
    {
        var pending = _board.Activity.Skip(_activityPersisted).ToList();
        if (pending.Count == 0)
        {
            return false;
        }

        var path = Path.Combine(MetaDir, "activity.jsonl");
        Directory.CreateDirectory(MetaDir);
        var sb = new StringBuilder();
        foreach (var a in pending)
        {
            sb.Append(JsonSerializer.Serialize(new ActivityLine(a.At, a.ItemId, a.Kind, a.Summary, a.Actor.Kind, a.Actor.Name), JsonLine)).Append('\n');
        }

        File.AppendAllText(path, sb.ToString());
        _activityPersisted = _board.Activity.Count;
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

    private sealed class VaultConflictException(string path) : IOException($"\"{path}\" changed while saving.");
}
