using TodoTracker.Core;
using TodoTracker.Core.Vault;

namespace TodoTracker.Server;

// Wire contracts. Enums are sent as camelCase strings so every client (web, extension, MCP, Swift) sees the same values.
public sealed record LabelDto(string Name, string Color);

public sealed record AttachmentDto(Guid Id, Guid ItemId, string FileName, long Size, DateTimeOffset AddedAt, string AddedBy, string Url);

public sealed record GroupDto(Guid Id, string Name, string? Color, int Now, int Waiting, int Attention);

public sealed record CardDto(
    Guid Id,
    string Title,
    IReadOnlyList<string> Breadcrumb,
    string Priority,
    string OwnPriority,
    string State,
    bool NeedsAttention,
    Guid? ReminderId,
    string? ReminderMessage,
    DateTimeOffset? WakeAt,
    string? WakeIn,
    DateTimeOffset? Deadline,
    string? DeadlineIn,
    bool IsOverdue,
    Guid GroupId,
    bool HasChildren,
    int? StepNumber,
    int? StepCount,
    int NoteCount,
    string? LastNote,
    string? Details,
    IReadOnlyList<string> Tags,
    IReadOnlyList<LabelDto> Labels,
    int AttachmentCount);

public sealed record OverviewDto(
    Guid Id,
    string Title,
    string Priority,
    int DoneLeaves,
    int TotalLeaves,
    int ActionableCount,
    int WaitingCount,
    DateTimeOffset? NextWakeAt,
    string? NextWakeIn,
    Guid GroupId,
    bool HasChildren);

public sealed record NoteDto(Guid Id, Guid ItemId, string ItemTitle, DateTimeOffset At, string Text, string Author, string AuthorKind, string? SourceUrl, string? SourceTitle);

public sealed record ReminderDto(Guid Id, DateTimeOffset DueAt, string Message, string Kind, DateTimeOffset? NotifiedAt, DateTimeOffset? DismissedAt);

public sealed record ItemDto(
    Guid Id,
    string Title,
    string? Details,
    string Priority,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? Deadline,
    DateTimeOffset? NextActionAt,
    bool Sequential,
    int? StepDelayMinutes,
    Guid GroupId,
    Guid? ParentId,
    IReadOnlyList<string> Path,
    IReadOnlyList<ReminderDto> Reminders,
    IReadOnlyList<NoteDto> Notes,
    IReadOnlyList<ItemDto> Children,
    IReadOnlyList<string> Tags,
    IReadOnlyList<LabelDto> Labels,
    IReadOnlyList<AttachmentDto> Attachments,
    bool HasRich,
    string? File,
    string? ObsidianUrl);

/// <summary>A search result: enough to show and act on a task without loading its whole tree.</summary>
public sealed record SearchHitDto(
    Guid Id,
    string Title,
    IReadOnlyList<string> Breadcrumb,
    string State,
    string Priority,
    bool IsDone,
    DateTimeOffset? Deadline,
    DateTimeOffset? NextActionAt,
    Guid GroupId,
    Guid? ParentId,
    IReadOnlyList<string> Tags,
    IReadOnlyList<LabelDto> Labels,
    string? File);

public sealed record TimelineDto(DateTimeOffset At, Guid ItemId, string? ItemTitle, string Kind, string Summary, string Actor, string ActorKind);

public sealed record PomodoroDto(
    string Phase,
    bool Running,
    DateTimeOffset? EndsAt,
    int RemainingSeconds,
    Guid? ItemId,
    string? ItemTitle,
    int CompletedFocusCount,
    int FocusMinutes,
    int ShortBreakMinutes,
    int LongBreakMinutes);

public sealed record DashboardDto(
    DateTimeOffset ServerTime,
    Guid? GroupId,
    IReadOnlyList<GroupDto> Groups,
    CardDto? Focus,
    IReadOnlyList<CardDto> Now,
    IReadOnlyList<CardDto> Waiting,
    IReadOnlyList<OverviewDto> Overview,
    IReadOnlyList<NoteDto> RecentNotes,
    PomodoroDto Pomodoro,
    IReadOnlyList<LabelDto> Labels,
    IReadOnlyList<VaultProblem> Problems,
    string? Query);

public sealed record ReportDto(ItemDto? Item, IReadOnlyList<TimelineDto> Timeline);

