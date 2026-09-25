using TodoTracker.Core;

var tests = new (string Name, Action Test)[]
{
    ("Waiting task is hidden until its next action time", WaitingTaskIsHiddenUntilDue),
    ("Agenda prioritizes urgent due work", AgendaPrioritizesUrgentDueWork),
    ("Recent notes returns newest task notes first", RecentNotesReturnsNewestFirst),
    ("Pomodoro remaining time is clamped at zero", PomodoroRemainingIsClamped)
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
    normal.AddReminder(now.AddMinutes(-10), "Normal reminder");
    var critical = board.AddTask("Critical due work", TaskPriority.Critical);
    critical.AddReminder(now.AddMinutes(-5), "Critical reminder");

    var agenda = board.Agenda(now);

    Assert(agenda[0] == critical, "Critical due work should sort ahead of normal due work.");
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

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
