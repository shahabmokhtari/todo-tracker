using System.Security.Cryptography;
using System.Text;
using TodoTracker.Core.Vault;

namespace TodoTracker.Core.Sync;

/// <summary>What one sync did.</summary>
public sealed record SyncResult(int Changes, int Peers, IReadOnlyList<SyncConflictRecord> Conflicts, bool Published, bool Skipped)
{
    /// <summary>Devices that couldn't be merged this time, and why (the others were).</summary>
    public string? Problem { get; init; }

    /// <summary>The other devices seen (null when the remote didn't change).</summary>
    public IReadOnlyList<SyncDevice>? Devices { get; init; }
}

/// <summary>How a place is used: read (others' changes come in), write (this device's tasks go out), or both.</summary>
[Flags]
public enum SyncMode
{
    None = 0,
    Read = 1,
    Write = 2,
    Both = Read | Write,
}

/// <summary>
/// Syncs a vault through a remote: merges each other device's latest snapshot into the vault (three-way, against
/// what the two devices last agreed on), then publishes this vault's snapshot. Nothing is merged or published when
/// neither side changed. One sync at a time per vault and remote, also across processes.
/// </summary>
public sealed class SyncEngine : IDisposable
{
    private readonly VaultBoardStore _store;
    private readonly ISyncRemote _remote;
    private readonly SyncState _state;
    private readonly string _lockPath;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SyncEngine(VaultBoardStore store, ISyncRemote remote, SyncState state, string lockPath, TimeProvider? time = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _lockPath = lockPath ?? throw new ArgumentNullException(nameof(lockPath));
        _time = time ?? TimeProvider.System;
    }

    public SyncState State => _state;

    /// <summary>
    /// Read only: merge the others' snapshots, publish nothing. Write only: publish, merge nothing. Read at the start of
    /// each sync, so it can change between syncs.
    /// </summary>
    public SyncMode Mode { get; set; } = SyncMode.Both;

    public void Dispose() => _gate.Dispose();

