using TodoTracker.Core.Connectors;

namespace TodoTracker.Core.Tests;

/// <summary>Two-way sync with an outside list (Notion, Microsoft To Do): what to create, update and archive.</summary>
public sealed class ConnectorPlanTests
{
    private static readonly ConnectorFields All = ConnectorFields.Title | ConnectorFields.Done | ConnectorFields.Due | ConnectorFields.Priority | ConnectorFields.Tags | ConnectorFields.Notes;
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static SyncedFields F(string title, bool done = false, DateOnly? due = null, Priority priority = Priority.Normal, string[]? tags = null, string? notes = null) =>
        new(title, done, due, priority, tags ?? [], notes);

    private static LocalItem Local(Guid id, SyncedFields fields, bool discoverable = true, bool archived = false) => new(id, fields, discoverable, archived);

    private static Guid ImportId(string remoteId) => ConnectorPlan.ImportId("notion", remoteId);

    private static ConnectorPlanResult Plan(
        IEnumerable<LocalItem>? local = null,
        IEnumerable<RemoteItem>? remote = null,
        IEnumerable<ConnectorLink>? links = null,
        ConnectorDirection direction = ConnectorDirection.Both,
        ConnectorFields fields = ConnectorFields.Title | ConnectorFields.Done | ConnectorFields.Due | ConnectorFields.Priority | ConnectorFields.Tags | ConnectorFields.Notes) =>
        ConnectorPlan.Make(new ConnectorPlanInput("notion", [.. local ?? []], [.. remote ?? []], [.. links ?? []], direction, fields));

    [Fact]
    public void First_run_sends_new_local_tasks_and_brings_in_open_remote_ones_without_matching_titles()
    {
        var result = Plan(
            local: [Local(A, F("Write report"))],
            remote: [new RemoteItem("r1", F("Write report"), null), new RemoteItem("r2", F("Old thing", done: true), null)]);

        Assert.Equal(
            [
                new CreateLocal(ImportId("r1"), "r1", F("Write report")),
                new MarkRemote("r1", ImportId("r1")),
                new CreateRemote(A, F("Write report")),
            ],
            result.Ops);
    }

    [Fact]
    public void The_direction_decides_which_side_new_tasks_are_created_on()
    {
        var local = new[] { Local(A, F("Mine")) };
        var remote = new[] { new RemoteItem("r1", F("Theirs"), null) };

        Assert.DoesNotContain(Plan(local, remote, direction: ConnectorDirection.ImportOnly).Ops, o => o is CreateRemote);
        Assert.DoesNotContain(Plan(local, remote, direction: ConnectorDirection.ExportOnly).Ops, o => o is CreateLocal);
    }

    [Fact]
    public void A_remote_item_marked_with_a_task_links_to_it_instead_of_creating_a_copy()
    {
        var result = Plan(local: [Local(A, F("Report", due: new DateOnly(2026, 1, 9)))], remote: [new RemoteItem("r1", F("Report"), A)]);

        // No snapshot to compare with: the task here wins and the remote side is brought in line.
        Assert.Equal([new UpdateRemote("r1", A, F("Report", due: new DateOnly(2026, 1, 9)))], result.Ops);
        Assert.Equal([new ConnectorLink(A, "r1", F("Report", due: new DateOnly(2026, 1, 9)))], result.Links);
    }

    [Fact]
    public void An_import_that_was_cut_short_is_finished_not_repeated()
    {
        // The task was created (with its import id) but the app stopped before the link and mark were saved.
        var id = ImportId("r1");
        var result = Plan(local: [Local(id, F("Theirs"))], remote: [new RemoteItem("r1", F("Theirs"), null)]);

        Assert.Equal([new MarkRemote("r1", id)], result.Ops);
        Assert.Equal([new ConnectorLink(id, "r1", F("Theirs"))], result.Links);
    }

    [Fact]
    public void Each_field_takes_the_side_that_changed_and_the_task_here_wins_when_both_did()
    {
        var before = F("Report", due: new DateOnly(2026, 1, 9), priority: Priority.Normal);
        var local = F("Report v2", due: new DateOnly(2026, 1, 9), priority: Priority.High);
        var remote = F("Quarterly report", due: new DateOnly(2026, 1, 12), priority: Priority.Normal);

        var result = Plan(local: [Local(A, local)], remote: [new RemoteItem("r1", remote, A)], links: [new ConnectorLink(A, "r1", before)]);

        var merged = F("Report v2", due: new DateOnly(2026, 1, 12), priority: Priority.High);
        Assert.Equal([new UpdateLocal(A, merged), new UpdateRemote("r1", A, merged)], result.Ops);
        Assert.Equal(merged, Assert.Single(result.Links).Snapshot);
    }

