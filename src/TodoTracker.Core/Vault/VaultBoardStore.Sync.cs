using System.Text;
using System.Text.Json;
using TodoTracker.Core.Sync;

namespace TodoTracker.Core.Vault;

/// <summary>What sync needs from the vault: a consistent snapshot of everything that syncs, and a careful apply.</summary>
public sealed partial class VaultBoardStore
{
    /// <summary>
    /// Everything that syncs, read under the lock after taking in outside edits: task files (keyed by task id, so
    /// renames and moves merge), their rich pages, other files in the vault's folders (attachments, images), the
    /// tabs/labels settings, the Now order and the activity log. Never AGENTS.md or anything hidden.
    /// </summary>
    public async Task<SyncSnapshot> ReadSyncAsync(CancellationToken cancellationToken = default)
    {
        var changed = false;
        SyncSnapshot snapshot;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var fileLock = await VaultFiles.AcquireLockAsync(_lockPath, LockTimeout, cancellationToken).ConfigureAwait(false);
            if (_dirty || _reactionPending || DiskDiffers())
            {
                changed = Reload();
            }

            snapshot = BuildSyncSnapshot();
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return snapshot;
    }

    /// <summary>
    /// Applies a sync plan under the lock. Every file it touches must still be what the plan was worked out from
    /// (else <see cref="SyncStaleException"/> and nothing is changed); deleted files go to the vault's trash.
    /// </summary>
    public async Task ApplySyncAsync(IReadOnlyList<SyncOp> ops, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ops);
        bool changed;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var fileLock = await VaultFiles.AcquireLockAsync(_lockPath, LockTimeout, cancellationToken).ConfigureAwait(false);
            if (_dirty || _reactionPending || DiskDiffers())
            {
                Reload();
            }

            Verify(ops);
            List<Guid>? order = null;
            foreach (var op in ops)
            {
                var full = op.Kind is SyncOpKind.SetConfig or SyncOpKind.SetOrder or SyncOpKind.AppendActivity ? null : VaultFiles.Full(_root, op.Path);
                switch (op.Kind)
                {
                    case SyncOpKind.AppendActivity:
                        AppendActivityLines(op.Content!);
                        break;
                    case SyncOpKind.Write:
                        VaultFiles.WriteAtomic(full!, op.Content!);
                        break;
                    case SyncOpKind.Move:
                        var target = VaultFiles.Full(_root, op.To!);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Move(full!, target);
                        break;
                    case SyncOpKind.Delete:
                        File.Move(full!, TrashPath(Path.GetFileName(full!)));
                        break;
                    case SyncOpKind.SetConfig:
                        VaultFiles.WriteAtomic(Path.Combine(MetaDir, "config.json"), op.Content!);
                        break;
                    case SyncOpKind.SetOrder:
                        order = ParseOrder(op.Content!);
                        break;
                }
            }

            MarkDirty();
            changed = Reload();
            if (order is not null)
            {
                _board.NowOrderList.Clear();
                _board.NowOrderList.AddRange(order.Where(id => _board.Find(id) is not null).Distinct());
                changed |= WriteMetadataIfChanged();
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
    }

    /// <summary>Whether <paramref name="text"/> would be read as a task file at <paramref name="rel"/> (merged text is checked before it's written).</summary>
    public bool IsValidTaskText(string rel, string text)
    {
        try
        {
            ParseText(text, rel);
            return true;
        }
        catch (VaultFormatException)
        {
            return false;
        }
    }

    /// <summary>A path from another device is only ever a file inside the vault's own folders.</summary>
    internal static bool IsSyncablePath(string rel) =>
        rel.Length > 0 && rel.Length < 400 && rel.Contains('/', StringComparison.Ordinal) && !rel.Contains('\\', StringComparison.Ordinal)
        && !rel.Contains(':', StringComparison.Ordinal) && !rel.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
        && rel.Split('/').All(s => s.Length > 0 && s != ".." && s[0] != '.' && s.Trim().Length == s.Length && s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);

    private SyncSnapshot BuildSyncSnapshot()
    {
        var entries = new Dictionary<string, SyncEntry>(StringComparer.Ordinal);
        var memory = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string key, string rel)
        {
            var full = VaultFiles.Full(_root, rel);
            var info = new FileInfo(full);
            if (info.Exists && info.Length <= SyncLimits.MaxFileBytes && !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                entries[key] = new SyncEntry(key, rel, SyncHash.Of(File.ReadAllBytes(full)));
                covered.Add(rel);
            }
        }

        foreach (var (id, rel) in _caches.Paths)
        {
            Add(SyncKeys.Task(id), rel);
            Add(SyncKeys.Rich(id), Path.ChangeExtension(rel, ".html"));
        }

        foreach (var rel in SyncableFiles().Where(r => !covered.Contains(r)))
        {
            Add(SyncKeys.File(rel), rel);
        }

        void Remember(string key, string path, byte[] bytes)
        {
            entries[key] = new SyncEntry(key, path, SyncHash.Of(bytes));
            memory[key] = bytes;
        }

        if (File.Exists(Path.Combine(MetaDir, "config.json")))
        {
            Remember(SyncKeys.Config, SyncKeys.ConfigPath, File.ReadAllBytes(Path.Combine(MetaDir, "config.json")));
        }

