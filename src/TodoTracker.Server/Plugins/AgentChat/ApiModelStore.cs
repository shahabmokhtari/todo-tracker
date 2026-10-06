using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TodoTracker.Server.Plugins.AgentChat;

/// <summary>
/// The API models added to Ask AI (<c>models.json</c>) and their keys, each in its own file under <c>keys/</c>:
/// protected for this Windows user (DPAPI) on Windows, readable only by the user elsewhere. Keys are never handed back
/// to a screen: only whether a model has one.
/// </summary>
public sealed class ApiModelStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly byte[] Entropy = "TodoTracker.AskAI.ApiKey"u8.ToArray();
    private readonly string _path;
    private readonly string _keys;
    private readonly Lock _lock = new();
    private List<ApiModel> _models;

    public ApiModelStore(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _path = Path.Combine(directory, "models.json");
        _keys = Path.Combine(directory, "keys");
        _models = Load();
    }

    /// <summary>Raised after a model was added or removed.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<ApiModel> List()
    {
        lock (_lock)
        {
            return [.. _models];
        }
    }

    public ApiModel? Get(string id)
    {
        lock (_lock)
        {
            return _models.FirstOrDefault(m => m.Id == id);
        }
    }

    public void Add(ApiModel model, string? key)
    {
        ArgumentNullException.ThrowIfNull(model);
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                WriteKey(model.Id, key.Trim());
            }

            List<ApiModel> next = [.. _models.Where(m => m.Id != model.Id), model];
            Save(next);
            _models = next;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            var next = _models.Where(m => m.Id != id).ToList();
            if (next.Count == _models.Count)
            {
                return false;
            }

            Save(next);
            _models = next;
            File.Delete(KeyPath(id));
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool HasKey(string id) => Get(id) is not null && File.Exists(KeyPath(id));

    /// <summary>The model's key, or empty when it has none (a model on this computer).</summary>
    public string Key(string id)
    {
        var path = KeyPath(id);
        if (Get(id) is null || !File.Exists(path))
        {
            return string.Empty;
        }

        var bytes = File.ReadAllBytes(path);
        return Encoding.UTF8.GetString(OperatingSystem.IsWindows() ? ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser) : bytes);
    }

    private string KeyPath(string id) => Path.Combine(_keys, Path.GetFileName(id) + ".key");

    private void WriteKey(string id, string key)
    {
        Directory.CreateDirectory(_keys);
        var path = KeyPath(id);
        var bytes = Encoding.UTF8.GetBytes(key);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(path, ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));
            return;
        }

        // Not encrypted here: only this user can read it (like ~/.ssh keys).
        using var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        file.Write(bytes);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private List<ApiModel> Load()
    {
        List<StoredModel>? stored;
        try
        {
            stored = File.Exists(_path) ? JsonSerializer.Deserialize<List<StoredModel>>(File.ReadAllText(_path), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var models = new List<ApiModel>();
        foreach (var model in stored ?? [])
        {
            try
            {
                models.Add(model.ToModel());
            }
            catch (ArgumentException)
            {
                // A hand-edited entry that isn't valid any more is left out, not the whole list.
            }
        }

        return models;
    }

    private void Save(List<ApiModel> models)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(models.Select(StoredModel.Of), Json));
        File.Move(temp, _path, overwrite: true);
    }

    /// <summary>What's written for a model (never its key).</summary>
    private sealed record StoredModel(string Id, string Preset, string Name, string BaseUrl, string Model)
    {
        public static StoredModel Of(ApiModel m) => new(m.Id, m.Preset, m.Name, m.BaseUrl, m.Model);

        // Checked again on the way in: a hand-edited file can't point a key at plain http elsewhere.
        public ApiModel ToModel() => ApiModel.Create(Preset, Name, BaseUrl, Model, Id);
    }
}