    [Fact]
    public void Finishing_on_either_side_finishes_on_the_other()
    {
        var before = F("Report");
        var fromRemote = Plan(local: [Local(A, before)], remote: [new RemoteItem("r1", F("Report", done: true), A)], links: [new ConnectorLink(A, "r1", before)]);
        Assert.Equal([new UpdateLocal(A, F("Report", done: true))], fromRemote.Ops);

        var fromHere = Plan(local: [Local(A, F("Report", done: true))], remote: [new RemoteItem("r1", before, A)], links: [new ConnectorLink(A, "r1", before)]);
        Assert.Equal([new UpdateRemote("r1", A, F("Report", done: true))], fromHere.Ops);
    }

    [Fact]
    public void Fields_the_other_side_doesnt_have_stay_as_they_are_here()
    {
        // Microsoft To Do has no tags: the task's tags are neither sent nor cleared.
        var withTags = F("Report", tags: ["work"]);
        var result = Plan(
            local: [Local(A, withTags)],
            remote: [new RemoteItem("r1", F("Report"), A)],
            links: [new ConnectorLink(A, "r1", withTags)],
            fields: All & ~ConnectorFields.Tags);

        Assert.Empty(result.Ops);
    }

    [Fact]
    public void A_task_deleted_here_is_archived_there_once_and_remembered()
    {
        var link = new ConnectorLink(A, "r1", F("Report"));
        var first = Plan(remote: [new RemoteItem("r1", F("Report"), A)], links: [link]);

        Assert.Equal([new ArchiveRemote("r1")], first.Ops);
        Assert.Equal(LinkState.LocalDeleted, Assert.Single(first.Links).State);

        // Archived there (gone from Notion's list, or finished in To Do): nothing more, and it's not brought back.
        Assert.Empty(Plan(links: first.Links).Ops);
        Assert.Empty(Plan(remote: [new RemoteItem("r1", F("Report", done: true), A)], links: first.Links).Ops);
    }

    [Fact]
    public void A_deleted_task_comes_back_with_its_id_when_it_is_changed_there_afterwards()
    {
        var gone = new ConnectorLink(A, "r1", F("Report"), LinkState.LocalDeleted);

        var result = Plan(remote: [new RemoteItem("r1", F("Report, again"), A)], links: [gone]);

        Assert.Equal([new CreateLocal(A, "r1", F("Report, again"))], result.Ops);
        Assert.Equal(LinkState.Active, Assert.Single(result.Links).State);
    }

    [Fact]
    public void A_remote_item_that_is_gone_leaves_the_task_here_alone_and_can_come_back()
    {
        var link = new ConnectorLink(A, "r1", F("Report"));
        var gone = Plan(local: [Local(A, F("Report"))], links: [link]);

        Assert.Empty(gone.Ops);
        Assert.Equal(LinkState.RemoteGone, Assert.Single(gone.Links).State);

        var back = Plan(local: [Local(A, F("Report"))], remote: [new RemoteItem("r1", F("Report (restored)"), A)], links: gone.Links);
        Assert.Equal([new UpdateLocal(A, F("Report (restored)"))], back.Ops);
        Assert.Equal(LinkState.Active, Assert.Single(back.Links).State);
    }

    [Fact]
    public void A_linked_task_moved_to_another_group_or_under_another_task_keeps_syncing()
    {
        var before = F("Report");
        var result = Plan(
            local: [Local(A, F("Report (moved)"), discoverable: false)],
            remote: [new RemoteItem("r1", before, A)],
            links: [new ConnectorLink(A, "r1", before)]);

        Assert.Equal([new UpdateRemote("r1", A, F("Report (moved)"))], result.Ops);
    }

    [Fact]
    public void Archived_tasks_and_tasks_outside_the_group_are_left_out()
    {
        var before = F("Report", done: true);
        var archivedLinked = Plan(
            local: [Local(A, before, archived: true)],
            remote: [new RemoteItem("r1", F("Report"), A)],
            links: [new ConnectorLink(A, "r1", before)]);
        Assert.Empty(archivedLinked.Ops);

        Assert.Empty(Plan(local: [Local(B, F("Elsewhere"), discoverable: false)]).Ops);
    }

    [Fact]
    public void A_remote_item_marked_with_a_task_that_isnt_here_is_left_alone()
    {
        // Deleted here before this computer knew the link, or not synced to this computer yet.
        Assert.Empty(Plan(remote: [new RemoteItem("r1", F("Report"), A)]).Ops);
    }

    [Fact]
    public void Running_again_after_everything_went_through_changes_nothing()
    {
        var local = new List<LocalItem> { Local(A, F("Mine", due: new DateOnly(2026, 2, 1))) };
        var remote = new List<RemoteItem> { new("r1", F("Theirs", priority: Priority.High), null) };
        var first = Plan(local, remote);

        // What the sync would have done:
        var links = ConnectorPlan.Commit([], first, first.Ops.Select(op => new ConnectorOpResult(op, op is CreateRemote ? "r2" : null)).ToList());
        local.Add(Local(ImportId("r1"), F("Theirs", priority: Priority.High)));
        remote[0] = remote[0] with { Marker = ImportId("r1") };
        remote.Add(new RemoteItem("r2", F("Mine", due: new DateOnly(2026, 2, 1)), A));

        var second = Plan(local, remote, links);

        Assert.Empty(second.Ops);
        Assert.Equal(2, second.Links.Count);
    }

