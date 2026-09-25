using TodoTracker.Core;

namespace TodoTracker.Core.Tests;

public class TaskGroupTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
    private readonly TaskBoard _board = new();

    [Fact]
    public void New_board_has_default_work_and_personal_groups()
    {
        Assert.Equal(["Work", "Personal"], _board.Groups.Select(g => g.Name));
        Assert.Equal(_board.Groups[0].Id, _board.DefaultGroupId);
    }

    [Fact]
    public void Root_tasks_default_to_the_first_group_and_children_inherit()
    {
        var root = _board.AddTask(new NewTask("Feature X"), Actor.User, T0);
        var child = _board.AddTask(new NewTask("Step") { ParentId = root.Id }, Actor.User, T0);

        Assert.Equal(_board.DefaultGroupId, root.GroupId);
        Assert.Equal(_board.DefaultGroupId, child.GroupId);
    }

    [Fact]
    public void Tasks_can_be_created_in_a_specific_group()
    {
        var personal = _board.Groups[1];

        var item = _board.AddTask(new NewTask("Dentist") { GroupId = personal.Id }, Actor.User, T0);

        Assert.Equal(personal.Id, item.GroupId);
    }

    [Fact]
    public void Unknown_group_is_rejected()
    {
        Assert.Throws<TaskNotFoundException>(() => _board.AddTask(new NewTask("x") { GroupId = Guid.NewGuid() }, Actor.User, T0));
    }

    [Fact]
    public void Groups_can_be_added_renamed_and_recolored()
    {
        var group = _board.AddGroup(" Side project ", "#22c55e", Actor.User, T0);

        _board.UpdateGroup(group.Id, "Hobby", "#f97316", Actor.User, T0);

        Assert.Equal("Hobby", group.Name);
        Assert.Equal("#f97316", group.Color);
        Assert.Equal(3, _board.Groups.Count);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("Ok", "red")]
    [InlineData("Ok", "#12345")]
    public void Invalid_group_name_or_color_is_rejected(string name, string? color)
    {
        Assert.Throws<ArgumentException>(() => _board.AddGroup(name, color, Actor.User, T0));
    }

    [Fact]
    public void Duplicate_group_names_are_rejected_case_insensitively()
    {
        Assert.Throws<ArgumentException>(() => _board.AddGroup("work", null, Actor.User, T0));
    }

    [Fact]
    public void Deleting_a_group_moves_its_tasks_to_the_target_group()
    {
        var personal = _board.Groups[1];
        var item = _board.AddTask(new NewTask("Dentist") { GroupId = personal.Id }, Actor.User, T0);

        _board.DeleteGroup(personal.Id, _board.DefaultGroupId, Actor.User, T0);

        Assert.Single(_board.Groups);
        Assert.Equal(_board.DefaultGroupId, item.GroupId);
    }

    [Fact]
    public void The_last_group_cannot_be_deleted()
    {
        _board.DeleteGroup(_board.Groups[1].Id, _board.Groups[0].Id, Actor.User, T0);

        Assert.Throws<InvalidOperationException>(() => _board.DeleteGroup(_board.Groups[0].Id, _board.Groups[0].Id, Actor.User, T0));
    }

    [Fact]
    public void Moving_a_root_task_moves_its_subtree()
    {
        var root = _board.AddTask(new NewTask("Feature X"), Actor.User, T0);
        var child = _board.AddTask(new NewTask("Step") { ParentId = root.Id }, Actor.User, T0);
        var personal = _board.Groups[1];

        _board.MoveToGroup(root.Id, personal.Id, Actor.User, T0);

        Assert.Equal(personal.Id, child.GroupId);
    }

    [Fact]
    public void Only_root_tasks_can_change_group()
    {
        var root = _board.AddTask(new NewTask("Feature X"), Actor.User, T0);
        var child = _board.AddTask(new NewTask("Step") { ParentId = root.Id }, Actor.User, T0);

        Assert.Throws<InvalidOperationException>(() => _board.MoveToGroup(child.Id, _board.Groups[1].Id, Actor.User, T0));
    }

    [Fact]
    public void Dashboard_can_be_filtered_by_group_and_reports_counts_per_group()
    {
        var work = _board.Groups[0];
        var personal = _board.Groups[1];
        var w = _board.AddTask(new NewTask("Work thing"), Actor.User, T0);
        var p = _board.AddTask(new NewTask("Personal thing") { GroupId = personal.Id }, Actor.User, T0);
        var p2 = _board.AddTask(new NewTask("Personal later") { GroupId = personal.Id }, Actor.User, T0);
        _board.ScheduleNextAction(p2.Id, T0.AddHours(1), Actor.User, T0);

        var all = Agenda.Build(_board, T0);
        var onlyPersonal = Agenda.Build(_board, T0, groupId: personal.Id);

        Assert.Equal(2, all.Now.Count);
        Assert.Equal([p.Id], onlyPersonal.Now.Select(e => e.Item.Id));
        Assert.Equal([p2.Id], onlyPersonal.Waiting.Select(e => e.Item.Id));
        Assert.All(onlyPersonal.Overview, o => Assert.Equal(personal.Id, o.Item.GroupId));
        Assert.Equal(1, all.GroupCounts[work.Id].Now);
        Assert.Equal(1, all.GroupCounts[personal.Id].Now);
        Assert.Equal(1, all.GroupCounts[personal.Id].Waiting);
        Assert.Equal(1, onlyPersonal.GroupCounts[work.Id].Now);
        Assert.Contains(w.Id, all.Now.Select(e => e.Item.Id));
    }

    [Fact]
    public void Groups_round_trip_through_serialization_and_legacy_boards_get_defaults()
    {
        var personal = _board.Groups[1];
        _board.AddTask(new NewTask("Dentist") { GroupId = personal.Id }, Actor.User, T0);

        var restored = BoardSerializer.Deserialize(BoardSerializer.Serialize(_board));
        var legacy = BoardSerializer.Deserialize("""{"schemaVersion":1,"items":[{"id":"6f1f3c8e-0000-4000-8000-000000000001","title":"T","priority":"low","createdAt":"2026-01-05T09:00:00Z"}]}""");

        Assert.Equal(personal.Id, restored.Items[0].GroupId);
        Assert.Equal(["Work", "Personal"], restored.Groups.Select(g => g.Name));
        Assert.Equal(legacy.DefaultGroupId, legacy.Items[0].GroupId);
    }
}
