using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneThing.Core.Sync;

/// <summary>A sync that couldn't be merged by itself; both versions are kept (see <see cref="SyncConflict"/>).</summary>
public sealed record SyncConflictRecord(string Key, string Path, string Peer, string PeerName, string Mine, string Theirs, string? Base, DateTimeOffset At)
{
    /// <summary>The hash of what the merge wrote (choosing a side only applies while the file is still that).</summary>
    public string? Result { get; init; }
}

/// <summary>Another device syncing through the same place, and when it last published.</summary>
public sealed record SyncDevice(string Device, string Name, DateTimeOffset At);

/// <summary>How the last sync went, for the status line and the Sync panel.</summary>
public sealed record SyncStatus(DateTimeOffset? LastSync, string? LastError, int Peers, IReadOnlyList<SyncConflictRecord> Conflicts)
{
    public static SyncStatus Empty { get; } = new(null, null, 0, []);

    public IReadOnlyList<SyncDevice> Devices { get; init; } = [];
}

/// <summary>
/// What one vault remembers about syncing with one remote, kept in local app data (never in the vault or the remote):
/// this device's id, what it last agreed on with each other device (the merge base), the contents those bases need,
/// what it last published, and the open conflicts.
/// </summary>
public sealed partial class SyncState
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly string _dir;

    public SyncState(string directory, string deviceDirectory, string? deviceName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentException.ThrowIfNullOrEmpty(deviceDirectory);
        _dir = directory;
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(deviceDirectory);

        // One id per vault on this device, shared by every remote it syncs with.
        var devicePath = Path.Combine(deviceDirectory, "device.json");
        var text = File.Exists(devicePath) ? File.ReadAllText(devicePath) : null;
        var device = text is null ? null : ReadDevice(text);
        if (device is null && text is not null && IdInText().Match(text) is { Success: true } found)
        {
            // Damaged, but the id is still there: this device stays who it was (a new id would merge it as a stranger).
            device = new DeviceInfo(found.Groups[1].Value, deviceName ?? Environment.MachineName);
            Write(devicePath, device);
        }

        if (device is null && text is not null)
        {
            Problem = $"This computer's sync identity ({devicePath}) can't be read, so sync is paused rather than joining as a new device. Put back an earlier copy of the file, or remove it to join as a new device.";
            DeviceId = string.Empty;
            DeviceName = deviceName ?? Environment.MachineName;
            return;
        }

        if (device is null)
        {
            device = new DeviceInfo(Guid.NewGuid().ToString("N"), deviceName ?? Environment.MachineName);
            Write(devicePath, device);
        }

        DeviceId = device.Id;
        DeviceName = deviceName ?? device.Name;
    }

    /// <summary>Why sync can't run with this state (its identity can't be read), or null.</summary>
    public string? Problem { get; }

    private static DeviceInfo? ReadDevice(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<DeviceInfo>(text, Json) is { Id: { Length: > 1 and <= 64 } id } info && id.All(char.IsAsciiLetterOrDigit) ? info with { Name = info.Name ?? Environment.MachineName } : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"""id""\s*:\s*""([0-9a-fA-F]{32})""")]
    private static partial System.Text.RegularExpressions.Regex IdInText();

    public string DeviceId { get; }

    public string DeviceName { get; }

    /// <summary>The remote's version (e.g. a gist's ETag) when we last merged everything it had.</summary>
    public string? RemoteVersion
    {
        get => Read<Marks>(MarksPath)?.RemoteVersion;
        set => Write(MarksPath, (Read<Marks>(MarksPath) ?? new Marks()) with { RemoteVersion = value });
    }

    /// <summary>The place's version when last seen while only writing to it (nothing merged; just "unchanged since").</summary>
    public string? SeenVersion
    {
        get => Read<Marks>(MarksPath)?.SeenVersion;
        set => Write(MarksPath, (Read<Marks>(MarksPath) ?? new Marks()) with { SeenVersion = value });
    }

    /// <summary>A hash of what we last published (to skip publishing an unchanged vault).</summary>
    public string? Published
    {
        get => Read<Marks>(MarksPath)?.Published;
        set => Write(MarksPath, (Read<Marks>(MarksPath) ?? new Marks()) with { Published = value });
    }

    /// <summary>When we last published.</summary>
    public DateTimeOffset? PublishedAt
    {
        get => Read<Marks>(MarksPath)?.PublishedAt;
        set => Write(MarksPath, (Read<Marks>(MarksPath) ?? new Marks()) with { PublishedAt = value });
    }

    /// <summary>Which place we last synced with (a new one gets everything published again).</summary>
    public string? RemoteIdentity
    {
        get => Read<Marks>(MarksPath)?.RemoteIdentity;
        set => Write(MarksPath, (Read<Marks>(MarksPath) ?? new Marks()) with { RemoteIdentity = value });
    }

    /// <summary>Drops what we agreed on with a device that's gone.</summary>
    public void ForgetPeer(string peer)
    {
        if (File.Exists(BasePath(peer)))
        {
            File.Delete(BasePath(peer));
        }
    }

    /// <summary>The entries we last published (what disappeared since was deleted here).</summary>
    public IReadOnlyList<SyncEntry> PublishedEntries
    {
        get => Read<List<SyncEntry>>(Path.Combine(_dir, "published.json")) ?? [];
        set => Write(Path.Combine(_dir, "published.json"), value);
    }

    /// <summary>What this device deleted lately.</summary>
    public IReadOnlyList<SyncTombstone> Tombstones
    {
        get => Read<List<SyncTombstone>>(Path.Combine(_dir, "deleted.json")) ?? [];
        set => Write(Path.Combine(_dir, "deleted.json"), value);
    }

    /// <summary>What the place last found whole of this device's stored contents (see <see cref="ISyncVerifiedCache"/>).</summary>
    public IReadOnlyDictionary<string, SyncVerified> Verified
    {
        get => Read<Dictionary<string, SyncVerified>>(Path.Combine(_dir, "verified.json")) ?? [];
        set => Write(Path.Combine(_dir, "verified.json"), value);
    }

    private string MarksPath => Path.Combine(_dir, "marks.json");

    private string BlobDir => Path.Combine(_dir, "blobs");

    public Dictionary<string, SyncEntry> LoadBase(string peer) =>
        (Read<List<SyncEntry>>(BasePath(peer)) ?? []).ToDictionary(e => e.Key, StringComparer.Ordinal);

    public void SaveBase(string peer, IReadOnlyDictionary<string, SyncEntry> entries) =>
        Write(BasePath(peer), entries.Values.OrderBy(e => e.Key, StringComparer.Ordinal).ToList());

    public byte[]? ReadBlob(string hash)
    {
        var path = BlobPath(hash);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public bool HasBlob(string hash) => File.Exists(BlobPath(hash));

    public void WriteBlob(string hash, byte[] content)
    {
        var path = BlobPath(hash);
        if (!File.Exists(path))
        {
            Vault.VaultFiles.WriteAtomic(path, content);
        }
    }

    /// <summary>Drops cached contents that no merge base needs any more.</summary>
    public void CollectBlobs(IEnumerable<string> keep)
    {
        if (!Directory.Exists(BlobDir))
        {
            return;
        }

        var needed = new HashSet<string>(keep, StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(BlobDir, "*", SearchOption.AllDirectories))
        {
            if (!needed.Contains(Path.GetFileName(file)))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Next time.
                }
            }
        }
    }

    public IEnumerable<string> Peers() =>
        Directory.Exists(Path.Combine(_dir, "bases"))
            ? Directory.EnumerateFiles(Path.Combine(_dir, "bases"), "*.json").Select(f => Path.GetFileNameWithoutExtension(f)!)
            : [];

    public SyncStatus Status
    {
        get => Read<SyncStatus>(Path.Combine(_dir, "status.json")) ?? SyncStatus.Empty;
        set => Write(Path.Combine(_dir, "status.json"), value);
    }

    private string BasePath(string peer) => Path.Combine(_dir, "bases", SafeId(peer) + ".json");

    private string BlobPath(string hash) => Path.Combine(BlobDir, SafeId(hash)[..2], SafeId(hash));

    /// <summary>Ids come from other devices: only letters and digits reach a file name.</summary>
    private static string SafeId(string id) =>
        id.Length is > 1 and <= 128 && id.All(char.IsAsciiLetterOrDigit) ? id : throw new InvalidDataException($"Not a valid sync id: {id}");

    private static T? Read<T>(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default;
        }
        catch (JsonException)
        {
            return default;
        }
    }

    // Flushed to the disk before it takes the real name: a power cut can't leave an empty file (a lost base or identity).
    private static void Write<T>(string path, T value) => Vault.VaultFiles.WriteAtomic(path, JsonSerializer.Serialize(value, Json));

    private sealed record DeviceInfo(string Id, string Name);

    private sealed record Marks(string? RemoteVersion = null, string? Published = null, DateTimeOffset? PublishedAt = null, string? RemoteIdentity = null, string? SeenVersion = null);
}
