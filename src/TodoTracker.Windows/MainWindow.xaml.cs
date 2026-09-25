using System.Windows;
using System.Windows.Media;
using TodoTracker.Core;

namespace TodoTracker.Windows;

public partial class MainWindow : Window
{
    private DesktopSidebarHost? _sidebarHost;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = SidebarViewModel.CreateSample();
        SourceInitialized += (_, _) =>
        {
            _sidebarHost = new DesktopSidebarHost(this);
            _sidebarHost.ReserveRightEdge();
        };
        Closed += (_, _) => _sidebarHost?.Dispose();
    }

    private sealed record SidebarViewModel(
        IReadOnlyList<TaskCard> AgendaItems,
        IReadOnlyList<TaskCard> WaitingItems,
        IReadOnlyList<NoteCard> RecentNotes,
        string PomodoroLabel)
    {
        public static SidebarViewModel CreateSample()
        {
            var now = DateTimeOffset.Now;
            var board = new TaskBoard();
            var rollout = board.AddTask("Roll out feature X", TaskPriority.Critical);
            rollout.SetDetail("Coordinate staged rollout while keeping each dependent step visible only when actionable.");
            rollout.AddNote("Feature A rollout completed; wait 24 hours before feature B validation.", now.AddMinutes(-35));

            var featureB = rollout.AddSubtask("Feature B: validate next rollout step", TaskPriority.High);
            featureB.SetDetail("Check telemetry, notify Teams channel, and schedule the next 24-hour gate.");
            featureB.AddReminder(now.AddMinutes(-5), "Time to validate feature B rollout.");

            var tomorrow = rollout.AddSubtask("Feature B: stage 2 rollout", TaskPriority.High);
            tomorrow.ScheduleNextActionAfter(TimeSpan.FromHours(24), now);

            var report = board.AddTask("Write handoff notes", TaskPriority.Normal);
            report.AddNote("Browser report should show full timeline; sidebar shows this recent note.", now.AddMinutes(-12));

            var pomodoro = new PomodoroSession(TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(5));
            pomodoro.Start(now.AddMinutes(-7));

            return new SidebarViewModel(
                board.Agenda(now).Select(TaskCard.From).ToList(),
                board.Waiting(now).Select(TaskCard.From).ToList(),
                board.RecentNotes(3).Select(NoteCard.From).ToList(),
                $"{(int)pomodoro.Remaining(now).TotalMinutes} minutes of focus remaining");
        }
    }

    private sealed record TaskCard(string Title, string? Detail, string DueLabel, Brush PriorityBrush)
    {
        public static TaskCard From(WorkItem item)
        {
            var dueAt = item.EffectiveDueAt();
            return new TaskCard(
                item.Title,
                item.Detail,
                dueAt is null ? "No deadline" : $"Next: {dueAt.Value.LocalDateTime:g}",
                PriorityBrushFor(item.Priority));
        }

        private static Brush PriorityBrushFor(TaskPriority priority)
        {
            return priority switch
            {
                TaskPriority.Critical => new SolidColorBrush(Color.FromRgb(190, 24, 93)),
                TaskPriority.High => new SolidColorBrush(Color.FromRgb(194, 65, 12)),
                TaskPriority.Normal => new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                _ => new SolidColorBrush(Color.FromRgb(71, 85, 105))
            };
        }
    }

    private sealed record NoteCard(string Title, string LatestNote)
    {
        public static NoteCard From(WorkItem item) => new(item.Title, item.Notes[^1].Text);
    }
}