public static class Wire
{
    public static string Of<T>(T value)
        where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static Priority ParsePriority(string? value, Priority fallback = Priority.Normal)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return Enum.TryParse<Priority>(value.Trim(), ignoreCase: true, out var p) && Enum.IsDefined(p)
            ? p
            : throw new ArgumentException($"Unknown priority \"{value}\". Use low, normal, high, or critical.", nameof(value));
    }

    public static DashboardDto Dashboard(TaskBoard board, DateTimeOffset now, Guid? groupId, int recentNotes = 5, VaultLinks? links = null, string? query = null)
    {
        if (groupId is { } g)
        {
            board.GetGroup(g);
        }

        var filter = string.IsNullOrWhiteSpace(query) ? null : TaskQuery.Parse(query);
        var snapshot = Agenda.Build(board, now, recentNotes, groupId, filter);
        return new DashboardDto(
            now,
            groupId,
            board.Groups.Select(gr =>
            {
                var c = snapshot.GroupCounts[gr.Id];
                return new GroupDto(gr.Id, gr.Name, gr.Color, c.Now, c.Waiting, c.Attention);
            }).ToList(),
            snapshot.Focus is { } f ? Card(f, now, board) : null,
            snapshot.Now.Select(e => Card(e, now, board)).ToList(),
            snapshot.Waiting.Select(e => Card(e, now, board)).ToList(),
            snapshot.Overview.Select(o => new OverviewDto(
                o.Item.Id,
                o.Item.Title,
                Of(o.TopPriority),
                o.DoneLeaves,
                o.TotalLeaves,
                o.ActionableCount,
                o.WaitingCount,
                o.NextWakeAt,
                o.NextWakeAt is { } w ? RelativeTime.Format(w, now) : null,
                o.Item.GroupId,
                o.Item.Children.Count > 0)).ToList(),
            snapshot.RecentNotes.Select(n => Note(n.Item, n.Note)).ToList(),
            Pomodoro(board, now),
            Labels(board),
            links?.Vault.Problems ?? [],
            string.IsNullOrWhiteSpace(query) ? null : query.Trim());
    }

    public static CardDto Card(AgendaEntry e, DateTimeOffset now, TaskBoard board)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(board);
        var item = e.Item;
        int? stepNumber = null;
        int? stepCount = null;
        if (item.Parent is { Sequential: true } parent)
        {
            stepNumber = parent.Children.ToList().IndexOf(item) + 1;
            stepCount = parent.Children.Count;
        }

        return new CardDto(
            item.Id,
            item.Title,
            e.Breadcrumb,
            Of(e.EffectivePriority),
            Of(item.Priority),
            Of(e.State),
            e.NeedsAttention,
            e.DueReminder?.Id,
            e.DueReminder?.Message,
            e.WakeAt,
            e.WakeAt is { } w ? RelativeTime.Format(w, now) : null,
            item.Deadline,
            item.Deadline is { } d ? RelativeTime.Format(d, now) : null,
            e.IsOverdue,
            item.GroupId,
            item.Children.Count > 0,
            stepNumber,
            stepCount,
            item.Notes.Count,
            item.Notes.Count > 0 ? item.Notes[^1].Text : null,
            item.Details,
            item.Tags,
            Labels(board, item),
            item.Attachments.Count);
    }

    public static ItemDto Item(WorkItem item, DateTimeOffset now, TaskBoard board, VaultLinks? links = null) => new(
        item.Id,
        item.Title,
        item.Details,
        Of(item.Priority),
        Of(Agenda.StateOf(item, now)),
        item.CreatedAt,
        item.CompletedAt,
        item.Deadline,
        item.NextActionAt,
        item.Sequential,
        item.StepDelay is { } s ? (int)Math.Round(s.TotalMinutes) : null,
        item.GroupId,
        item.Parent?.Id,
        item.Path,
        item.Reminders.Select(r => new ReminderDto(r.Id, r.DueAt, r.Message, Of(r.Kind), r.NotifiedAt, r.DismissedAt)).ToList(),
        item.Notes.OrderByDescending(n => n.At).Select(n => Note(item, n)).ToList(),
        item.Children.Select(c => Item(c, now, board, links)).ToList(),
        item.Tags,
        Labels(board, item),
        item.Attachments.Select(a => Attachment(item, a)).ToList(),
        links?.HasRich(item) ?? false,
        links?.FileOf(item.Id),
        links?.ObsidianUrlOf(item.Id));

    public static SearchHitDto SearchHit(WorkItem item, DateTimeOffset now, TaskBoard board, VaultLinks? links = null) => new(
        item.Id,
        item.Title,
        item.Ancestors().Reverse().Select(a => a.Title).ToList(),
        Of(Agenda.StateOf(item, now)),
        Of(Agenda.EffectivePriority(item)),
        item.IsDone,
        item.Deadline,
        item.NextActionAt,
        item.GroupId,
        item.Parent?.Id,
        item.Tags,
        Labels(board, item),
        links?.FileOf(item.Id));

    public static AttachmentDto Attachment(WorkItem item, Attachment attachment) =>
        new(attachment.Id, item.Id, attachment.FileName, attachment.Size, attachment.AddedAt, attachment.AddedBy.DisplayName, VaultLinks.AttachmentUrl(item.Id, attachment.Id));

    public static IReadOnlyList<LabelDto> Labels(TaskBoard board) => board.Labels.Select(l => new LabelDto(l.Name, l.Color)).ToList();

    public static IReadOnlyList<LabelDto> Labels(TaskBoard board, WorkItem item) =>
        item.Labels.Select(n => board.FindLabel(n) is { } l ? new LabelDto(l.Name, l.Color) : new LabelDto(n, "#64748b")).ToList();

    public static NoteDto Note(WorkItem item, Note note) =>
        new(note.Id, item.Id, item.Title, note.At, note.Text, note.Author.DisplayName, Of(note.Author.Kind), note.SourceUrl, note.SourceTitle);

    public static IReadOnlyList<TimelineDto> Timeline(TaskBoard board, Guid? itemId, int limit) =>
        board.Timeline(itemId)
            .Take(Math.Clamp(limit, 1, 1000))
            .Select(a => new TimelineDto(a.At, a.ItemId, board.Find(a.ItemId)?.Title, Of(a.Kind), a.Summary, a.Actor.DisplayName, Of(a.Actor.Kind)))
            .ToList();

    public static PomodoroDto Pomodoro(TaskBoard board, DateTimeOffset now)
    {
        var p = board.Pomodoro;
        return new PomodoroDto(
            Of(p.Phase),
            p.IsRunning,
            p.EndsAt,
            (int)Math.Ceiling(p.Remaining(now).TotalSeconds),
            p.ItemId,
            p.ItemId is { } id ? board.Find(id)?.Title : null,
            p.CompletedFocusCount,
            p.Settings.FocusMinutes,
            p.Settings.ShortBreakMinutes,
            p.Settings.LongBreakMinutes);
    }
}
