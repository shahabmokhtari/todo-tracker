using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace TodoTracker.Core.Sync;

/// <summary>What a sync entry holds; decides how two versions are merged.</summary>
public enum SyncKind
{
    /// <summary>A task's markdown file, keyed by the task's id so renames and moves merge with edits.</summary>
    Task,

    /// <summary>A task's rich (HTML) page, keyed by the task's id.</summary>
    Rich,

    /// <summary>Any other file (attachments, images, other notes), keyed by its path.</summary>
    File,

    /// <summary>Tabs, labels and the task order (<c>.todo-tracker/config.json</c>), merged field by field.</summary>
    Config,

    /// <summary>The manual Now order, merged as a list.</summary>
    Order,

    /// <summary>The activity log; every device's entries are kept.</summary>
    Activity,
}

/// <summary>One syncable thing: <paramref name="Path"/> is vault-relative with forward slashes; <paramref name="Hash"/> is of its bytes.</summary>
public sealed record SyncEntry(string Key, string Path, string Hash);

public static class SyncKeys
{
    public const string Config = "config";
    public const string Order = "order";
    public const string Activity = "activity";
    public const string ConfigPath = ".todo-tracker/config.json";
    public const string OrderPath = ".todo-tracker/order.json";
    public const string ActivityPath = ".todo-tracker/activity.jsonl";

    public static string Task(Guid id) => "t/" + id.ToString("N");

    public static string Rich(Guid id) => "h/" + id.ToString("N");

    public static string File(string path) => "f/" + path;

    public static SyncKind KindOf(string key) => key switch
    {
        Config => SyncKind.Config,
        Order => SyncKind.Order,
        Activity => SyncKind.Activity,
        _ when key.StartsWith("t/", StringComparison.Ordinal) => SyncKind.Task,
        _ when key.StartsWith("h/", StringComparison.Ordinal) => SyncKind.Rich,
        _ => SyncKind.File,
    };
}

public static class SyncHash
{
    public static string Of(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
}

/// <summary>What one device published: everything in its vault, by key. Contents are stored separately by hash.</summary>
public sealed record DeviceSnapshot(
    [property: JsonPropertyName("device")] string Device,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("entries")] IReadOnlyList<SyncEntry> Entries)
{
    [JsonPropertyName("schema")]
    public int Schema { get; init; } = 1;

    /// <summary>What this device deleted lately (so a device that comes back with old copies doesn't bring them back).</summary>
    [JsonPropertyName("deleted")]
    public IReadOnlyList<SyncTombstone> Deleted { get; init; } = [];

    public Dictionary<string, SyncEntry> ByKey() => Entries.ToDictionary(e => e.Key, StringComparer.Ordinal);
}

/// <summary>A device deleted this version of this entry at this time.</summary>
public sealed record SyncTombstone(string Key, string Hash, DateTimeOffset At);

public static class SyncLimits
{
    /// <summary>Bigger files aren't synced (they'd be read whole into memory).</summary>
    public const long MaxFileBytes = 50L * 1024 * 1024;

    /// <summary>Deletes are remembered this long; devices not heard from this long are no longer merged.</summary>
    public static readonly TimeSpan Memory = TimeSpan.FromDays(180);

    /// <summary>A device that changed nothing still publishes this often, so others know it's alive.</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromDays(7);
}

/// <summary>One side of a merge: its entries and a way to read an entry's content.</summary>
public sealed record SyncSide(string Device, string Name, IReadOnlyDictionary<string, SyncEntry> Entries, Func<SyncEntry, byte[]> Read);

/// <summary>This vault's syncable entries; <see cref="Read"/> throws <see cref="SyncStaleException"/> if a file changed since.</summary>
public sealed record SyncSnapshot(IReadOnlyDictionary<string, SyncEntry> Entries, Func<SyncEntry, byte[]> Read);

/// <summary>Raised when the vault changed while a sync was being worked out; the sync starts over.</summary>
public sealed class SyncStaleException(string message) : Exception(message);
