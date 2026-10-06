using System.Text.Json;
using System.Text.RegularExpressions;

namespace TodoTracker.Server.Plugins.AgentChat;

/// <summary>A chat in the list: what it's called, who it's with, and when it last changed.</summary>
public sealed record ChatSummary(string Id, string Title, string Agent, DateTimeOffset UpdatedAt, string? Snippet = null);

/// <summary>
/// One chat as it's kept: the transcript the screen shows, plus what's needed to carry on. An agent chat remembers
/// its agent session (to resume it); an API chat keeps the conversation the model is sent each time.
/// </summary>
public sealed class ChatRecord
{
    public required string Id { get; init; }

    public string Title { get; set; } = "New chat";

    /// <summary>Named by the person (else the title follows the first message).</summary>
    public bool Renamed { get; set; }

    /// <summary>The agent (copilot, claude) or API model (api:&lt;id&gt;) the chat is with; a chat stays with it.</summary>
    public required string Agent { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Grows with every copy taken for saving, so an older copy never overwrites a newer one.</summary>
    public long Revision { get; set; }

    public List<ChatEntry> Entries { get; init; } = [];

    /// <summary>The agent's session, to pick the chat up where it was (agents that can load sessions).</summary>
    public string? AcpSessionId { get; set; }

    /// <summary>The chat signed in with the person's own agent settings.</summary>
    public bool SignInFallback { get; set; }

    /// <summary>The conversation sent to an API model (text, tool calls and their results).</summary>
    public List<ApiMessage> Messages { get; init; } = [];

    public static ChatRecord New(string agent, DateTimeOffset now) =>
        new() { Id = Guid.NewGuid().ToString("N")[..12], Agent = agent, CreatedAt = now, UpdatedAt = now };

    /// <summary>A copy to save (taken while the chat can't change), newer than every copy taken before it.</summary>
    public ChatRecord Snapshot()
    {
        var copy = new ChatRecord
        {
            Id = Id, Title = Title, Renamed = Renamed, Agent = Agent, CreatedAt = CreatedAt, UpdatedAt = UpdatedAt, Revision = Revision,
            Entries = [.. Entries], AcpSessionId = AcpSessionId, SignInFallback = SignInFallback, Messages = [.. Messages],
        };
        Revision++;
        return copy;
    }
}

/// <summary>
/// Chats kept one file each (<c>&lt;id&gt;.json</c>, written whole and replaced, so a crash never leaves half a chat).
/// Deleting moves a chat aside for 30 days, so it can be brought back.
/// </summary>
public sealed partial class ChatHistoryStore
{
    public const int TitleLength = 60;
    private static readonly TimeSpan KeepDeleted = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _dir;
    private readonly string _deleted;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (ChatSummary Summary, bool Renamed, long Revision)> _index = new(StringComparer.Ordinal);

    public ChatHistoryStore(string directory, TimeProvider time)
    {
        _dir = directory ?? throw new ArgumentNullException(nameof(directory));
        _deleted = Path.Combine(directory, "deleted");
        _time = time ?? TimeProvider.System;
        Directory.CreateDirectory(_dir);
        PurgeDeleted();
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            if (Read(file) is { } chat && ValidId(chat.Id) && Path.GetFileNameWithoutExtension(file) == chat.Id)
            {
                _index[chat.Id] = (Summary(chat), chat.Renamed, chat.Revision);
            }
        }
    }

    /// <summary>Raised after the list changed (a chat saved, renamed, deleted or brought back).</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<ChatSummary> List()
    {
        lock (_lock)
        {
            return [.. _index.Values.Select(v => v.Summary).OrderByDescending(s => s.UpdatedAt)];
        }
    }

    public ChatRecord? Load(string id)
    {
        if (!ValidId(id))
        {
            return null;
        }

        lock (_lock)
        {
            return _index.ContainsKey(id) ? Read(PathOf(id)) : null;
        }
    }