    public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var crossProcess = await VaultFiles.AcquireLockAsync(_lockPath, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var result = await SyncOnceAsync(cancellationToken).ConfigureAwait(false);
                    var open = _state.Status.Conflicts.Concat(result.Conflicts)
                        .DistinctBy(c => (c.Key, c.Mine, c.Theirs)).TakeLast(50).ToList();
                    _state.Status = new SyncStatus(_time.GetUtcNow(), result.Problem, result.Peers, open) { Devices = result.Devices ?? _state.Status.Devices };
                    return result;
                }
                catch (SyncStaleException) when (attempt < 3)
                {
                    // The vault changed while we worked: start over (and merge everything again).
                    _state.RemoteVersion = null;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _state.Status = _state.Status with { LastError = ex.Message };
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// This device stops writing to the place: its snapshot there goes (between syncs, so a sync already running can't
    /// put it back), and from now on the place is used as <paramref name="mode"/>.
    /// </summary>
    public async Task RetireAsync(SyncMode mode, CancellationToken cancellationToken = default)
    {
        Mode = mode;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Mode = mode;
            using var crossProcess = await VaultFiles.AcquireLockAsync(_lockPath, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            await _remote.ForgetAsync(_state.DeviceId, cancellationToken).ConfigureAwait(false);
            _state.Published = null;
            _state.SeenVersion = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets a conflict once it's been settled (between syncs, so a running sync can't bring it back).</summary>
    public async Task DismissAsync(string key, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _state.Status = _state.Status with { Conflicts = _state.Status.Conflicts.Where(c => c.Key != key).ToList() };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops merging a device (a computer that's gone, or this computer's old tasks folder).</summary>
    public async Task ForgetAsync(string device, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _remote.ForgetAsync(device, cancellationToken).ConfigureAwait(false);
            _state.ForgetPeer(device);
            _state.RemoteVersion = null;
            _state.Status = _state.Status with { Devices = _state.Status.Devices.Where(d => d.Device != device).ToList() };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SyncResult> SyncOnceAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var mode = Mode;
        var reading = mode.HasFlag(SyncMode.Read);

        // A new place (another folder, another gist): everything is published there again.
        if (_state.RemoteIdentity != _remote.Identity)
        {
            _state.RemoteIdentity = _remote.Identity;
            _state.RemoteVersion = null;
            _state.SeenVersion = null;
            _state.Published = null;
        }

        // Read: what's merged up to ("merged up to here"). Written only: what was last seen there (nothing is merged,
        // but an unchanged place still costs next to nothing to check).
        var known = reading ? _state.RemoteVersion : _state.SeenVersion;
        var local = await _store.ReadSyncAsync(cancellationToken).ConfigureAwait(false);
        var remote = await _remote.ReadAsync(_state.DeviceId, known, cancellationToken).ConfigureAwait(false);
        if (!reading && remote is { Partial: false })
        {
            _state.SeenVersion = remote.Version;
        }
        var heartbeatDue = _state.PublishedAt is not { } last || now - last > SyncLimits.Heartbeat;
        if (remote is null && Fingerprint(local) == _state.Published && !heartbeatDue)
        {
            return new SyncResult(0, _state.Peers().Count(), [], false, true);
        }

        // Devices not heard from in months are no longer merged (their old copies would bring back deleted tasks).
        // Written only: the others there are seen (and listed), not merged.
        var seen = (remote?.Devices ?? []).Where(d => d.Device != _state.DeviceId && now - d.At < SyncLimits.Memory).OrderBy(d => d.Device, StringComparer.Ordinal).ToList();
        var peers = reading ? seen : [];

        // A delete counts against the version it deleted, and only if it happened after that version appeared there
        // (the same content made again later is new, not deleted).
        var deleted = new Dictionary<(string Key, string Hash), DateTimeOffset>();
        foreach (var t in peers.SelectMany(d => d.Deleted ?? []).Where(t => t?.Key is not null && t.Hash is not null))
        {
            deleted[(t.Key, t.Hash)] = deleted.TryGetValue((t.Key, t.Hash), out var at) && at > t.At ? at : t.At;
        }

        var publishedSince = _state.PublishedEntries.Where(e => e.Since is not null).ToDictionary(e => (e.Key, e.Hash), e => e.Since!.Value);

        // A device with no sync history (new, reinstalled, a restored backup) may hold copies of files deleted
        // elsewhere since: its files count as old, so existing deletes apply. Later, a file that wasn't published
        // yet is new (made after any delete seen so far).
        var unpublished = _state.PublishedEntries.Count == 0 && !_state.Peers().Any() ? DateTimeOffset.MinValue : now;
        var options = new SyncPlanOptions
        {
            Deleted = e => deleted.TryGetValue((e.Key, e.Hash), out var at) && at > (e.Since ?? publishedSince.GetValueOrDefault((e.Key, e.Hash), unpublished)),
            IsValidTask = _store.IsValidTaskText,
        };

        var changes = 0;
        var conflicts = new List<SyncConflictRecord>();
        var problems = new List<string>();
        var complete = remote is not { Partial: true };
        foreach (var peer in peers)
        {
            try
            {
                var entries = Valid(peer);
                if (entries is null)
                {
                    continue;
                }

                if (!await FetchAsync(peer, entries, local, cancellationToken).ConfigureAwait(false))
                {
                    complete = false;
                    continue;
                }

                var @base = _state.LoadBase(peer.Device);
                var plan = SyncPlanner.Plan(
                    new SyncSide(_state.DeviceId, _state.DeviceName, local.Entries, local.Read),
                    new SyncSide(peer.Device, peer.Name, entries, e => _state.ReadBlob(e.Hash) ?? throw new SyncStaleException($"{e.Path} is missing.")),
                    @base,
                    _state.ReadBlob,
                    options);

                // Both versions of a clash stay available (to keep one of them, or open them in a merge tool).
                foreach (var conflict in plan.Conflicts)
                {
                    _state.WriteBlob(conflict.Mine, local.Read(local.Entries[conflict.Key]));
                }

                if (plan.Ops.Count > 0)
                {
                    await _store.ApplySyncAsync(plan.Ops, cancellationToken).ConfigureAwait(false);
                    changes += plan.Ops.Count;
                    local = await _store.ReadSyncAsync(cancellationToken).ConfigureAwait(false);
                }

                conflicts.AddRange(plan.Conflicts.Select(c => new SyncConflictRecord(c.Key, c.Path, c.Peer, c.PeerName, c.Mine, c.Theirs, c.Base, now) { Result = c.Result }));

                // Remember what both now have; text needs its content kept for the next three-way merge.
                var next = SyncPlanner.NextBase(local.Entries, entries, @base);
                foreach (var entry in next.Values.Where(e => NeedsBase(e.Key) && !_state.HasBlob(e.Hash)))
                {
                    _state.WriteBlob(entry.Hash, local.Read(entry));
                }

                _state.SaveBase(peer.Device, next);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
            {
                // One device's odd data never stops this one syncing with the others (or publishing).
                problems.Add($"{peer.Name}: {ex.Message}");
                complete = false;
            }
        }

        // "Merged up to here" only when it was read: switched to reading later, everything there is merged then.
        if (remote is not null && complete && reading)
        {
            _state.RemoteVersion = remote.Version;
        }

        if (remote is { Partial: true })
        {
            problems.Add("Another computer's sync file couldn't be read (it may be from a newer version of Todo Tracker); it's tried again.");
        }

        var published = false;
        var fingerprint = Fingerprint(local);
        if (mode.HasFlag(SyncMode.Write) && (fingerprint != _state.Published || heartbeatDue || remote is { SelfMissing: true }))
        {
            // What disappeared since the last publish was deleted here: say so (for months) so it stays gone everywhere.
            var tombstones = _state.Tombstones.Where(t => now - t.At < SyncLimits.Memory && !local.Entries.ContainsKey(t.Key))
                .Concat(_state.PublishedEntries.Where(e => !local.Entries.ContainsKey(e.Key) && SyncKeys.KindOf(e.Key) is SyncKind.Task or SyncKind.Rich or SyncKind.File)
                    .Select(e => new SyncTombstone(e.Key, e.Hash, now)))
                .DistinctBy(t => (t.Key, t.Hash))
                .ToList();
            var entriesNow = local.Entries.Values
                .Select(e => e with { Since = publishedSince.TryGetValue((e.Key, e.Hash), out var since) ? since : now })
                .OrderBy(e => e.Key, StringComparer.Ordinal)
                .ToList();
            var mine = new DeviceSnapshot(_state.DeviceId, _state.DeviceName, now, entriesNow) { Deleted = tombstones };
            var version = await _remote.PublishAsync(mine, local.Read, reading ? _state.RemoteVersion : _state.SeenVersion, cancellationToken).ConfigureAwait(false);
            _state.Tombstones = tombstones;
            _state.PublishedEntries = entriesNow;
            _state.Published = fingerprint;
            _state.PublishedAt = now;
            if (complete && reading)
            {
                _state.RemoteVersion = version;
            }
            else if (!reading)
            {
                _state.SeenVersion = version;
            }

            published = true;
        }

        CollectBlobs(conflicts);
        var devices = remote is null ? null : seen.Select(d => new SyncDevice(d.Device, d.Name, d.At)).ToList();
        return new SyncResult(changes, peers.Count, conflicts, published, false) { Problem = problems.Count > 0 ? string.Join(" ", problems) : null, Devices = devices };
    }

    /// <summary>Brings the peer's contents we don't have yet into the local cache. False if some haven't arrived.</summary>
    private async Task<bool> FetchAsync(DeviceSnapshot peer, Dictionary<string, SyncEntry> entries, SyncSnapshot local, CancellationToken cancellationToken)
    {
        foreach (var entry in entries.Values)
        {
            if ((local.Entries.TryGetValue(entry.Key, out var mine) && mine.Hash == entry.Hash) || _state.HasBlob(entry.Hash))
            {
                continue;
            }

            byte[] content;
            try
            {
                content = await _remote.ReadContentAsync(peer, entry, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Not there yet, still downloading (cloud placeholders), or too big: this device is merged later.
                return false;
            }

            if (SyncHash.Of(content) != entry.Hash)
            {
                // Still being written (or damaged): try again next time.
                return false;
            }

            _state.WriteBlob(entry.Hash, content);
        }

        return true;
    }

    /// <summary>The peer's entries we accept: well-formed ids, hashes and paths only (it's data from elsewhere).</summary>
    private static Dictionary<string, SyncEntry>? Valid(DeviceSnapshot peer)
    {
        if (peer.Device is not { Length: > 1 and <= 64 } id || !id.All(char.IsAsciiLetterOrDigit) || peer.Entries is null)
        {
            return null;
        }

        var entries = new Dictionary<string, SyncEntry>(StringComparer.Ordinal);
        foreach (var e in peer.Entries)
        {
            if (e?.Key is null || e.Path is null || e.Hash is not { Length: 64 } hash || !hash.All(char.IsAsciiHexDigitLower))
            {
                continue;
            }

            var ok = SyncKeys.KindOf(e.Key) switch
            {
                SyncKind.Config => e.Path == SyncKeys.ConfigPath,
                SyncKind.Order => e.Path == SyncKeys.OrderPath,
                SyncKind.Activity => e.Path == SyncKeys.ActivityPath,
                SyncKind.Task => e.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && VaultBoardStore.IsSyncablePath(e.Path) && Guid.TryParseExact(e.Key[2..], "N", out _),
                SyncKind.Rich => e.Path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && VaultBoardStore.IsSyncablePath(e.Path) && Guid.TryParseExact(e.Key[2..], "N", out _),
                _ => e.Key == SyncKeys.File(e.Path) && VaultBoardStore.IsSyncablePath(e.Path),
            };
            if (ok)
            {
                // Older devices don't say since when they have a version: oldest possible (a later delete wins).
                entries[e.Key] = e.Since is null ? e with { Since = DateTimeOffset.MinValue } : e;
            }
        }

        return entries;
    }

    private static bool NeedsBase(string key) => SyncKeys.KindOf(key) is SyncKind.Task or SyncKind.Rich or SyncKind.Config or SyncKind.Order;

    private static string Fingerprint(SyncSnapshot snapshot) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', snapshot.Entries.Values.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key}|{e.Path}|{e.Hash}")))));

    private void CollectBlobs(IEnumerable<SyncConflictRecord> fresh)
    {
        var keep = _state.Peers().SelectMany(p => _state.LoadBase(p).Values).Where(e => NeedsBase(e.Key)).Select(e => e.Hash)
            .Concat(_state.Status.Conflicts.Concat(fresh).SelectMany(c => new[] { c.Mine, c.Theirs, c.Base }.OfType<string>()));
        _state.CollectBlobs(keep);
    }
}