        Remember(SyncKeys.Order, SyncKeys.OrderPath, OrderBytes(_board.NowOrder));
        if (File.Exists(Path.Combine(MetaDir, "activity.jsonl")))
        {
            Remember(SyncKeys.Activity, SyncKeys.ActivityPath, File.ReadAllBytes(Path.Combine(MetaDir, "activity.jsonl")));
        }

        var root = _root;
        return new SyncSnapshot(entries, e =>
        {
            if (memory.TryGetValue(e.Key, out var bytes))
            {
                return bytes;
            }

            var full = VaultFiles.Full(root, e.Path);
            var content = File.Exists(full) ? File.ReadAllBytes(full) : [];
            return SyncHash.Of(content) == e.Hash ? content : throw new SyncStaleException($"{e.Path} changed.");
        });
    }

    /// <summary>Files in the vault's folders (never root-level files like AGENTS.md, hidden ones, or links that lead elsewhere).</summary>
    private IEnumerable<string> SyncableFiles()
    {
        static bool Plain(FileSystemInfo i) => !i.Name.StartsWith('.') && !i.Attributes.HasFlag(FileAttributes.ReparsePoint);
        var pending = new Stack<DirectoryInfo>(Directory.Exists(_root) ? new DirectoryInfo(_root).EnumerateDirectories().Where(Plain) : []);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var file in dir.EnumerateFiles().Where(Plain))
            {
                var rel = VaultFiles.Relative(_root, file.FullName);
                if (IsSyncablePath(rel))
                {
                    yield return rel;
                }
            }

            foreach (var sub in dir.EnumerateDirectories().Where(Plain))
            {
                pending.Push(sub);
            }
        }
    }

    /// <summary>Writing through a link (junction, symlink) could reach outside the vault: refused.</summary>
    private void EnsureInsideVault(string rel)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(VaultFiles.Full(_root, rel))!);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root));
        for (var d = dir; d is not null && d.FullName.Length > root.Length; d = d.Parent)
        {
            if (d.Exists && d.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException($"Sync refused to write through a link: {rel}");
            }
        }

        var file = new FileInfo(VaultFiles.Full(_root, rel));
        if (file.Exists && file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"Sync refused to write through a link: {rel}");
        }
    }

    private static byte[] OrderBytes(IEnumerable<Guid> ids) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ids.Select(i => i.ToString())));

    private static List<Guid> ParseOrder(byte[] content)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<string>>(content) ?? []).Select(s => Guid.TryParse(s, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Checks the plan against the files as they are now (moves and writes in order); nothing is written yet.</summary>
    private void Verify(IReadOnlyList<SyncOp> ops)
    {
        var known = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? Current(string rel)
        {
            if (!known.TryGetValue(rel, out var hash))
            {
                var full = VaultFiles.Full(_root, rel);
                hash = File.Exists(full) ? SyncHash.Of(File.ReadAllBytes(full)) : null;
                known[rel] = hash;
            }

            return hash;
        }

        void Expect(string rel, string? expected)
        {
            if (Current(rel) != expected)
            {
                throw new SyncStaleException($"{rel} changed while syncing.");
            }
        }

        foreach (var op in ops)
        {
            if (op.Kind is SyncOpKind.Write or SyncOpKind.Move or SyncOpKind.Delete && !IsSyncablePath(op.Path))
            {
                throw new InvalidDataException($"Sync refused an unsafe path: {op.Path}");
            }

            if (op.Kind is SyncOpKind.Write or SyncOpKind.Move or SyncOpKind.Delete)
            {
                EnsureInsideVault(op.Path);
                if (op.To is not null && IsSyncablePath(op.To))
                {
                    EnsureInsideVault(op.To);
                }
            }

            switch (op.Kind)
            {
                case SyncOpKind.Write:
                    Expect(op.Path, op.Expected);
                    known[op.Path] = SyncHash.Of(op.Content!);
                    break;
                case SyncOpKind.Move:
                    if (op.To is null || !IsSyncablePath(op.To))
                    {
                        throw new InvalidDataException($"Sync refused an unsafe path: {op.To}");
                    }

                    Expect(op.Path, op.Expected);
                    if (!string.Equals(op.To, op.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        Expect(op.To, null);
                    }

                    known[op.To] = op.Expected;
                    known[op.Path] = null;
                    break;
                case SyncOpKind.Delete:
                    Expect(op.Path, op.Expected);
                    known[op.Path] = null;
                    break;
                case SyncOpKind.SetConfig:
                    var config = Path.Combine(MetaDir, "config.json");
                    if ((File.Exists(config) ? SyncHash.Of(File.ReadAllBytes(config)) : null) != op.Expected)
                    {
                        throw new SyncStaleException("The settings changed while syncing.");
                    }

                    break;
                case SyncOpKind.SetOrder:
                    if (op.Expected is not null && SyncHash.Of(OrderBytes(_board.NowOrder)) != op.Expected)
                    {
                        throw new SyncStaleException("The Now order changed while syncing.");
                    }

                    break;
            }
        }
    }

    /// <summary>Appends whole lines another device logged (never glued onto a torn last line).</summary>
    private void AppendActivityLines(byte[] lines)
    {
        var path = Path.Combine(MetaDir, "activity.jsonl");
        Directory.CreateDirectory(MetaDir);
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        if (stream.Length > 0)
        {
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() != '\n')
            {
                stream.WriteByte((byte)'\n');
            }
        }

        stream.Seek(0, SeekOrigin.End);
        stream.Write(lines);
    }
}
