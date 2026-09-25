using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TodoTracker.Core;

namespace TodoTracker.Desktop;

/// <summary>Platform services the view model needs from the shell (WPF implements this; tests fake it).</summary>
public interface IDesktopShell
{
    void OpenUrl(string url);

    void CopyToClipboard(string text);

    void RunOnUi(Action action);

    string? Prompt(string title, string message, string? initialValue = null);

    bool Confirm(string message);
}

public sealed record SidebarOptions(string BaseUrl, string LaunchUrl, string McpConfigJson, TimeZoneInfo TimeZone, string ApiToken = "");

public enum ToastAction
{
    Open,
    Done,
    Snooze,
}

public sealed record SnoozeOption(string Label, Func<DateTimeOffset, TimeZoneInfo, int> Minutes)
{
    public static IReadOnlyList<SnoozeOption> Defaults { get; } =
    [
        new("15 min", (_, _) => 15),
        new("1 hour", (_, _) => 60),
        new("3 hours", (_, _) => 180),
        new("Tomorrow 9:00", MinutesUntilTomorrowMorning),
        new("+24 hours", (_, _) => 1440),
    ];

    private static int MinutesUntilTomorrowMorning(DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var wall = local.Date.AddDays(1).AddHours(9);
        var target = new DateTimeOffset(wall, zone.GetUtcOffset(wall));
        return Math.Max(1, (int)Math.Round((target - now).TotalMinutes));
    }
}

public sealed record SnoozeRequest(CardViewModel Card, SnoozeOption Option);

public sealed partial class CardViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string NoteDraft { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsNoteOpen { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Breadcrumb { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Meta { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PriorityColor { get; set; } = PriorityPalette.Normal;

    [ObservableProperty]
    public partial string PriorityLabel { get; set; } = "Normal";

    [ObservableProperty]
    public partial bool NeedsAttention { get; set; }

    [ObservableProperty]
    public partial Guid? ReminderId { get; set; }

    [ObservableProperty]
    public partial string? ReminderMessage { get; set; }

    [ObservableProperty]
    public partial bool IsWaiting { get; set; }

    [ObservableProperty]
    public partial bool CanComplete { get; set; }

    [ObservableProperty]
    public partial string? LastNote { get; set; }

    public Guid Id { get; init; }

    public bool HasBreadcrumb => Breadcrumb.Length > 0;

    internal void CopyFrom(CardViewModel other)
    {
        Title = other.Title;
        Breadcrumb = other.Breadcrumb;
        Meta = other.Meta;
        PriorityColor = other.PriorityColor;
        PriorityLabel = other.PriorityLabel;
        NeedsAttention = other.NeedsAttention;
        ReminderId = other.ReminderId;
        ReminderMessage = other.ReminderMessage;
        IsWaiting = other.IsWaiting;
        CanComplete = other.CanComplete;
        LastNote = other.LastNote;
        OnPropertyChanged(nameof(HasBreadcrumb));
    }
}

public sealed partial class GroupTabViewModel : ObservableObject
{
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial int Count { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Color { get; set; } = "#6366f1";

    [ObservableProperty]
    public partial bool HasAttention { get; set; }

    public Guid? Id { get; init; }

    public string CountText => Count > 0 ? Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
}

public sealed record WorkstreamViewModel(Guid Id, string Title, string PriorityColor, int ProgressPercent, string Summary);

public sealed record NoteViewModel(Guid ItemId, string Text, string ItemTitle, string Meta, string? SourceUrl);

public static class PriorityPalette
{
    public const string Low = "#94a3b8";
    public const string Normal = "#3b82f6";
    public const string High = "#f97316";
    public const string Critical = "#e11d48";

    public static string ColorOf(Priority priority) => priority switch
    {
        Priority.Low => Low,
        Priority.High => High,
        Priority.Critical => Critical,
        _ => Normal,
    };
}

public sealed partial class PomodoroViewModel : ObservableObject
{
    private PomodoroPhase _phase;
    private DateTimeOffset? _endsAt;
    private TimeSpan _remaining;

    [ObservableProperty]
    public partial string TimeText { get; set; } = "25:00";

    [ObservableProperty]
    public partial string Label { get; set; } = "Focus";

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool IsIdle { get; set; } = true;

    [ObservableProperty]
    public partial bool IsBreak { get; set; }

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    internal void Update(PomodoroTimer timer, string? itemTitle, DateTimeOffset now)
    {
        _phase = timer.Phase;
        _endsAt = timer.EndsAt;
        _remaining = timer.Remaining(now);
        IsRunning = timer.IsRunning;
        IsIdle = timer.Phase == PomodoroPhase.Idle;
        IsBreak = timer.Phase is PomodoroPhase.ShortBreak or PomodoroPhase.LongBreak;
        IsPaused = timer.Phase != PomodoroPhase.Idle && !timer.IsRunning;
        var phase = timer.Phase switch
        {
            PomodoroPhase.ShortBreak => "Break",
            PomodoroPhase.LongBreak => "Long break",
            _ => "Focus",
        };
        Label = itemTitle is not null && timer.Phase != PomodoroPhase.Idle ? $"{phase} · {itemTitle}" : phase;
        Tick(now);
    }

    internal void Tick(DateTimeOffset now)
    {
        var left = _endsAt is { } end && _phase != PomodoroPhase.Idle ? end - now : _remaining;
        if (left < TimeSpan.Zero)
        {
            left = TimeSpan.Zero;
        }

        var seconds = (int)Math.Ceiling(left.TotalSeconds);
        TimeText = $"{seconds / 60:00}:{seconds % 60:00}";
    }
}

internal static class CollectionSync
{
    /// <summary>Updates <paramref name="target"/> in place so bound UI keeps focus and drafts across refreshes.</summary>
    public static void SyncCards(ObservableCollection<CardViewModel> target, IReadOnlyList<CardViewModel> source)
    {
        var existing = target.ToDictionary(c => c.Id);
        var ordered = source.Select(s =>
        {
            if (existing.TryGetValue(s.Id, out var keep))
            {
                keep.CopyFrom(s);
                return keep;
            }

            return s;
        }).ToList();
        Sync(target, ordered);
    }

    public static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> ordered)
    {
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!ordered.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            var item = ordered[i];
            var index = target.IndexOf(item);
            if (index < 0)
            {
                target.Insert(i, item);
            }
            else if (index != i)
            {
                target.Move(index, i);
            }
        }
    }
}


