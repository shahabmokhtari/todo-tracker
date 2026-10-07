using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TodoTracker.Core.Connectors;

namespace TodoTracker.Server.Plugins.Connectors;

/// <summary>A secret on disk: encrypted for this Windows user (DPAPI); elsewhere readable only by this user.</summary>
public static class SecretFile
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TodoTracker.Connectors");

    public static string? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            return Encoding.UTF8.GetString(OperatingSystem.IsWindows() ? ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser) : bytes);
        }
        catch (CryptographicException)
        {
            return null; // written by another user or computer: sign in again
        }
    }

    public static void Write(string path, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Encoding.UTF8.GetBytes(secret);
        if (OperatingSystem.IsWindows())
        {
            // Whole or not at all: a half-written token cache would mean signing in again.
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));
            File.Move(temp, path, overwrite: true);
            return;
        }

        using var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        file.Write(bytes);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

/// <summary>What a connector keeps in step with: the outside list, the group here, and which side new tasks are made on.</summary>
public sealed record ConnectorSettings
{
    /// <summary>The Notion database or To Do list id.</summary>
    public string? Target { get; init; }

    public string? TargetName { get; init; }

    public Guid? GroupId { get; init; }

    public ConnectorDirection Direction { get; init; } = ConnectorDirection.Both;

    /// <summary>Syncs on its own (every few minutes); off until the person has looked at the first preview.</summary>
    public bool Enabled { get; init; }

    /// <summary>Microsoft To Do: the app registration (client id) to sign in with.</summary>
    public string? ClientId { get; init; }

    public DateTimeOffset? LastRun { get; init; }

    public string? LastResult { get; init; }

    public IReadOnlyList<string> Problems { get; init; } = [];
}

/// <summary>
/// One connector's files on this computer (its token stays here; the links are this computer's view of what's
/// linked): <c>connectors/&lt;id&gt;/settings.json</c>, <c>links.json</c> and <c>secret</c>.
/// </summary>
public sealed class ConnectorFiles(string dataDirectory, string id)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    private readonly Lock _lock = new();

    public string Folder { get; } = Path.Combine(dataDirectory, "connectors", id);

    public string SecretPath => Path.Combine(Folder, "secret");

    public ConnectorSettings Settings
    {
        get
        {
            lock (_lock)
            {
                return ReadJson<ConnectorSettings>("settings.json") ?? new ConnectorSettings();
            }
        }
    }

    public IReadOnlyList<ConnectorLink> Links
    {
        get
        {
            lock (_lock)
            {
                return ReadJson<List<ConnectorLink>>("links.json") ?? [];
            }
        }
    }

    public string? Secret => SecretFile.Read(SecretPath);

    public ConnectorSettings Update(Func<ConnectorSettings, ConnectorSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_lock)
        {
            var next = change(ReadJson<ConnectorSettings>("settings.json") ?? new ConnectorSettings());
            WriteJson("settings.json", next);
            return next;
        }
    }

    public void SaveLinks(IReadOnlyList<ConnectorLink> links)
    {
        lock (_lock)
        {
            WriteJson("links.json", links);
        }
    }

    public void SaveSecret(string secret) => SecretFile.Write(SecretPath, secret);

    /// <summary>Forgets everything (outside items keep their task ids, so connecting again links them back).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            if (Directory.Exists(Folder))
            {
                Directory.Delete(Folder, recursive: true);
            }
        }
    }

    /// <summary>Null when the file isn't there yet; a file that can't be read is an error (never taken as empty, which
    /// would forget every link and then save that).</summary>
    private T? ReadJson<T>(string name)
        where T : class
    {
        var path = Path.Combine(Folder, name);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new IOException($"Couldn't read {path} ({ex.Message}). Nothing was changed.", ex);
        }
    }

    private void WriteJson<T>(string name, T value)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, name);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, overwrite: true);
    }
}
