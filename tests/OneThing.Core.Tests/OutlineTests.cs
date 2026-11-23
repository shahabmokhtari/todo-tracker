namespace OneThing.Core.Tests;

/// <summary>The Tasks tree's rules (Outline): what rows show, the zoom and its path, Open to, and trimmed titles.</summary>
public sealed class OutlineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    private readonly TaskBoard _board = new();

    private WorkItem Add(string title, WorkItem? parent = null, Guid? group = null, bool sequential = false) =>
        _board.AddTask(new NewTask(title) { ParentId = parent?.Id, GroupId = group, Sequential = sequential }, Actor.User, T0);

    private Guid Work => _board.Groups[0].Id;

    private Guid Personal => _board.Groups[1].Id;

    private IReadOnlyList<OutlineRow> Rows(OutlineOptions options) => Outline.Rows(_board, options, T0);

    private static OutlineOptions Open(params WorkItem[] items) => new() { Expanded = items.Select(i => i.Id).ToHashSet() };

    [Fact]
    public void Under_all_groups_the_top_level_is_the_groups_in_order_with_their_tasks()
    {
        var errand = Add("Buy milk", group: Personal);
        var report = Add("Write the report");

        var rows = Rows(new OutlineOptions());

        Assert.Equal(["Work", "Write the report", "Personal", "Buy milk"], rows.Select(r => r.Title));
        Assert.Equal([0, 1, 0, 1], rows.Select(r => r.Depth));
        Assert.Equal([OutlineRowKind.Group, OutlineRowKind.Task, OutlineRowKind.Group, OutlineRowKind.Task], rows.Select(r => r.Kind));
        Assert.Equal([Work, report.Id, Personal, errand.Id], rows.Select(r => r.Id));
    }

    [Fact]
    public void A_closed_group_hides_its_tasks_and_an_empty_group_still_shows()
    {
        Add("Write the report");
        var empty = _board.AddGroup("Someday", null, Actor.User, T0);

        var rows = Rows(new OutlineOptions { ClosedGroups = new HashSet<Guid> { Work } });

        Assert.Equal(["Work", "Personal", "Someday"], rows.Select(r => r.Title));
        Assert.False(rows[0].Expanded);
        Assert.True(rows[0].HasChildren);
        Assert.False(rows[2].HasChildren);
        Assert.Equal(empty.Id, rows[2].Id);
    }

    [Fact]
    public void With_one_group_picked_there_are_no_group_rows()
    {
        Add("Write the report");
        Add("Buy milk", group: Personal);

        var rows = Rows(new OutlineOptions { GroupId = Work });

        Assert.Equal(["Write the report"], rows.Select(r => r.Title));
        Assert.Equal(0, rows[0].Depth);
    }

    [Fact]
    public void Parents_count_leaves_not_the_subtasks_that_have_their_own()
    {
        var launch = Add("Launch");
        var copy = Add("Copy", launch);
        Add("Hero", copy);
        var faq = Add("FAQ", copy);
        Add("Domain", launch);
        _board.Complete(faq.Id, Actor.User, T0);
        Add("Loose");

        var rows = Rows(Open(launch, copy));
        var byTitle = rows.ToDictionary(r => r.Title);

        Assert.Equal((1, 3), (byTitle["Launch"].Done, byTitle["Launch"].Total));
        Assert.Equal((1, 2), (byTitle["Copy"].Done, byTitle["Copy"].Total));
        Assert.Equal(0, byTitle["Hero"].Total);
        // The group: 3 leaves under Launch, and Loose (no subtasks) counts as one.
        Assert.Equal((1, 4, 3), (byTitle["Work"].Done, byTitle["Work"].Total, byTitle["Work"].Left));
    }

    [Fact]
    public void Can_do_now_keeps_what_can_be_done_with_its_parents_and_leaves_empty_groups_out()
    {
        var trip = Add("Trip", group: Personal);
        var flights = Add("Flights", trip);
        _board.ScheduleNextAction(flights.Id, T0.AddDays(1), Actor.User, T0);
        var launch = Add("Launch");
        Add("Copy", launch);

        var rows = Rows(Open(launch, trip) with { Show = OutlineShow.Now });

        Assert.Equal(["Work", "Launch", "Copy"], rows.Select(r => r.Title));
    }

    [Fact]
    public void Waiting_keeps_snoozed_tasks_and_later_steps_with_their_parents_closed_unless_open()
    {
        var rollout = Add("Roll out", sequential: true);
        Add("Ring 0", rollout);
        Add("Ring 1", rollout);
        var call = Add("Call");
        _board.ScheduleNextAction(call.Id, T0.AddDays(1), Actor.User, T0);
        Add("Ready");

        Assert.Equal(["Work", "Roll out", "Call"], Rows(new OutlineOptions { Show = OutlineShow.Waiting }).Select(r => r.Title));
        Assert.Equal(["Work", "Roll out", "Ring 1", "Call"], Rows(Open(rollout) with { Show = OutlineShow.Waiting }).Select(r => r.Title));
    }

    [Fact]
    public void A_search_shows_the_way_to_a_match_open_without_changing_what_is_open()
    {
        var launch = Add("Launch");
        var copy = Add("Copy", launch);
        Add("Hero section", copy);
        Add("Trip", group: Personal);

        var rows = Rows(new OutlineOptions { Query = "hero", ClosedGroups = new HashSet<Guid> { Work } });

        Assert.Equal(["Work", "Launch", "Copy", "Hero section"], rows.Select(r => r.Title));
        Assert.All(rows.Take(3), r => Assert.True(r.Expanded));
    }

    [Fact]
    public void A_zoom_shows_what_is_under_the_task_with_a_path_back()
    {
        var launch = Add("Launch the site");
        var copy = Add("Launch the site: Write the copy", launch);
        Add("Hero", copy);
        var options = Open(copy) with { ZoomId = launch.Id };

        var rows = Rows(options);
        var path = Outline.Path(_board, options, T0);

        Assert.Equal(["Write the copy", "Hero"], rows.Select(r => r.Title));
        Assert.Equal([0, 1], rows.Select(r => r.Depth));
        Assert.False(rows[0].CanOutdent);
        Assert.True(rows[1].CanOutdent);
        Assert.Equal(["All groups", "Work", "Launch the site"], path.Select(p => p.Title));
        Assert.Equal((launch.Id, Work), Outline.NewTaskPlace(_board, options, T0));
    }

    [Fact]
    public void A_zoom_on_a_task_that_is_no_longer_shown_goes_out_to_its_nearest_shown_parent()
    {
        var trip = Add("Trip", group: Personal);
        var packing = Add("Packing", trip);
        var sunscreen = Add("Sunscreen", packing);
        Add("Flights", trip);
        _board.Complete(packing.Id, Actor.User, T0);

        Assert.Equal(trip.Id, Outline.ResolveZoom(_board, new OutlineOptions { ZoomId = packing.Id }, T0));
        Assert.Equal(packing.Id, Outline.ResolveZoom(_board, new OutlineOptions { ZoomId = packing.Id, Show = OutlineShow.All }, T0));
        Assert.Null(Outline.ResolveZoom(_board, new OutlineOptions { ZoomId = trip.Id, GroupId = Work }, T0));
        Assert.Null(Outline.ResolveZoom(_board, new OutlineOptions { ZoomId = Guid.NewGuid() }, T0));
        // A task without subtasks can't be zoomed: its parent is.
        Assert.Equal(packing.Id, Outline.ResolveZoom(_board, new OutlineOptions { ZoomId = sunscreen.Id, Show = OutlineShow.All }, T0));
    }

    [Fact]
    public void Open_to_sets_how_deep_the_tree_is_open_and_leaves_other_groups_alone()
    {
        var launch = Add("Launch");
        var copy = Add("Copy", launch);
        Add("Hero", copy);
        var trip = Add("Trip", group: Personal);
        Add("Flights", trip);
        var options = new OutlineOptions { GroupId = Work, Expanded = new HashSet<Guid> { trip.Id, copy.Id } };

        Assert.Equal([trip.Id], Outline.OpenTo(_board, options, OutlineLevel.Tasks, T0).Expanded);
        Assert.Equal(new HashSet<Guid> { trip.Id, launch.Id }, Outline.OpenTo(_board, options, OutlineLevel.Subtasks, T0).Expanded);
        Assert.Equal(new HashSet<Guid> { trip.Id, launch.Id, copy.Id }, Outline.OpenTo(_board, options, OutlineLevel.All, T0).Expanded);

        var groups = Outline.OpenTo(_board, new OutlineOptions(), OutlineLevel.Groups, T0);
        Assert.Empty(groups.Expanded);
        Assert.Equal(_board.Groups.Select(g => g.Id).ToHashSet(), groups.ClosedGroups);
        Assert.Empty(Outline.OpenTo(_board, new OutlineOptions { ClosedGroups = groups.ClosedGroups }, OutlineLevel.Tasks, T0).ClosedGroups);
    }

    [Theory]
    [InlineData("Publisher accounts: Pick the contact phone number", "Publisher accounts", "Pick the contact phone number")]
    [InlineData("Launch - Buy the domain", "Launch", "Buy the domain")]
    [InlineData("Launch – Buy the domain", "Launch", "Buy the domain")]
    [InlineData("Launch — Buy the domain", "Launch", "Buy the domain")]
    [InlineData("LAUNCH:   shout", "launch", "shout")]
    [InlineData("Launch:", "Launch", "Launch:")]
    [InlineData("Launch:   ", "Launch", "Launch:   ")]
    [InlineData("Launcher: other", "Launch", "Launcher: other")]
    [InlineData("Launch-pad", "Launch", "Launch-pad")]
    [InlineData("Launch: copy", null, "Launch: copy")]
    public void A_subtask_does_not_repeat_its_parents_name(string title, string? parent, string shown) =>
        Assert.Equal(shown, Outline.ShownTitle(title, parent));
}
