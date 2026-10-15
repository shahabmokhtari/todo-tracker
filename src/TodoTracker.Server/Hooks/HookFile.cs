using System.Text.Json;
using System.Text.Json.Serialization;

namespace TodoTracker.Server.Hooks;

/// <summary>
/// <c>hooks.json</c> in the app's data folder: what runs when something happens. It lives on this computer only (not in
/// the synced tasks folder) and nothing in the app writes it (no API, no AI tool), so only the person at this computer
/// decides what runs. Same shape as Copilot's hooks: <c>{"version":1,"hooks":{"taskCompleted":[{...}],"*":[{...}]}}</c>.
/// </summary>
public sealed class HookFile
{
    public int Version { get; set; } = 1;

    /// <summary>Event name (or <c>*</c> for every event) → what to run.</summary>
    public Dictionary<string, List<HookSpec>> Hooks { get; set; } = new(StringComparer.Ordinal);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>What a new hooks.json could start from (shown in Settings).</summary>
    public const string Sample = """
        {
          "version": 1,
          "hooks": {
            "taskCompleted": [
              {
                "type": "command",
                "powershell": "$e = [Console]::In.ReadToEnd() | ConvertFrom-Json; Add-Content \"$HOME\\done.log\" \"$($e.at) $($e.task.title)\"",
                "bash": "jq -r '.at + \" \" + .task.title' >> ~/done.log",
                "timeoutSec": 30,
                "match": { "groups": ["Work"], "actors": ["user"] }
              }
            ],
            "reminderDue": [
              { "type": "http", "url": "https://example.com/webhook", "headers": { "Authorization": "Bearer …" } }
            ]
          }
        }
        """;
}

public sealed class HookSpec
{
    /// <summary>Its handle for editing (written by the app; one written by hand without it gets one from its content).</summary>
    public string? Id { get; set; }

    /// <summary><c>false</c>: switched off (kept, doesn't run).</summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// Added or changed by an AI agent (or another app with the API token): it doesn't run until you approve it in
    /// Settings › Hooks or with <c>tt hooks approve</c>.
    /// </summary>
    public bool? NeedsApproval { get; set; }

    /// <summary>Who added or last changed it (an agent's name, or <c>you</c>).</summary>
    public string? AddedBy { get; set; }

    public DateTimeOffset? AddedAt { get; set; }

    /// <summary>New (random) with every change to what it does: what an approval names, so it can't be guessed.</summary>
    public string? Revision { get; set; }

    /// <summary>Whether it runs: switched on, and approved if an agent added or changed it.</summary>
    [JsonIgnore]
    public bool IsActive => Enabled != false && NeedsApproval != true;

    /// <summary><c>on</c>, <c>off</c>, or <c>needs approval</c>.</summary>
    [JsonIgnore]
    public string State => NeedsApproval == true ? "needs approval" : Enabled == false ? "off" : "on";

    /// <summary><c>command</c> (runs a program) or <c>http</c> (posts the event).</summary>
    public string Type { get; set; } = "command";

    /// <summary>Run with PowerShell on Windows (pwsh when installed).</summary>
    public string? Powershell { get; set; }

    /// <summary>Run with bash on Linux and macOS.</summary>
    public string? Bash { get; set; }

    public string? Cwd { get; set; }

    public int? TimeoutSec { get; set; }

    [JsonConverter(typeof(VerbatimKeys))]
    public Dictionary<string, string>? Env { get; set; }

    public string? Url { get; set; }

    [JsonConverter(typeof(VerbatimKeys))]
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>Only for these; a task-less event (a break) runs only when nothing about a task is asked.</summary>
    public HookMatch? Match { get; set; }

    /// <summary>A short name for the recent runs list (default: what it runs).</summary>
    public string? Name { get; set; }

    public const int DefaultTimeoutSec = 30;
    public const int MaxTimeoutSec = 600;

    [JsonIgnore]
    public TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(TimeoutSec ?? DefaultTimeoutSec, 1, MaxTimeoutSec));

    /// <summary>The script for this computer, or null when there's none for it.</summary>
    [JsonIgnore]
    public string? Script => OperatingSystem.IsWindows() ? Powershell : Bash;

    [JsonIgnore]
    public string Label => Name ?? (Type == "http" ? Url : Script) ?? Type;

    /// <summary>Why this hook can't run, or null when it can.</summary>
    public string? Problem()
    {
        if (Type == "http")
        {
            return Uri.TryCreate(Url, UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
                ? null
                : "An http hook needs a \"url\" (http or https).";
        }

        if (Type != "command")
        {
            return $"\"{Type}\" isn't a hook type (use \"command\" or \"http\").";
        }

        return Powershell is null && Bash is null ? "A command hook needs \"powershell\" (Windows) or \"bash\" (Linux, macOS)." : null;
    }

    public bool Matches(TaskEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (Match is not { } m)
        {
            return true;
        }

        if (m.Actors is { Count: > 0 } actors && !actors.Contains(e.Actor.Kind, StringComparer.OrdinalIgnoreCase) && (e.Actor.Name is null || !actors.Contains(e.Actor.Name, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        var aboutTask = m.Groups is { Count: > 0 } || m.Tags is { Count: > 0 } || m.Labels is { Count: > 0 };
        if (!aboutTask)
        {
            return true;
        }

        if (e.Task is not { } task)
        {
            return false;
        }

        return (m.Groups is not { Count: > 0 } groups || (task.Group is not null && groups.Contains(task.Group, StringComparer.OrdinalIgnoreCase)))
            && (m.Tags is not { Count: > 0 } tags || tags.Any(t => task.Tags.Contains(t.TrimStart('#'), StringComparer.OrdinalIgnoreCase)))
            && (m.Labels is not { Count: > 0 } labels || labels.Any(l => task.Labels.Contains(l, StringComparer.OrdinalIgnoreCase)));
    }
}

/// <summary>
/// Names written exactly as given (variable names are case-sensitive): not changed by a serializer's dictionary key
/// policy (the API's turns keys into camelCase).
/// </summary>
public sealed class VerbatimKeys : JsonConverter<Dictionary<string, string>>
{
    public override Dictionary<string, string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected names and values.");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString() ?? throw new JsonException("A name is missing.");
            reader.Read();
            result[name] = reader.TokenType == JsonTokenType.String ? reader.GetString()! : throw new JsonException($"\"{name}\" needs a text value.");
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, string> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        foreach (var (name, text) in value)
        {
            writer.WriteString(name, text);
        }

        writer.WriteEndObject();
    }
}

/// <summary>Every list given must match (any one of its values).</summary>
public sealed class HookMatch
{
    public List<string>? Groups { get; set; }

    public List<string>? Tags { get; set; }

    public List<string>? Labels { get; set; }

    /// <summary><c>user</c>, <c>agent</c>, <c>vault</c>, <c>system</c>, or an agent's name.</summary>
    public List<string>? Actors { get; set; }
}
