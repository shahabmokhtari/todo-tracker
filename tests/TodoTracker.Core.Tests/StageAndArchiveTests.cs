using TodoTracker.Core;

namespace TodoTracker.Core.Tests;

/// <summary>Where a task is on the board (Inbox, Next, Doing, Done), and putting finished tasks away (archive).</summary>
public class StageAndArchiveTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
    private readonly TaskBoard _board = new();

    private WorkItem Task(string title, Guid? parent = null) => _board.AddTask(new NewTask(title) { ParentId = parent }, Actor.User, T0);

    [Fact]
    public void New_tasks_land_in_the_inbox_and_move_along_the_board()
    {
        var item = Task("Plan trip");
        Assert.Equal(Stage.Inbox, item.Stage);

        _board.SetStage(item.Id, Stage.Doing, Actor.User, T0);

        Assert.Equal(Stage.Doing, item.Stage);
        Assert.Contains(_board.Activity, a => a.ItemId == item.Id && a.Summary.Contains("Doing", StringComparison.Ordinal));
    }

    [Fact]
    public void A_task_can_be_created_straight_into_a_stage()
    {
        var item = _board.AddTask(new NewTask("Fix bug") { Stage = Stage.Next }, Actor.User, T0);

        Assert.Equal(Stage.Next, item.Stage);
    }

    [Fact]
    public void Only_top_level_tasks_are_cards()
    {
        var parent = Task("Release");
        var step = Task("Roll out", parent.Id);

        Assert.Throws<InvalidOperationException>(() => _board.SetStage(step.Id, Stage.Doing, Actor.User, T0));
    }

    [Fact]
    public void Moving_a_done_card_back_reopens_it()
    {
        var item = Task("Plan trip");
        _board.Complete(item.Id, Actor.User, T0);

        _board.SetStage(item.Id, Stage.Next, Actor.User, T0);

        Assert.False(item.IsDone);
        Assert.Equal(Stage.Next, item.Stage);
    }

    [Fact]
    public void Finished_tasks_can_be_archived_and_brought_back()
    {
        var item = Task("Plan trip");
        Assert.Throws<InvalidOperationException>(() => _board.Archive(item.Id, Actor.User, T0));
        _board.Complete(item.Id, Actor.User, T0);

        _board.Archive(item.Id, Actor.User, T0.AddDays(1));
        Assert.Equal(T0.AddDays(1), item.ArchivedAt);

        _board.Unarchive(item.Id, Actor.User, T0.AddDays(2));
        Assert.Null(item.ArchivedAt);
        Assert.True(item.IsDone);
    }

    [Fact]
    public void Reopening_an_archived_task_takes_it_out_of_the_archive()
    {
        var item = Task("Plan trip");
        _board.Complete(item.Id, Actor.User, T0);
        _board.Archive(item.Id, Actor.User, T0);

        _board.Reopen(item.Id, Actor.User, T0);

        Assert.Null(item.ArchivedAt);
    }

    [Fact]
    public void Subtasks_are_archived_with_their_task_not_alone()
    {
        var parent = Task("Release");
        var step = Task("Roll out", parent.Id);
        _board.Complete(step.Id, Actor.User, T0);

        Assert.Throws<InvalidOperationException>(() => _board.Archive(step.Id, Actor.User, T0));
    }

    [Fact]
    public void Archiving_everything_done_before_a_date_keeps_open_and_recent_tasks()
    {
        var old = Task("Old");
        var recent = Task("Recent");
        var open = Task("Open");
        _board.Complete(old.Id, Actor.User, T0);
        _board.Complete(recent.Id, Actor.User, T0.AddDays(10));

        var archived = _board.ArchiveCompleted(doneBefore: T0.AddDays(5), groupId: null, Actor.User, T0.AddDays(11));

        Assert.Equal([old.Id], archived);
        Assert.Null(recent.ArchivedAt);
        Assert.Null(open.ArchivedAt);
    }

    [Fact]
    public void Archived_tasks_are_found_only_when_asked_for()
    {
        var item = Task("Old trip");
        _board.Complete(item.Id, Actor.User, T0);
        _board.Archive(item.Id, Actor.User, T0);
        var open = Task("New trip");

        Assert.Equal([open.Id], TaskQuery.Parse("trip").Apply(_board).Select(i => i.Id));
        Assert.Equal([item.Id], TaskQuery.Parse("trip is:archived").Apply(_board).Select(i => i.Id));
    }

    [Fact]
    public void Board_columns_can_be_searched()
    {
        var doing = _board.AddTask(new NewTask("Fix bug") { Stage = Stage.Doing }, Actor.User, T0);
        Task("Inbox thing");

        Assert.Equal([doing.Id], TaskQuery.Parse("is:doing").Apply(_board).Select(i => i.Id));
        Assert.Equal([doing.Id], TaskQuery.Parse("stage:Doing").Apply(_board).Select(i => i.Id));
    }

    [Fact]
    public void A_subtask_moved_to_the_top_becomes_a_card_in_next()
    {
        var parent = Task("Release");
        var step = Task("Roll out", parent.Id);

        _board.Move(step.Id, parentId: null, index: null, groupId: null, Actor.User, T0);

        Assert.Equal(Stage.Next, step.Stage);
    }
}
