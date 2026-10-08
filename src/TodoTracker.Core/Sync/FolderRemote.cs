using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TodoTracker.Core.Sync;

/// <summary>
/// A shared folder as the meeting place (OneDrive, iCloud Drive, Dropbox, a network share):
/// <c>devices/&lt;id&gt;.json</c> per device and <c>blobs/&lt;ab&gt;/&lt;hash&gt;</c> for contents. Files are only ever
/// added or replaced whole (temp file + rename), contents before the snapshot that lists them.
/// </summary>
public sealed class FolderRemote(string root) : ISyncRemote, ISyncPeek
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private DateTime _lastCollect = DateTime.MinValue;

    public string Root { get; } = root ?? throw new ArgumentNullException(nameof(root));

    public string Identity => "folder:" + Path.GetFullPath(Root);

    private string Devices => Path.Combine(Root, "devices");

    public Task ForgetAsync(string device, CancellationToken cancellationToken)
    {
        if (device is not { Length: > 1 and <= 64 } || !device.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("Not a device id.", nameof(device));
        }

        File.Delete(Path.Combine(Devices, device + ".json"));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SyncDevice>> PeekAsync(string self, CancellationToken cancellationToken)
    {
        var found = new List<SyncDevice>();
        if (Directory.Exists(Devices))
        {
            foreach (var file in Directory.EnumerateFiles(Devices, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(Path.GetFileNameWithoutExtension(file), self, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    if (JsonSerializer.Deserialize<DeviceSnapshot>(File.ReadAllBytes(file), Json) is { Device: not null } device)
                    {
                        found.Add(new SyncDevice(device.Device, device.Name, device.At));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    // Being written right now, or not ours: not counted this time.
                }
            }
        }

        return Task.FromResult<IReadOnlyList<SyncDevice>>(found);
    }

    public Task<RemoteSnapshot?> ReadAsync(string self, string? knownVersion, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Devices);
        var selfMissing = !File.Exists(Path.Combine(Devices, self + ".json"));
        var files = new List<(string Name, byte[] Content)>();
        foreach (var file in Directory.EnumerateFiles(Devices, "*.json").Order(StringComparer.Ordinal))
        {
            if (string.Equals(Path.GetFileNameWithoutExtension(file), self, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                files.Add((Path.GetFileName(file), File.ReadAllBytes(file)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Being replaced right now: seen next time.
            }
        }

        // The other devices' files by content (times and sizes can stay the same across quick writes; ours don't count).
        var version = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', files.Select(f => $"{f.Name}|{SyncHash.Of(f.Content)}")))));
        if (version == knownVersion && !selfMissing)
        {
            return Task.FromResult<RemoteSnapshot?>(null);
        }

        var devices = new List<DeviceSnapshot>();
        var partial = false;
        foreach (var (name, content) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (JsonSerializer.Deserialize<DeviceSnapshot>(content, Json) is { Schema: 1, Entries: not null } device
                    && string.Equals(device.Device, Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase))
                {
                    devices.Add(device);
                }
            }
            catch (JsonException)
            {
                // Half-synced or from a newer version: skipped, and looked at again next time.
                partial = true;
            }
        }

        return Task.FromResult<RemoteSnapshot?>(new RemoteSnapshot(devices, version, partial, selfMissing));
    }

    public async Task<byte[]> ReadContentAsync(DeviceSnapshot device, SyncEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var path = BlobPath(entry.Hash);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{entry.Path} from {device?.Name} hasn't arrived yet.", path);
        }

        if (new FileInfo(path).Length > SyncLimits.MaxFileBytes)
        {
            throw new InvalidDataException($"{entry.Path} from {device?.Name} is too big to sync.");
        }

        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> PublishAsync(DeviceSnapshot snapshot, Func<SyncEntry, byte[]> read, string? readVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(read);
        foreach (var entry in snapshot.Entries)
        {
            var path = BlobPath(entry.Hash);
            if (!File.Exists(path))
            {
                await WriteAsync(path, read(entry), cancellationToken).ConfigureAwait(false);
            }
            else if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1))
            {
                // Still in use: never "old" to a device cleaning up while it sees an older list of ours.
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
        }

        await WriteAsync(Path.Combine(Devices, snapshot.Device + ".json"), JsonSerializer.SerializeToUtf8Bytes(snapshot, Json), cancellationToken).ConfigureAwait(false);
        if (DateTime.UtcNow - _lastCollect > TimeSpan.FromHours(6))
        {
            _lastCollect = DateTime.UtcNow;
            Collect();
        }

        // Our own file doesn't change the version the other devices' files have.
        return readVersion;
    }

    /// <summary>Removes contents no device lists any more (only old ones: a device may be between writing contents and its snapshot).</summary>
    public void Collect(TimeSpan? minimumAge = null)
    {
        var blobs = Path.Combine(Root, "blobs");
        if (!Directory.Exists(blobs) || !Directory.Exists(Devices))
        {
            return;
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Devices, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<DeviceSnapshot>(File.ReadAllBytes(file), Json)?.Entries is { } entries)
                {
                    used.UnionWith(entries.Select(e => e.Hash));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A device file that can't be read now: keep everything.
                return;
            }
        }

        var cutoff = DateTime.UtcNow - (minimumAge ?? TimeSpan.FromDays(7));
        foreach (var file in Directory.EnumerateFiles(blobs, "*", SearchOption.AllDirectories))
        {
            if (!used.Contains(Path.GetFileName(file)) && File.GetLastWriteTimeUtc(file) < cutoff)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Next time.
                }
            }
        }
    }

    private string BlobPath(string hash) =>
        hash.Length == 64 && hash.All(char.IsAsciiHexDigitLower)
            ? Path.Combine(Root, "blobs", hash[..2], hash)
            : throw new InvalidDataException($"Not a content hash: {hash}");

    private static async Task WriteAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, content, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
