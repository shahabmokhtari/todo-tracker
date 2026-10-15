using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TodoTracker.Server.Hooks;

/// <summary>What removing a hook says: which one went.</summary>
public sealed record HookRemoved(string Removed);

/// <summary>A hook with its event and handle, as Settings, the API, the AI tools and <c>tt hooks</c> show it.</summary>
public sealed record HookEntry(string Id, string Event, HookSpec Hook, string State, string? Problem);

/// <summary>
/// Who's changing hooks. <see cref="Trusted"/>: the person at this computer (the app, or <c>tt</c>): their hooks run
/// at once. Otherwise an AI agent or another app with the API token: what it adds or changes waits for approval.
/// </summary>
public sealed record HookAuthor(string Name, bool Trusted)
{
    public static readonly HookAuthor You = new("you", true);
}

/// <summary>
/// <c>hooks.json</c>: reads it (again when it changes on disk) and changes it for the app, the API, the AI tools and
/// <c>tt hooks</c>. Writes replace the file in one step and never overwrite a file that can't be read (fix it first).
/// Comments in a hand-edited file don't survive a change made here.
/// </summary>
public sealed class HookStore
{
    public const int MaxScriptLength = 8000;
    public const int MaxListLength = 50;

    private readonly Lock _lock = new();
    private (DateTime Stamp, HookFile? File, string? Problem) _cache = (DateTime.MinValue, null, null);

