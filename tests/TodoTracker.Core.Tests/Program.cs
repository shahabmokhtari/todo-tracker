using TodoTracker.Apple;
using TodoTracker.Core;

var tests = new (string Name, Action Test)[]
{
    ("Waiting task is hidden until its next action time", WaitingTaskIsHiddenUntilDue),
    ("Future reminder waits and due reminder is actionable", FutureReminderWaitsUntilDue),
    ("Agenda prioritizes urgent due work", AgendaPrioritizesUrgentDueWork),
    ("Recent notes returns newest task notes first", RecentNotesReturnsNewestFirst),
    ("Pomodoro remaining time is clamped at zero", PomodoroRemainingIsClamped),
    ("Completed items are not actionable", CompletedItemsAreNotActionable),
    ("Reminder dismissal updates stored reminders", ReminderDismissalUpdatesStoredReminder),
    ("Apple shell projects agenda snapshots", AppleShellProjectsAgendaSnapshots),
    ("Recent notes handles non-positive counts", RecentNotesHandlesNonPositiveCounts),
    ("Core models validate invalid input", CoreModelsValidateInvalidInput)
};

foreach (var (name, test) in tests)
{
    test();
    Console.WriteLine($"PASS {name}");
}

static void WaitingTaskIsHiddenUntilDue()
{
    var now = new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);
    var board = new TaskBoard();
    var rollout = board.AddTask("Roll out feature X", TaskPriority.Critical);
    var featureB = rollout.AddSubtask("Feature B step 2", TaskPriority.High);

    featureB.ScheduleNextActionAfter(TimeSpan.FromHours(24), now);

    Assert(!board.Agenda(now.AddHours(23)).Contains(featureB), "Task should not be actionable before the wait period ends.");
    Assert(board.Waiting(now.AddHours(23)).Contains(featureB), "Task should remain in waiting list before the wait period ends.");
    Assert(board.Agenda(now.AddHours(24)).Contains(featureB), "Task should become actionable when the wait period ends.");
}

static void AgendaPrioritizesUrgentDueWork()
{
    var now = new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);
    var board = new TaskBoard();
    var normal = board.AddTask("Normal due work", TaskPriority.Normal);
    normal.AddReminder(now.AddMinutes(-10), "Normal reminder", now);
    var critical = board.AddTask("Critical due work", TaskPriority.Critical);
    critical.AddReminder(now.AddMinutes(-5), "Critical reminder", now);

    var agenda = board.Agenda(now);

    Assert(agenda[0] == critical, "Critical due work should sort ahead of normal due work.");
}

static void FutureReminderWaitsUntilDue()
{
    var now = new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);
    var board = new TaskBoard();
    var task = board.AddTask("Follow up on rollout", TaskPriority.High);

    task.AddReminder(now.AddHours(1), "Check rollout", now);

    Assert(!board.Agenda(now).Contains(task), "Future reminders should keep the task out of the agenda.");
    Assert(board.Waiting(now).Contains(task), "Future reminders should place the task in the waiting list.");
    Assert(board.Agenda(now.AddHours(1)).Contains(task), "Due reminders should make the task actionable.");
}

static void RecentNotesReturnsNewestFirst()
{
    var now = new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);
    var board = new TaskBoard();
    var first = board.AddTask("First");
    first.AddNote("Older note", now.AddMinutes(-30));
    var second = board.AddTask("Second");
    second.AddNote("Newer note", now.AddMinutes(-5));

    var recent = board.RecentNotes(2);

    Assert(recent[0] == second, "Newest note should appear first.");
    Assert(recent[1] == first, "Older note should appear second.");
}

static void PomodoroRemainingIsClamped()
{
    var now = new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);
    var pomodoro = new PomodoroSession(TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(5));
    pomodoro.Start(now);

    Assert(pomodoro.Remaining(now.AddMinutes(30)) == TimeSpan.Zero, "Elapsed Pomodoro should not return negative time.");
}

static void CompletedItemsAreNotActionable()
{
    var now = new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);
    var item = new WorkItem("Done task", TaskPriority.Critical);
    item.AddReminder(now.AddMinutes(-1), "Due reminder", now);

    item.Complete();

    Assert(!item.IsActionable(now), "Completed items should not be actionable even when reminders are due.");
}

static void ReminderDismissalUpdatesStoredReminder()
{
    var now = new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);
    var item = new WorkItem("Reminder task");
    item.Complete();

    item.AddReminder(now.AddMinutes(5), "Do not resurrect completed task", now);
    var reminder = item.Reminders[0];

    Assert(item.Status == WorkItemStatus.Completed, "Adding a reminder should not resurrect a completed item.");
    Assert(item.DismissReminder(reminder), "Existing reminder should be dismissed.");
    Assert(item.Reminders[0].IsDismissed, "Dismissed reminder should be stored back on the task.");
    Assert(!item.DismissReminder(reminder), "The original reminder value should no longer match after dismissal.");
}

static void AppleShellProjectsAgendaSnapshots()
{
    var now = new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);
    var board = new TaskBoard();
    var task = board.AddTask("Check iOS glance", TaskPriority.High);
    task.AddReminder(now.AddMinutes(-1), "Show on Apple shell", now);
    var shell = new AppleTaskShell(board);

    var agenda = shell.Agenda(now);

    Assert(agenda.Count == 1, "Apple shell should expose agenda snapshots.");
    Assert(agenda[0].Title == "Check iOS glance", "Apple snapshot should preserve task title.");
    Assert(agenda[0].AccessibilityLabel.Contains("High priority", StringComparison.Ordinal), "Apple snapshot should include priority in accessibility label.");
}

static void RecentNotesHandlesNonPositiveCounts()
{
    var board = new TaskBoard();
    var item = board.AddTask("Task with note");
    item.AddNote("Note", DateTimeOffset.UtcNow);

    Assert(board.RecentNotes(0).Count == 0, "Zero recent-note count should return no items.");
    Assert(board.RecentNotes(-1).Count == 0, "Negative recent-note count should return no items.");
}

static void CoreModelsValidateInvalidInput()
{
    AssertThrows<ArgumentException>(() => _ = new WorkItem(" "));

    var item = new WorkItem("Valid task");
    AssertThrows<ArgumentException>(() => item.AddNote(" ", DateTimeOffset.UtcNow));
    AssertThrows<ArgumentException>(() => item.AddReminder(DateTimeOffset.UtcNow, " "));
    AssertThrows<ArgumentOutOfRangeException>(() => item.ScheduleNextActionAfter(TimeSpan.FromSeconds(-1), DateTimeOffset.UtcNow));
    AssertThrows<ArgumentOutOfRangeException>(() => _ = new PomodoroSession(TimeSpan.Zero, TimeSpan.FromMinutes(5)));
    AssertThrows<ArgumentOutOfRangeException>(() => _ = new PomodoroSession(TimeSpan.FromMinutes(25), TimeSpan.Zero));
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertThrows<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}
