using TodoTracker.Core;

namespace TodoTracker.Core.Tests;

public class TaskBoardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
    private readonly TaskBoard _board = new();

    [Fact]
    public void AddTask_trims_title_and_logs_created_activity()
    {
        var item = _board.AddTask(new NewTask("  Roll out feature X  ") { Priority = Priority.Critical }, Actor.User, T0);

        Assert.Equal("Roll out feature X", item.Title);
        Assert.Equal(Priority.Critical, item.Priority);
        Assert.Equal(T0, item.CreatedAt);
        Assert.Single(_board.Items);
        var entry = Assert.Single(_board.Activity);
        Assert.Equal(ActivityKind.Created, entry.Kind);
        Assert.Equal(item.Id, entry.ItemId);
        Assert.Equal(Actor.User, entry.Actor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddTask_rejects_blank_title(string title)
    {
        Assert.Throws<ArgumentException>(() => _board.AddTask(new NewTask(title), Actor.User, T0));
        Assert.Empty(_board.Items);
        Assert.Empty(_board.Activity);
    }

    [Fact]
    public void AddTask_with_parent_nests_item_and_exposes_path()
    {
        var parent = _board.AddTask(new NewTask("Feature X"), Actor.User, T0);
        var child = _board.AddTask(new NewTask("Feature A") { ParentId = parent.Id }, Actor.User, T0);

        Assert.Same(parent, child.Parent);
        Assert.Same(child, Assert.Single(parent.Children));
        Assert.Equal(["Feature X", "Feature A"], child.Path);
        Assert.Single(_board.Items);
        Assert.Equal(2, _board.AllItems().Count());
    }

    [Fact]
    public void AddTask_with_unknown_parent_throws_not_found()
    {
        Assert.Throws<TaskNotFoundException>(() =>
            _board.AddTask(new NewTask("Orphan") { ParentId = Guid.NewGuid() }, Actor.User, T0));
    }

    [Fact]
    public void AddTask_rejects_non_positive_step_delay()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _board.AddTask(new NewTask("Rollout") { Sequential = true, StepDelay = TimeSpan.Zero }, Actor.User, T0));
    }

    [Fact]
    public void AddNote_records_author_source_and_activity()
    {
        var item = _board.AddTask(new NewTask("Feature A"), Actor.User, T0);
        var agent = Actor.Agent("copilot");

        var note = _board.AddNote(item.Id, " Enabled ring 1 ", agent, T0.AddMinutes(5), "https://example.test/pr/1", "PR 1");

        Assert.Equal("Enabled ring 1", note.Text);
        Assert.Equal(agent, note.Author);
        Assert.Equal("https://example.test/pr/1", note.SourceUrl);
        Assert.Equal("PR 1", note.SourceTitle);
        Assert.Same(note, Assert.Single(item.Notes));
        var entry = _board.Activity[^1];
        Assert.Equal(ActivityKind.NoteAdded, entry.Kind);
        Assert.Equal("Enabled ring 1", entry.Summary);
        Assert.Equal(agent, entry.Actor);
    }

    [Fact]
    public void AddNote_rejects_empty_text()
    {
        var item = _board.AddTask(new NewTask("Feature A"), Actor.User, T0);
        Assert.Throws<ArgumentException>(() => _board.AddNote(item.Id, " ", Actor.User, T0));
    }

    [Fact]
    public void AddNote_rejects_non_http_source_url()
    {
        var item = _board.AddTask(new NewTask("Feature A"), Actor.User, T0);
        Assert.Throws<ArgumentException>(() => _board.AddNote(item.Id, "x", Actor.User, T0, "javascript:alert(1)"));
    }

    [Fact]
    public void Complete_sets_completion_and_cascades_to_descendants()
    {
        var parent = _board.AddTask(new NewTask("Feature X"), Actor.User, T0);
        var child = _board.AddTask(new NewTask("Step") { ParentId = parent.Id }, Actor.User, T0);

        _board.Complete(parent.Id, Actor.User, T0.AddHours(1));

        Assert.Equal(T0.AddHours(1), parent.CompletedAt);
        Assert.True(child.IsDone);
        Assert.Equal(ActivityKind.Completed, _board.Activity[^1].Kind);
    }

    [Fact]
    public void Complete_is_idempotent_for_done_items()
    {
        var item = _board.AddTask(new NewTask("Once"), Actor.User, T0);
        _board.Complete(item.Id, Actor.User, T0);
        var count = _board.Activity.Count;

        _board.Complete(item.Id, Actor.User, T0.AddHours(1));

        Assert.Equal(T0, item.CompletedAt);
        Assert.Equal(count, _board.Activity.Count);
    }

    [Fact]
    public void Completing_a_step_gates_the_next_step_by_the_step_delay()
    {
        var rollout = _board.AddTask(new NewTask("Feature A") { Sequential = true, StepDelay = TimeSpan.FromHours(24) }, Actor.User, T0);
        var step1 = _board.AddTask(new NewTask("Step 1") { ParentId = rollout.Id }, Actor.User, T0);
        var step2 = _board.AddTask(new NewTask("Step 2") { ParentId = rollout.Id }, Actor.User, T0);

        _board.Complete(step1.Id, Actor.User, T0.AddHours(2));

        Assert.Equal(T0.AddHours(26), step2.NextActionAt);
        Assert.Contains(_board.Activity, a => a.Kind == ActivityKind.Scheduled && a.ItemId == step2.Id);
    }

    [Fact]
    public void Completing_the_last_step_completes_the_sequence()
    {
        var rollout = _board.AddTask(new NewTask("Feature A") { Sequential = true }, Actor.User, T0);
        var step1 = _board.AddTask(new NewTask("Step 1") { ParentId = rollout.Id }, Actor.User, T0);
        var step2 = _board.AddTask(new NewTask("Step 2") { ParentId = rollout.Id }, Actor.User, T0);

        _board.Complete(step1.Id, Actor.User, T0);
        Assert.False(rollout.IsDone);
        _board.Complete(step2.Id, Actor.User, T0.AddHours(1));

        Assert.Equal(T0.AddHours(1), rollout.CompletedAt);
    }

    [Fact]
    public void Completing_the_last_child_of_a_plain_parent_leaves_parent_open()
    {
        var parent = _board.AddTask(new NewTask("Feature X"), Actor.User, T0);
        var child = _board.AddTask(new NewTask("Only child") { ParentId = parent.Id }, Actor.User, T0);

        _board.Complete(child.Id, Actor.User, T0);

        Assert.False(parent.IsDone);
    }

    [Fact]
    public void Completing_a_step_keeps_a_later_existing_gate()
    {
        var rollout = _board.AddTask(new NewTask("Feature A") { Sequential = true, StepDelay = TimeSpan.FromHours(1) }, Actor.User, T0);
        var step1 = _board.AddTask(new NewTask("Step 1") { ParentId = rollout.Id }, Actor.User, T0);
        var step2 = _board.AddTask(new NewTask("Step 2") { ParentId = rollout.Id }, Actor.User, T0);
        _board.ScheduleNextAction(step2.Id, T0.AddDays(3), Actor.User, T0);

        _board.Complete(step1.Id, Actor.User, T0);

        Assert.Equal(T0.AddDays(3), step2.NextActionAt);
    }

    [Fact]
    public void Completing_a_locked_step_out_of_order_is_rejected()
    {
        var rollout = _board.AddTask(new NewTask("Feature A") { Sequential = true }, Actor.User, T0);
        _board.AddTask(new NewTask("Step 1") { ParentId = rollout.Id }, Actor.User, T0);
        var step2 = _board.AddTask(new NewTask("Step 2") { ParentId = rollout.Id }, Actor.User, T0);

        var error = Assert.Throws<InvalidOperationException>(() => _board.Complete(step2.Id, Actor.User, T0));
        Assert.Contains("Step 1", error.Message, StringComparison.Ordinal);
        Assert.False(step2.IsDone);
    }

    [Fact]
    public void Reopen_clears_completion_and_reopens_done_ancestors()
    {
        var parent = _board.AddTask(new NewTask("Feature X"), Actor.User, T0);
        var child = _board.AddTask(new NewTask("Step") { ParentId = parent.Id }, Actor.User, T0);
        _board.Complete(parent.Id, Actor.User, T0);

        _board.Reopen(child.Id, Actor.User, T0.AddHours(1));

        Assert.False(child.IsDone);
        Assert.False(parent.IsDone);
        Assert.Equal(ActivityKind.Reopened, _board.Activity[^1].Kind);
    }

    [Fact]
    public void ScheduleNextAction_with_notify_adds_reminder_at_the_same_time()
    {
        var item = _board.AddTask(new NewTask("Feature B"), Actor.User, T0);

        _board.ScheduleNextAction(item.Id, T0.AddDays(1), Actor.User, T0, notify: true, message: "Continue rollout");

        Assert.Equal(T0.AddDays(1), item.NextActionAt);
        var reminder = Assert.Single(item.Reminders);
        Assert.Equal(T0.AddDays(1), reminder.DueAt);
        Assert.Equal("Continue rollout", reminder.Message);
    }

    [Fact]
    public void ScheduleNextAction_replaces_pending_schedule_reminders_instead_of_stacking()
    {
        var item = _board.AddTask(new NewTask("Feature B"), Actor.User, T0);
        _board.ScheduleNextAction(item.Id, T0.AddHours(1), Actor.User, T0, notify: true);

        _board.ScheduleNextAction(item.Id, T0.AddHours(5), Actor.User, T0, notify: true);

        var pending = item.Reminders.Where(r => r.DismissedAt is null).ToList();
        Assert.Equal(T0.AddHours(5), Assert.Single(pending).DueAt);
    }

    [Fact]
    public void ClearNextAction_removes_the_gate()
    {
        var item = _board.AddTask(new NewTask("Feature B"), Actor.User, T0);
        _board.ScheduleNextAction(item.Id, T0.AddDays(1), Actor.User, T0);

        _board.ClearNextAction(item.Id, Actor.User, T0);

        Assert.Null(item.NextActionAt);
    }

    [Fact]
    public void AddReminder_uses_default_message_when_blank()
    {
        var item = _board.AddTask(new NewTask("Feature B"), Actor.User, T0);

        var reminder = _board.AddReminder(item.Id, T0.AddHours(1), null, Actor.User, T0);

        Assert.Equal("Time to act on: Feature B", reminder.Message);
        Assert.Equal(ActivityKind.ReminderAdded, _board.Activity[^1].Kind);
    }

    [Fact]
    public void DismissReminder_marks_it_dismissed_and_unknown_ids_throw()
    {
        var item = _board.AddTask(new NewTask("Feature B"), Actor.User, T0);
        var reminder = _board.AddReminder(item.Id, T0, "Now", Actor.User, T0);

        _board.DismissReminder(item.Id, reminder.Id, Actor.User, T0.AddMinutes(1));

        Assert.Equal(T0.AddMinutes(1), reminder.DismissedAt);
        Assert.Throws<TaskNotFoundException>(() => _board.DismissReminder(item.Id, Guid.NewGuid(), Actor.User, T0));
    }

    [Fact]
    public void Update_changes_fields_and_logs_once()
    {
        var item = _board.AddTask(new NewTask("Feature B") { Deadline = T0.AddDays(2) }, Actor.User, T0);

        _board.Update(item.Id, new TaskChanges { Title = "Feature B rollout", Details = "ring 0-3", Priority = Priority.High, ClearDeadline = true }, Actor.User, T0);

        Assert.Equal("Feature B rollout", item.Title);
        Assert.Equal("ring 0-3", item.Details);
        Assert.Equal(Priority.High, item.Priority);
        Assert.Null(item.Deadline);
        Assert.Equal(ActivityKind.Updated, _board.Activity[^1].Kind);
    }

    [Fact]
    public void Update_with_blank_title_is_rejected_without_partial_changes()
    {
        var item = _board.AddTask(new NewTask("Feature B"), Actor.User, T0);

        Assert.Throws<ArgumentException>(() => _board.Update(item.Id, new TaskChanges { Title = " ", Priority = Priority.High }, Actor.User, T0));

        Assert.Equal(Priority.Normal, item.Priority);
    }

    [Fact]
    public void Delete_removes_subtree_and_logs()
    {
        var parent = _board.AddTask(new NewTask("Feature X"), Actor.User, T0);
        var child = _board.AddTask(new NewTask("Step") { ParentId = parent.Id }, Actor.User, T0);

        _board.Delete(child.Id, Actor.User, T0);
        Assert.Empty(parent.Children);

        _board.Delete(parent.Id, Actor.User, T0);
        Assert.Empty(_board.Items);
        Assert.Null(_board.Find(parent.Id));
        Assert.Equal(ActivityKind.Deleted, _board.Activity[^1].Kind);
    }

    [Fact]
    public void AddSteps_creates_sequential_steps_with_delay()
    {
        var rollout = _board.AddTask(new NewTask("Feature A"), Actor.User, T0);

        var steps = _board.AddSteps(rollout.Id, ["Ring 0", "Ring 1", " "], TimeSpan.FromHours(24), Actor.User, T0);

        Assert.Equal(["Ring 0", "Ring 1"], steps.Select(s => s.Title));
        Assert.True(rollout.Sequential);
        Assert.Equal(TimeSpan.FromHours(24), rollout.StepDelay);
        Assert.Equal(2, rollout.Children.Count);
    }

    [Fact]
    public void AddSteps_requires_at_least_one_title()
    {
        var rollout = _board.AddTask(new NewTask("Feature A"), Actor.User, T0);
        Assert.Throws<ArgumentException>(() => _board.AddSteps(rollout.Id, [" "], null, Actor.User, T0));
    }

    [Fact]
    public void Timeline_returns_subtree_activity_newest_first()
    {
        var parent = _board.AddTask(new NewTask("Feature X"), Actor.User, T0);
        var child = _board.AddTask(new NewTask("Step") { ParentId = parent.Id }, Actor.User, T0.AddMinutes(1));
        var other = _board.AddTask(new NewTask("Unrelated"), Actor.User, T0.AddMinutes(2));
        _board.AddNote(child.Id, "did it", Actor.User, T0.AddMinutes(3));

        var timeline = _board.Timeline(parent.Id);

        Assert.Equal([ActivityKind.NoteAdded, ActivityKind.Created, ActivityKind.Created], timeline.Select(t => t.Kind));
        Assert.DoesNotContain(timeline, t => t.ItemId == other.Id);
        Assert.Equal(4, _board.Timeline().Count);
    }

    [Fact]
    public void Operations_on_unknown_items_throw_not_found()
    {
        var id = Guid.NewGuid();
        Assert.Throws<TaskNotFoundException>(() => _board.Complete(id, Actor.User, T0));
        Assert.Throws<TaskNotFoundException>(() => _board.AddNote(id, "x", Actor.User, T0));
        Assert.Throws<TaskNotFoundException>(() => _board.Get(id));
    }

    [Fact]
    public void Actor_display_names_are_readable()
    {
        Assert.Equal("You", Actor.User.DisplayName);
        Assert.Equal("Agent: copilot", Actor.Agent("copilot").DisplayName);
        Assert.Equal("Browser", new Actor(ActorKind.Browser).DisplayName);
    }
}