    [Fact]
    public void A_change_that_failed_keeps_the_old_link_so_it_is_tried_again()
    {
        var before = F("Report");
        var link = new ConnectorLink(A, "r1", before);
        var result = Plan(local: [Local(A, F("Report v2"))], remote: [new RemoteItem("r1", before, A)], links: [link]);

        var kept = ConnectorPlan.Commit([link], result, []);

        Assert.Equal([link], kept);
        Assert.Equal([new UpdateRemote("r1", A, F("Report v2"))], Plan(local: [Local(A, F("Report v2"))], remote: [new RemoteItem("r1", before, A)], links: kept).Ops);
    }

    [Fact]
    public void Import_ids_are_the_same_on_every_computer_and_differ_per_connector()
    {
        Assert.Equal(ConnectorPlan.ImportId("notion", "r1"), ConnectorPlan.ImportId("notion", "r1"));
        Assert.NotEqual(ConnectorPlan.ImportId("notion", "r1"), ConnectorPlan.ImportId("mstodo", "r1"));
        Assert.NotEqual(ConnectorPlan.ImportId("notion", "r1"), ConnectorPlan.ImportId("notion", "r2"));
    }

    [Fact]
    public void A_value_the_other_side_can_only_approximate_stays_as_it_is_here()
    {
        // Microsoft To Do has no "critical": it shows as high there and stays critical here.
        static SyncedFields ToDo(SyncedFields f) => f with { Priority = f.Priority == Priority.Critical ? Priority.High : f.Priority };
        var critical = F("Report", priority: Priority.Critical);

        var same = ConnectorPlan.Make(new ConnectorPlanInput("mstodo", [Local(A, critical)], [new RemoteItem("r1", F("Report", priority: Priority.High), A)], [new ConnectorLink(A, "r1", critical)], ConnectorDirection.Both, All) { Normalize = ToDo });
        Assert.Empty(same.Ops);

        // A real change there still comes in.
        var lowered = ConnectorPlan.Make(new ConnectorPlanInput("mstodo", [Local(A, critical)], [new RemoteItem("r1", F("Report", priority: Priority.Low), A)], [new ConnectorLink(A, "r1", critical)], ConnectorDirection.Both, All) { Normalize = ToDo });
        Assert.Equal([new UpdateLocal(A, F("Report", priority: Priority.Low))], lowered.Ops);
    }

    [Fact]
    public void An_import_that_failed_before_its_mark_was_written_is_tried_again()
    {
        // The task couldn't be created, but the outside item got its mark anyway (marks are written after creates).
        var result = Plan(remote: [new RemoteItem("r1", F("Theirs"), ImportId("r1"))]);

        Assert.Equal([new CreateLocal(ImportId("r1"), "r1", F("Theirs"))], result.Ops);
    }

    [Fact]
    public void A_duplicated_outside_item_carrying_a_linked_task_id_comes_in_as_its_own_task()
    {
        // Duplicating a Notion page copies its "Todo Tracker ID".
        var before = F("Report");
        var result = Plan(
            local: [Local(A, before)],
            remote: [new RemoteItem("r1", before, A), new RemoteItem("r2", F("Report (copy)"), A)],
            links: [new ConnectorLink(A, "r1", before)]);

        Assert.Equal([new CreateLocal(ImportId("r2"), "r2", F("Report (copy)")), new MarkRemote("r2", ImportId("r2"))], result.Ops);
    }

    [Fact]
    public void Untitled_outside_items_are_not_brought_in_and_a_cleared_title_there_keeps_the_one_here()
    {
        Assert.Empty(Plan(remote: [new RemoteItem("r1", F("  "), null)]).Ops);

        var before = F("Report");
        var cleared = Plan(local: [Local(A, before)], remote: [new RemoteItem("r1", F(""), A)], links: [new ConnectorLink(A, "r1", before)]);
        Assert.Equal([new UpdateRemote("r1", A, before)], cleared.Ops);
    }

    [Fact]
    public void A_link_whose_two_sides_are_both_gone_is_forgotten()
    {
        Assert.Empty(Plan(links: [new ConnectorLink(A, "r1", F("Report"), LinkState.RemoteGone)]).Links);
    }

    [Fact]
    public void Tags_compare_as_a_set_and_notes_ignore_trailing_space()
    {
        Assert.Equal(F("x", tags: ["a", "b"]), F("x", tags: ["b", "a"]));
        Assert.Equal(F("x", notes: "hello\n"), F("x", notes: "hello"));
        Assert.Equal(F("x", notes: ""), F("x", notes: null));
    }
}