    /// <summary>Saves a chat (one with nothing said yet isn't kept). A copy older than the saved one is ignored.</summary>
    public void Save(ChatRecord chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (!ValidId(chat.Id) || !chat.Entries.Any(e => e.Kind == "user"))
        {
            return;
        }

        lock (_lock)
        {
            // Deleted (it can still be brought back): a late save from a turn that was ending doesn't revive it.
            if (File.Exists(Path.Combine(_deleted, chat.Id + ".json")))
            {
                return;
            }

            if (_index.TryGetValue(chat.Id, out var known))
            {
                if (chat.Revision < known.Revision)
                {
                    return;
                }

                if (known.Renamed)
                {
                    chat.Title = known.Summary.Title;
                    chat.Renamed = true;
                }
            }

            if (!chat.Renamed)
            {
                chat.Title = TitleOf(chat.Entries.First(e => e.Kind == "user").Text);
            }

            Write(chat);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <exception cref="KeyNotFoundException">There's no such chat.</exception>
    public void Rename(string id, string title)
    {
        var clean = Clean(title);
        if (clean.Length == 0)
        {
            throw new ArgumentException("Give the chat a name.", nameof(title));
        }

        lock (_lock)
        {
            if (!ValidId(id) || !_index.ContainsKey(id) || Read(PathOf(id)) is not { } chat)
            {
                throw new KeyNotFoundException("That chat isn't there any more.");
            }

            // Same revision: the chat being written to keeps saving, and keeps this name (see Save).
            chat.Title = clean.Length > TitleLength ? clean[..TitleLength] : clean;
            chat.Renamed = true;
            Write(chat);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            if (!ValidId(id) || !_index.Remove(id))
            {
                return false;
            }

            Directory.CreateDirectory(_deleted);
            var target = Path.Combine(_deleted, id + ".json");
            File.Move(PathOf(id), target, overwrite: true);
            File.SetLastWriteTimeUtc(target, _time.GetUtcNow().UtcDateTime);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Restore(string id)
    {
        lock (_lock)
        {
            var source = Path.Combine(_deleted, id + ".json");
            if (!ValidId(id) || !File.Exists(source) || Read(source) is not { } chat)
            {
                return false;
            }

            File.Move(source, PathOf(id), overwrite: true);
            _index[id] = (Summary(chat), chat.Renamed, chat.Revision);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Chats whose title or messages contain <paramref name="query"/>, newest first, with where it was said.</summary>
    public IReadOnlyList<ChatSummary> Search(string query)
    {
        var words = Clean(query ?? string.Empty);
        if (words.Length == 0)
        {
            return [];
        }

        var found = new List<ChatSummary>();
        foreach (var summary in List())
        {
            if (summary.Title.Contains(words, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(summary);
                continue;
            }

            var text = Load(summary.Id)?.Entries.Where(e => e.Kind is "user" or "agent").Select(e => e.Text).FirstOrDefault(t => t.Contains(words, StringComparison.OrdinalIgnoreCase));
            if (text is not null)
            {
                found.Add(summary with { Snippet = SnippetOf(Clean(text), words) });
            }
        }

        return found;
    }

    internal static string TitleOf(string text)
    {
        var clean = Clean(text);
        return clean.Length <= TitleLength ? clean : clean[..(TitleLength - 1)] + "…";
    }

    private static string SnippetOf(string text, string words)
    {
        var at = text.IndexOf(words, StringComparison.OrdinalIgnoreCase);
        var start = Math.Max(0, at - 40);
        var end = Math.Min(text.Length, at + words.Length + 60);
        return (start > 0 ? "…" : string.Empty) + text[start..end] + (end < text.Length ? "…" : string.Empty);
    }

    private static string Clean(string text) => Spaces().Replace(text, " ").Trim();

    private static bool ValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    private static ChatSummary Summary(ChatRecord chat) => new(chat.Id, chat.Title, chat.Agent, chat.UpdatedAt);

    private static ChatRecord? Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<ChatRecord>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private string PathOf(string id) => Path.Combine(_dir, id + ".json");

    private void Write(ChatRecord chat)
    {
        var path = PathOf(chat.Id);
        var temp = path + ".tmp";

        // A question can only be answered (and a tool only runs) while the app is at it: as kept, neither is.
        var kept = chat.Entries.Any(Unfinished)
            ? new ChatRecord
            {
                Id = chat.Id, Title = chat.Title, Renamed = chat.Renamed, Agent = chat.Agent, CreatedAt = chat.CreatedAt, UpdatedAt = chat.UpdatedAt,
                Revision = chat.Revision, AcpSessionId = chat.AcpSessionId, SignInFallback = chat.SignInFallback, Messages = chat.Messages,
                Entries = [.. chat.Entries.Select(e => Unfinished(e) ? e with { Status = "cancelled", Choices = null } : e)],
            }
            : chat;
        File.WriteAllText(temp, JsonSerializer.Serialize(kept, Json));
        File.Move(temp, path, overwrite: true);
        _index[chat.Id] = (Summary(chat), chat.Renamed, chat.Revision);
    }

    private static bool Unfinished(ChatEntry entry) => entry.Status is "waiting" or "pending" or "in_progress";

    private void PurgeDeleted()
    {
        if (!Directory.Exists(_deleted))
        {
            return;
        }

        var cutoff = (_time.GetUtcNow() - KeepDeleted).UtcDateTime;
        foreach (var file in Directory.EnumerateFiles(_deleted, "*.json"))
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff)
            {
                File.Delete(file);
            }
        }
    }

    [GeneratedRegex("^[a-z0-9]{8,32}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