    public HookStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        FilePath = System.IO.Path.Combine(dataDirectory, "hooks.json");
    }

    public string FilePath { get; }

    public bool Exists => File.Exists(FilePath);

    /// <summary>hooks.json as it is now (every hook with an id), or why it can't be read; (null, null) when there's none.</summary>
    public (HookFile? File, string? Problem) Read()
    {
        lock (_lock)
        {
            return ReadLocked();
        }
    }

    public IReadOnlyList<HookEntry> List()
    {
        var (file, _) = Read();
        return Entries(file).ToList();
    }

    public HookEntry Get(string id) => List().FirstOrDefault(e => e.Id == id) ?? throw new KeyNotFoundException($"There's no hook {id}.");

    /// <summary>Adds a hook for <paramref name="eventName"/> (or <c>*</c>). One an agent adds waits for approval.</summary>
    public HookEntry Add(string eventName, HookSpec hook, HookAuthor author, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(hook);
        ArgumentNullException.ThrowIfNull(author);
        var name = CleanEvent(eventName);
        var clean = Clean(hook);
        return Change(file =>
        {
            clean.Id = NewId(file);
            Stamp(clean, author, now);
            clean.Enabled = hook.Enabled == false ? false : null;
            if (!file.Hooks.TryGetValue(name, out var list))
            {
                file.Hooks[name] = list = [];
            }

            list.Add(clean);
            return clean.Id;
        });
    }

    /// <summary>Replaces what a hook does (and, given an event, when). A change by an agent needs approval again.</summary>
    public HookEntry Update(string id, string? eventName, HookSpec hook, HookAuthor author, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(hook);
        ArgumentNullException.ThrowIfNull(author);
        var clean = Clean(hook);
        var name = eventName is null ? null : CleanEvent(eventName);
        return Change(file =>
        {
            var (oldEvent, old) = Find(file, id);
            clean.Id = id;
            clean.Enabled = old.Enabled;
            Stamp(clean, author, now);
            var target = name ?? oldEvent;
            var list = file.Hooks[oldEvent];
            var index = list.IndexOf(old);
            if (target == oldEvent)
            {
                list[index] = clean;
            }
            else
            {
                list.RemoveAt(index);
                if (list.Count == 0)
                {
                    file.Hooks.Remove(oldEvent);
                }

                if (!file.Hooks.TryGetValue(target, out var other))
                {
                    file.Hooks[target] = other = [];
                }

                other.Add(clean);
            }

            return id;
        });
    }

    /// <summary>Switches a hook on or off (anyone may; an agent's hook still needs approval to run).</summary>
    public HookEntry SetEnabled(string id, bool enabled) => Change(file =>
    {
        Find(file, id).Hook.Enabled = enabled ? null : false;
        return id;
    });

    /// <summary>Lets an agent's hook run. Only the person at this computer can approve.</summary>
    public HookEntry Approve(string id, HookAuthor author)
    {
        ArgumentNullException.ThrowIfNull(author);
        if (!author.Trusted)
        {
            throw new UnauthorizedAccessException("Only you can approve a hook: in Settings › Hooks, or with tt hooks approve.");
        }

        return Change(file =>
        {
            Find(file, id).Hook.NeedsApproval = null;
            return id;
        });
    }

    public void Remove(string id) => Change(file =>
    {
        var (eventName, hook) = Find(file, id);
        file.Hooks[eventName].Remove(hook);
        if (file.Hooks[eventName].Count == 0)
        {
            file.Hooks.Remove(eventName);
        }

        return null;
    });

    // ---- inside ---------------------------------------------------------------------------------

    private HookEntry Change(Func<HookFile, string?> change)
    {
        lock (_lock)
        {
            var (file, problem) = ReadLocked();
            if (problem is not null)
            {
                throw new InvalidOperationException($"{problem} Fix the file first ({FilePath}).");
            }

            file ??= new HookFile();
            var id = change(file);
            Write(file);
            _cache = (DateTime.MinValue, null, null);
            var (saved, _) = ReadLocked();
            return id is null ? new HookEntry(string.Empty, string.Empty, new HookSpec(), string.Empty, null) : Entries(saved).First(e => e.Id == id);
        }
    }

    private (HookFile? File, string? Problem) ReadLocked()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                _cache = (DateTime.MinValue, null, null);
                return (null, null);
            }

            var stamp = File.GetLastWriteTimeUtc(FilePath);
            if (stamp != _cache.Stamp || (_cache.File is null && _cache.Problem is null))
            {
                try
                {
                    var file = JsonSerializer.Deserialize<HookFile>(File.ReadAllText(FilePath), HookFile.Json);
                    if (file is not null)
                    {
                        file.Hooks ??= new(StringComparer.Ordinal);
                        AssignIds(file);
                    }

                    _cache = (stamp, file, file is null ? "hooks.json is empty." : null);
                }
                catch (JsonException ex)
                {
                    _cache = (stamp, null, $"hooks.json can't be read: {ex.Message}");
                }
            }

            return (_cache.File, _cache.Problem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"hooks.json can't be read: {ex.Message}");
        }
    }

    private void Write(HookFile file)
    {
        var dir = System.IO.Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, HookFile.Json) + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, FilePath, overwrite: true);
    }

    private static IEnumerable<HookEntry> Entries(HookFile? file) =>
        (file?.Hooks ?? []).SelectMany(p => p.Value.Select(h => new HookEntry(h.Id!, p.Key, h, h.State, h.Problem() ?? (p.Key == "*" || EventNames.All.ContainsKey(p.Key) ? null : $"\"{p.Key}\" isn't an event."))));

    private static (string Event, HookSpec Hook) Find(HookFile file, string id) =>
        file.Hooks.SelectMany(p => p.Value.Select(h => (p.Key, h))).FirstOrDefault(x => x.h.Id == id) is ({ } e, { } h)
            ? (e, h)
            : throw new KeyNotFoundException($"There's no hook {id}.");

    /// <summary>A hook written by hand without an id gets one from what it is (the same every time it's read).</summary>
    private static void AssignIds(HookFile file)
    {
        var used = new HashSet<string>(file.Hooks.SelectMany(p => p.Value).Select(h => h.Id).OfType<string>(), StringComparer.Ordinal);
        foreach (var (eventName, list) in file.Hooks)
        {
            foreach (var hook in list.Where(h => string.IsNullOrWhiteSpace(h.Id)))
            {
                var seed = $"{eventName}|{JsonSerializer.Serialize(hook, HookFile.Json)}";
                var id = Hash(seed);
                for (var n = 2; used.Contains(id); n++)
                {
                    id = Hash($"{seed}|{n}");
                }

                hook.Id = id;
                used.Add(id);
            }
        }
    }

    private static string NewId(HookFile file)
    {
        var used = file.Hooks.SelectMany(p => p.Value).Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
        string id;
        do
        {
            id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        }
        while (used.Contains(id));
        return id;
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8];

    private static void Stamp(HookSpec hook, HookAuthor author, DateTimeOffset now)
    {
        hook.AddedBy = author.Name;
        hook.AddedAt = now;
        hook.NeedsApproval = author.Trusted ? null : true;
    }

    private static string CleanEvent(string? eventName)
    {
        var name = eventName?.Trim() ?? string.Empty;
        return name == "*" || EventNames.All.ContainsKey(name) && name != "test"
            ? name
            : throw new ArgumentException($"\"{name}\" isn't an event. Events: {string.Join(", ", EventNames.All.Keys.Where(k => k != "test"))}, or * for every one.", nameof(eventName));
    }

    /// <summary>Only what a hook is (its state is the store's), checked and trimmed.</summary>
    private static HookSpec Clean(HookSpec hook)
    {
        static string? Text(string? value, int max, string what)
        {
            var clean = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            return clean is null || clean.Length <= max ? clean : throw new ArgumentException($"The {what} is too long (at most {max} characters).");
        }

        static List<string>? Items(List<string>? values, string what)
        {
            var clean = values?.Select(v => v?.Trim()).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return clean is null || clean.Count == 0 ? null
                : clean.Count <= MaxListLength && clean.All(v => v.Length <= 200) ? clean : throw new ArgumentException($"Too many {what}.");
        }

        static Dictionary<string, string>? Pairs(Dictionary<string, string>? values, string what) =>
            values is null || values.Count == 0 ? null
                : values.Count <= MaxListLength && values.All(p => !string.IsNullOrWhiteSpace(p.Key) && p.Key.Length <= 200 && (p.Value?.Length ?? 0) <= 2000)
                    ? new Dictionary<string, string>(values, StringComparer.Ordinal)
                    : throw new ArgumentException($"The {what} aren't right (names and values, at most {MaxListLength}).");

        var clean = new HookSpec
        {
            Type = (hook.Type ?? "command").Trim().ToLowerInvariant(),
            Powershell = Text(hook.Powershell, MaxScriptLength, "PowerShell script"),
            Bash = Text(hook.Bash, MaxScriptLength, "bash script"),
            Cwd = Text(hook.Cwd, 500, "folder"),
            TimeoutSec = hook.TimeoutSec is { } t ? (t is >= 1 and <= HookSpec.MaxTimeoutSec ? t : throw new ArgumentException($"timeoutSec is 1 to {HookSpec.MaxTimeoutSec}.")) : null,
            Env = Pairs(hook.Env, "environment variables"),
            Url = Text(hook.Url, 2000, "URL"),
            Headers = Pairs(hook.Headers, "headers"),
            Name = Text(hook.Name, 100, "name"),
            Match = hook.Match is { } m && (m.Groups ?? m.Tags ?? m.Labels ?? m.Actors) is not null
                ? new HookMatch { Groups = Items(m.Groups, "groups"), Tags = Items(m.Tags, "tags"), Labels = Items(m.Labels, "labels"), Actors = Items(m.Actors, "actors") }
                : null,
        };
        if (clean.Match is { Groups: null, Tags: null, Labels: null, Actors: null })
        {
            clean.Match = null;
        }

        if (clean.Type == "command")
        {
            clean.Url = null;
            clean.Headers = null;
        }
        else
        {
            clean.Powershell = null;
            clean.Bash = null;
            clean.Cwd = null;
            clean.Env = null;
        }

        return clean.Problem() is { } problem ? throw new ArgumentException(problem) : clean;
    }
}
