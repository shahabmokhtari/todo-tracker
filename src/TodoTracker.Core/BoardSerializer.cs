using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TodoTracker.Core;

/// <summary>
/// Versioned JSON persistence format (schema v1). The format is shared with the Swift core, so
/// changes here must be mirrored in <c>apple/Sources/TodoTrackerKit/BoardDocument.swift</c>.
/// Timestamps are always written as UTC ISO-8601 with millisecond precision.
/// </summary>
public static class BoardSerializer
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(TaskBoard board)
    {
        ArgumentNullException.ThrowIfNull(board);
        return JsonSerializer.Serialize(ToDocument(board), Options);
    }

    public static TaskBoard Deserialize(string json)
    {
        BoardDocument doc;
        try
        {
            doc = JsonSerializer.Deserialize<BoardDocument>(json, Options) ?? throw new InvalidDataException("Board file is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Board file is not valid JSON: " + ex.Message, ex);
        }

        if (doc.SchemaVersion > TaskBoard.CurrentSchemaVersion)
        {
            throw new NotSupportedException($"Board schema v{doc.SchemaVersion} is newer than this app supports (v{TaskBoard.CurrentSchemaVersion}). Please update the app.");
        }

        return FromDocument(doc);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new UtcDateTimeOffsetConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static BoardDocument ToDocument(TaskBoard board) => new()
    {
        SchemaVersion = TaskBoard.CurrentSchemaVersion,
        Groups = board.Groups.Select(g => new GroupDocument { Id = g.Id, Name = g.Name, Color = g.Color }).ToList(),
        Items = board.Items.Select(ToDocument).ToList(),
        Activity = board.Activity.Select(a => new ActivityDocument { At = a.At, ItemId = a.ItemId, Kind = a.Kind, Summary = a.Summary, Actor = ToDocument(a.Actor) }).ToList(),
        Pomodoro = new PomodoroDocument
        {
            Settings = new PomodoroSettingsDocument
            {
                FocusMinutes = board.Pomodoro.Settings.FocusMinutes,
                ShortBreakMinutes = board.Pomodoro.Settings.ShortBreakMinutes,
                LongBreakMinutes = board.Pomodoro.Settings.LongBreakMinutes,
                FocusesBeforeLongBreak = board.Pomodoro.Settings.FocusesBeforeLongBreak,
            },
            Phase = board.Pomodoro.Phase,
            EndsAt = board.Pomodoro.EndsAt,
            PausedRemainingSeconds = board.Pomodoro.PausedRemaining is { } r ? (int)Math.Round(r.TotalSeconds) : null,
            ItemId = board.Pomodoro.ItemId,
            CompletedFocusCount = board.Pomodoro.CompletedFocusCount,
        },
    };

    private static ItemDocument ToDocument(WorkItem item) => new()
    {
        Id = item.Id,
        Title = item.Title,
        Details = item.Details,
        Priority = item.Priority,
        CreatedAt = item.CreatedAt,
        CompletedAt = item.CompletedAt,
        Deadline = item.Deadline,
        NextActionAt = item.NextActionAt,
        Sequential = item.Sequential ? true : null,
        StepDelayMinutes = item.StepDelay is { } d ? (int)Math.Round(d.TotalMinutes) : null,
        GroupId = item.Parent is null ? item.OwnGroupId : null,
        Reminders = item.Reminders.Count == 0 ? null : item.Reminders.Select(r => new ReminderDocument { Id = r.Id, DueAt = r.DueAt, Message = r.Message, Kind = r.Kind, NotifiedAt = r.NotifiedAt, DismissedAt = r.DismissedAt }).ToList(),
        Notes = item.Notes.Count == 0 ? null : item.Notes.Select(n => new NoteDocument { Id = n.Id, At = n.At, Text = n.Text, Author = ToDocument(n.Author), SourceUrl = n.SourceUrl, SourceTitle = n.SourceTitle }).ToList(),
        Children = item.Children.Count == 0 ? null : item.Children.Select(ToDocument).ToList(),
    };

    private static ActorDocument ToDocument(Actor actor) => new() { Kind = actor.Kind, Name = actor.Name };

    private static TaskBoard FromDocument(BoardDocument doc)
    {
        var board = new TaskBoard(seedDefaultGroups: false);
        foreach (var g in doc.Groups ?? [])
        {
            if (!string.IsNullOrWhiteSpace(g.Name) && !board.Groups.Any(x => x.Id == g.Id))
            {
                board.AddLoadedGroup(new TaskGroup(g.Id, g.Name.Trim(), g.Color));
            }
        }

        if (board.Groups.Count == 0)
        {
            board.SeedDefaultGroups();
        }

        foreach (var item in doc.Items ?? [])
        {
            Load(board, item, null);
        }

        foreach (var a in doc.Activity ?? [])
        {
            board.AddLoadedActivity(new ActivityEntry(a.At, a.ItemId, a.Kind, a.Summary ?? string.Empty, FromDocument(a.Actor)));
        }

        if (doc.Pomodoro is { } p)
        {
            var s = p.Settings;
            board.Pomodoro = new PomodoroTimer(s is null ? null : new PomodoroSettings(s.FocusMinutes, s.ShortBreakMinutes, s.LongBreakMinutes, s.FocusesBeforeLongBreak));
            board.Pomodoro.Restore(p.Phase, p.EndsAt, p.PausedRemainingSeconds is { } secs ? TimeSpan.FromSeconds(secs) : null, p.ItemId, p.CompletedFocusCount);
        }

        return board;
    }

    private static void Load(TaskBoard board, ItemDocument doc, WorkItem? parent)
    {
        var item = new WorkItem(doc.Id, TaskBoard.RequireText(doc.Title, "title", TaskBoard.MaxTitleLength, $"Task {doc.Id} has no title."), doc.Priority, doc.CreatedAt)
        {
            Details = doc.Details,
            CompletedAt = doc.CompletedAt,
            Deadline = doc.Deadline,
            NextActionAt = doc.NextActionAt,
            Sequential = doc.Sequential ?? false,
            StepDelay = doc.StepDelayMinutes is > 0 and var m ? TimeSpan.FromMinutes(m) : null,
            OwnGroupId = doc.GroupId is { } g && board.Groups.Any(x => x.Id == g) ? g : board.DefaultGroupId,
        };
        foreach (var r in doc.Reminders ?? [])
        {
            item.ReminderList.Add(new Reminder(r.Id, r.DueAt, r.Message ?? string.Empty, r.Kind) { NotifiedAt = r.NotifiedAt, DismissedAt = r.DismissedAt });
        }

        foreach (var n in doc.Notes ?? [])
        {
            item.NoteList.Add(new Note(n.Id, n.At, n.Text ?? string.Empty, FromDocument(n.Author), n.SourceUrl, n.SourceTitle));
        }

        board.Attach(item, parent);
        foreach (var child in doc.Children ?? [])
        {
            Load(board, child, item);
        }
    }

    private static Actor FromDocument(ActorDocument? doc) => doc is null ? Actor.User : new Actor(doc.Kind, doc.Name);

    private sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTimeOffset.Parse(reader.GetString() ?? throw new JsonException("Expected a timestamp."), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
    }

#pragma warning disable CA1812 // Instantiated by System.Text.Json.
    private sealed class BoardDocument
    {
        public int SchemaVersion { get; set; } = 1;

        public List<GroupDocument>? Groups { get; set; }

        public List<ItemDocument>? Items { get; set; }

        public List<ActivityDocument>? Activity { get; set; }

        public PomodoroDocument? Pomodoro { get; set; }
    }

    private sealed class GroupDocument
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string? Color { get; set; }
    }

    private sealed class ItemDocument
    {
        public Guid Id { get; set; }

        public string Title { get; set; } = string.Empty;

        public string? Details { get; set; }

        public Priority Priority { get; set; } = Priority.Normal;

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset? CompletedAt { get; set; }

        public DateTimeOffset? Deadline { get; set; }

        public DateTimeOffset? NextActionAt { get; set; }

        public bool? Sequential { get; set; }

        public int? StepDelayMinutes { get; set; }

        public Guid? GroupId { get; set; }

        public List<ReminderDocument>? Reminders { get; set; }

        public List<NoteDocument>? Notes { get; set; }

        public List<ItemDocument>? Children { get; set; }
    }

    private sealed class ReminderDocument
    {
        public Guid Id { get; set; }

        public DateTimeOffset DueAt { get; set; }

        public string? Message { get; set; }

        public ReminderKind Kind { get; set; }

        public DateTimeOffset? NotifiedAt { get; set; }

        public DateTimeOffset? DismissedAt { get; set; }
    }

    private sealed class NoteDocument
    {
        public Guid Id { get; set; }

        public DateTimeOffset At { get; set; }

        public string? Text { get; set; }

        public ActorDocument? Author { get; set; }

        public string? SourceUrl { get; set; }

        public string? SourceTitle { get; set; }
    }

    private sealed class ActorDocument
    {
        public ActorKind Kind { get; set; }

        public string? Name { get; set; }
    }

    private sealed class ActivityDocument
    {
        public DateTimeOffset At { get; set; }

        public Guid ItemId { get; set; }

        public ActivityKind Kind { get; set; }

        public string? Summary { get; set; }

        public ActorDocument? Actor { get; set; }
    }

    private sealed class PomodoroDocument
    {
        public PomodoroSettingsDocument? Settings { get; set; }

        public PomodoroPhase Phase { get; set; }

        public DateTimeOffset? EndsAt { get; set; }

        public int? PausedRemainingSeconds { get; set; }

        public Guid? ItemId { get; set; }

        public int CompletedFocusCount { get; set; }
    }

    private sealed class PomodoroSettingsDocument
    {
        public int FocusMinutes { get; set; } = 25;

        public int ShortBreakMinutes { get; set; } = 5;

        public int LongBreakMinutes { get; set; } = 15;

        public int FocusesBeforeLongBreak { get; set; } = 4;
    }
#pragma warning restore CA1812
}
