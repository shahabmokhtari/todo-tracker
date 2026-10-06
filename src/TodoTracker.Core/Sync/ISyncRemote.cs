namespace TodoTracker.Core.Sync;

/// <summary>What a remote holds: every device's last published snapshot, and a version token for all of it.</summary>
public sealed record RemoteSnapshot(IReadOnlyList<DeviceSnapshot> Devices, string Version);

/// <summary>
/// Where devices meet: a shared folder (OneDrive, iCloud Drive, a network share), a gist, ... Each device only ever
/// writes its own snapshot (and contents), so two devices never write the same thing.
/// </summary>
public interface ISyncRemote
{
    /// <summary>The other devices' snapshots, or null when nothing changed since <paramref name="knownVersion"/>.</summary>
    Task<RemoteSnapshot?> ReadAsync(string self, string? knownVersion, CancellationToken cancellationToken);

    /// <summary>The content of one of a device's entries.</summary>
    /// <exception cref="FileNotFoundException">Not there (yet): the device may still be uploading.</exception>
    Task<byte[]> ReadContentAsync(DeviceSnapshot device, SyncEntry entry, CancellationToken cancellationToken);

    /// <summary>
    /// Publishes this device's snapshot (contents first, the snapshot last). Returns the remote's new version when it
    /// is certain nothing else changed since <paramref name="readVersion"/> (so the next read can skip), else null.
    /// </summary>
    Task<string?> PublishAsync(DeviceSnapshot snapshot, Func<SyncEntry, byte[]> read, string? readVersion, CancellationToken cancellationToken);
}
