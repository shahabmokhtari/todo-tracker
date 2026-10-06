using System.Text;
using TodoTracker.Core.Sync;
using Xunit;

namespace TodoTracker.Core.Tests.Sync;

public class SyncPlannerTests
{
    private const string TaskKey = "t/00000000000000000000000000000001";
    private const string OtherKey = "t/00000000000000000000000000000002";

    // Device "a" sorts before "b": a's version wins ties.
    private static readonly Dictionary<string, byte[]> Blobs = [];

    private static SyncEntry E(string key, string path, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SyncHash.Of(bytes);
        Blobs[hash] = bytes;
        return new SyncEntry(key, path, hash);
    }

    private static SyncSide Side(string device, params SyncEntry[] entries) =>
        new(device, device == "a" ? "Laptop" : "Desktop", entries.ToDictionary(e => e.Key), e => Blobs[e.Hash]);

    private static Dictionary<string, SyncEntry> Base(params SyncEntry[] entries) => entries.ToDictionary(e => e.Key);

    private static SyncPlan Plan(SyncSide local, SyncSide peer, Dictionary<string, SyncEntry>? b = null) =>
        SyncPlanner.Plan(local, peer, b ?? [], h => Blobs.GetValueOrDefault(h));

    private static string Text(SyncOp op) => Encoding.UTF8.GetString(op.Content!);

    [Fact]
    public void A_task_only_the_peer_has_is_created_here()
    {
        var plan = Plan(Side("a"), Side("b", E(TaskKey, "Work/Plan.md", "# Plan\n")));

        var op = Assert.Single(plan.Ops);
        Assert.Equal(SyncOpKind.Write, op.Kind);
        Assert.Equal("Work/Plan.md", op.Path);
        Assert.Null(op.Expected);
        Assert.Equal("# Plan\n", Text(op));
    }

    [Fact]
    public void A_peer_edit_to_a_task_unchanged_here_is_taken()
    {
        var old = E(TaskKey, "Work/Plan.md", "# Plan\n");
        var plan = Plan(Side("a", old), Side("b", E(TaskKey, "Work/Plan.md", "# Plan\n- [ ] Book\n")), Base(old));

        var op = Assert.Single(plan.Ops);
        Assert.Equal((SyncOpKind.Write, "Work/Plan.md", old.Hash), (op.Kind, op.Path, op.Expected));
        Assert.Equal("# Plan\n- [ ] Book\n", Text(op));
    }

    [Fact]
    public void A_local_edit_the_peer_has_not_seen_stays()
    {
        var old = E(TaskKey, "Work/Plan.md", "# Plan\n");

        var plan = Plan(Side("a", E(TaskKey, "Work/Plan.md", "# Plan!\n")), Side("b", old), Base(old));

        Assert.Empty(plan.Ops);
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void Edits_on_both_devices_merge_to_the_same_text_on_each()
    {
        var old = E(TaskKey, "Work/Plan.md", "# Plan\n- [ ] Book\n- [ ] Pack\n- [ ] Go\n");
        var mine = E(TaskKey, "Work/Plan.md", "# Plan\n- [x] Book\n- [ ] Pack\n- [ ] Go\n");
        var theirs = E(TaskKey, "Work/Plan.md", "# Plan\n- [ ] Book\n- [ ] Pack\n- [ ] Go\n- [ ] Visa\n");

        var onA = Assert.Single(Plan(Side("a", mine), Side("b", theirs), Base(old)).Ops);
        var onB = Assert.Single(Plan(Side("b", theirs), Side("a", mine), Base(old)).Ops);

        Assert.Equal("# Plan\n- [x] Book\n- [ ] Pack\n- [ ] Go\n- [ ] Visa\n", Text(onA));
        Assert.Equal(Text(onA), Text(onB));
    }

    [Fact]
    public void Clashing_edits_keep_both_lines_and_are_reported_the_same_way_on_each_device()
    {
        var old = E(TaskKey, "Work/Call.md", "# Call\nmom\n");
        var mine = E(TaskKey, "Work/Call.md", "# Call\nmom today\n");
        var theirs = E(TaskKey, "Work/Call.md", "# Call\nmom tomorrow\n");

        var onA = Plan(Side("a", mine), Side("b", theirs), Base(old));
        var onB = Plan(Side("b", theirs), Side("a", mine), Base(old));

        Assert.Equal("# Call\nmom today\nmom tomorrow\n", Text(Assert.Single(onA.Ops)));
        Assert.Equal(Text(onA.Ops[0]), Text(Assert.Single(onB.Ops)));
        var conflict = Assert.Single(onA.Conflicts);
        Assert.Equal((TaskKey, "Work/Call.md", mine.Hash, theirs.Hash, old.Hash), (conflict.Key, conflict.Path, conflict.Mine, conflict.Theirs, conflict.Base));
        Assert.Single(onB.Conflicts);
    }

    [Fact]
    public void A_task_deleted_on_the_peer_and_unchanged_here_is_deleted()
    {
        var old = E(TaskKey, "Work/Plan.md", "# Plan\n");

        var op = Assert.Single(Plan(Side("a", old), Side("b"), Base(old)).Ops);

        Assert.Equal((SyncOpKind.Delete, "Work/Plan.md", old.Hash), (op.Kind, op.Path, op.Expected));
    }

    [Fact]
    public void An_edit_wins_over_a_delete()
    {
        var old = E(TaskKey, "Work/Plan.md", "# Plan\n");
        var edited = E(TaskKey, "Work/Plan.md", "# Plan!\n");

        Assert.Empty(Plan(Side("a", edited), Side("b"), Base(old)).Ops);
        var restore = Assert.Single(Plan(Side("a"), Side("b", edited), Base(old)).Ops);
        Assert.Equal((SyncOpKind.Write, "Work/Plan.md", (string?)null), (restore.Kind, restore.Path, restore.Expected));
    }

    [Fact]
    public void A_task_deleted_here_and_unchanged_on_the_peer_stays_deleted()
    {
        var old = E(TaskKey, "Work/Plan.md", "# Plan\n");

        Assert.Empty(Plan(Side("a"), Side("b", old), Base(old)).Ops);
    }

    [Fact]
    public void A_task_moved_on_the_peer_moves_here_and_keeps_local_edits()
    {
        var old = E(TaskKey, "Work/Plan.md", "# Plan\n- [ ] a\n- [ ] b\n- [ ] c\n");
        var mine = E(TaskKey, "Work/Plan.md", "# Plan\n- [x] a\n- [ ] b\n- [ ] c\n");
        var moved = E(TaskKey, "Home/Plan.md", "# Plan\n- [ ] a\n- [ ] b\n- [ ] c\n");

        var plan = Plan(Side("a", mine), Side("b", moved), Base(old));

        var op = Assert.Single(plan.Ops);
        Assert.Equal((SyncOpKind.Move, "Work/Plan.md", mine.Hash, "Home/Plan.md"), (op.Kind, op.Path, op.Expected, op.To));
    }

    [Fact]
    public void A_moved_and_edited_task_moves_then_is_written_at_its_new_place()
    {
        var old = E(TaskKey, "Work/Plan.md", "# Plan\n- [ ] a\n- [ ] b\n- [ ] c\n");
        var mine = E(TaskKey, "Work/Plan.md", "# Plan\n- [x] a\n- [ ] b\n- [ ] c\n");
        var moved = E(TaskKey, "Home/Plan.md", "# Plan\n- [ ] a\n- [ ] b\n- [ ] c\n- [ ] d\n");

        var plan = Plan(Side("a", mine), Side("b", moved), Base(old));

        Assert.Equal([SyncOpKind.Move, SyncOpKind.Write], plan.Ops.Select(o => o.Kind));
        Assert.Equal(("Home/Plan.md", mine.Hash), (plan.Ops[1].Path, plan.Ops[1].Expected));
        Assert.Equal("# Plan\n- [x] a\n- [ ] b\n- [ ] c\n- [ ] d\n", Text(plan.Ops[1]));
    }

    [Fact]
    public void The_same_task_created_differently_on_two_devices_keeps_both_identically_everywhere()
    {
        var mine = E(TaskKey, "Work/Plan.md", "---\nid: 1\n---\n# Plan\nfrom laptop\n");
        var theirs = E(TaskKey, "Work/Plan.md", "---\nid: 1\n---\n# Plan\nfrom desktop\n");

        var onA = Plan(Side("a", mine), Side("b", theirs));
        var onB = Plan(Side("b", theirs), Side("a", mine));

        // a wins the original file; b's version becomes a copy with the same name and text on both devices.
        var copyOnA = Assert.Single(onA.Ops);
        Assert.Equal((SyncOpKind.Write, "Work/Plan (from Desktop).md", (string?)null), (copyOnA.Kind, copyOnA.Path, copyOnA.Expected));
        Assert.Equal("---\nid: 1\n---\n# Plan (from Desktop)\nfrom desktop\n", Text(copyOnA));
        Assert.Equal(2, onB.Ops.Count);
        var copyOnB = Assert.Single(onB.Ops, o => o.Path == copyOnA.Path);
        Assert.Equal(Text(copyOnA), Text(copyOnB));
        var original = Assert.Single(onB.Ops, o => o.Path == "Work/Plan.md");
        Assert.Equal(("---\nid: 1\n---\n# Plan\nfrom laptop\n", theirs.Hash), (Text(original), original.Expected));
        Assert.Same(copyOnB, onB.Ops[0]);
    }

    [Fact]
    public void A_conflict_copy_that_already_exists_is_not_written_again()
    {
        var mine = E(TaskKey, "Work/Plan.md", "# Plan\nfrom laptop\n");
        var theirs = E(TaskKey, "Work/Plan.md", "# Plan\nfrom desktop\n");
        var copy = E(OtherKey, "Work/Plan (from Desktop).md", "# Plan (from Desktop)\nfrom desktop\n");

        Assert.Empty(Plan(Side("a", mine, copy), Side("b", theirs)).Ops);
    }

    [Fact]
    public void A_new_task_whose_path_is_taken_here_gets_a_free_name()
    {
        var plan = Plan(Side("a", E(OtherKey, "Work/Plan.md", "# Plan\nmine\n")), Side("b", E(TaskKey, "Work/Plan.md", "# Plan\ntheirs\n")));

        Assert.Equal("Work/Plan 2.md", Assert.Single(plan.Ops).Path);
    }

    [Fact]
    public void Clashing_attachments_keep_both_files()
    {
        var mine = E("f/_attachments/1/a.txt", "_attachments/1/a.txt", "mine");
        var theirs = E("f/_attachments/1/a.txt", "_attachments/1/a.txt", "theirs");

        var onB = Plan(Side("b", theirs), Side("a", mine));

        Assert.Equal(["_attachments/1/a (from Desktop).txt", "_attachments/1/a.txt"], onB.Ops.Select(o => o.Path).Order());
    }

    [Fact]
    public void Activity_from_the_peer_is_appended_once()
    {
        var mine = E(SyncKeys.Activity, SyncKeys.ActivityPath, "{\"x\":1}\n{\"x\":2}\n");
        var theirs = E(SyncKeys.Activity, SyncKeys.ActivityPath, "{\"x\":1}\n{\"x\":3}\n{\"x\":2}\n");

        var op = Assert.Single(Plan(Side("a", mine), Side("b", theirs)).Ops);

        Assert.Equal(SyncOpKind.AppendActivity, op.Kind);
        Assert.Equal("{\"x\":3}\n", Text(op));
        Assert.Empty(Plan(Side("a", theirs), Side("b", mine)).Ops);
    }

    [Fact]
    public void The_now_order_merges_as_a_list()
    {
        var old = E(SyncKeys.Order, SyncKeys.OrderPath, "[\"1\",\"2\"]");
        var theirs = E(SyncKeys.Order, SyncKeys.OrderPath, "[\"2\",\"1\"]");
        var mine = E(SyncKeys.Order, SyncKeys.OrderPath, "[\"1\",\"2\",\"3\"]");

        var taken = Assert.Single(Plan(Side("a", old), Side("b", theirs), Base(old)).Ops);
        Assert.Equal((SyncOpKind.SetOrder, "[\"2\",\"1\"]"), (taken.Kind, Text(taken)));
        var onB = Assert.Single(Plan(Side("b", theirs), Side("a", mine), Base(old)).Ops);
        Assert.Equal("[\"1\",\"2\",\"3\"]", Text(onB));
        Assert.Empty(Plan(Side("a", mine), Side("b", theirs), Base(old)).Ops);
    }

    [Fact]
    public void Settings_merge_by_tab_and_label()
    {
        var old = E(SyncKeys.Config, SyncKeys.ConfigPath, """
            {"schemaVersion":2,"groups":[{"id":"g1","folder":"Work","color":"#111"},{"id":"g2","folder":"Home"}],"labels":[{"name":"urgent","color":"red"}],"order":["1","2"]}
            """);
        var mine = E(SyncKeys.Config, SyncKeys.ConfigPath, """
            {"schemaVersion":2,"groups":[{"id":"g1","folder":"Work","color":"#222"},{"id":"g2","folder":"Home"}],"labels":[{"name":"urgent","color":"red"},{"name":"later","color":"gray"}],"order":["1","2","3"]}
            """);
        var theirs = E(SyncKeys.Config, SyncKeys.ConfigPath, """
            {"schemaVersion":2,"groups":[{"id":"g1","folder":"Job","color":"#111"},{"id":"g2","folder":"Home"},{"id":"g3","folder":"Kids"}],"labels":[],"order":["2","1"]}
            """);

        var onA = Assert.Single(Plan(Side("a", mine), Side("b", theirs), Base(old)).Ops);
        var onB = Assert.Single(Plan(Side("b", theirs), Side("a", mine), Base(old)).Ops);

        Assert.Equal(SyncOpKind.SetConfig, onA.Kind);
        Assert.Equal(Text(onA), Text(onB));
        var doc = System.Text.Json.Nodes.JsonNode.Parse(Text(onA))!;
        Assert.Equal(["Job:#222", "Home:", "Kids:"], doc["groups"]!.AsArray().Select(g => $"{g!["folder"]}:{g["color"]}"));
        Assert.Equal(["later"], doc["labels"]!.AsArray().Select(l => (string)l!["name"]!));
        Assert.Equal(["1", "2", "3"], doc["order"]!.AsArray().Select(i => (string)i!));
    }

    [Fact]
    public void Tasks_that_traded_places_on_the_peer_trade_places_here_too()
    {
        // Review finding: each move's target was taken by the other, so neither moved and the devices never agreed.
        var a = E(TaskKey, "Work/A.md", "# A\n");
        var b = E(OtherKey, "Work/B.md", "# B\n");

        var plan = Plan(Side("a", a, b), Side("b", E(TaskKey, "Work/B.md", "# A\n"), E(OtherKey, "Work/A.md", "# B\n")), Base(a, b));

        Assert.All(plan.Ops, o => Assert.Equal(SyncOpKind.Move, o.Kind));
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Work/A.md"] = "A", ["Work/B.md"] = "B" };
        foreach (var op in plan.Ops)
        {
            Assert.False(files.ContainsKey(op.To!), $"{op.To} would be overwritten");
            files[op.To!] = files[op.Path];
            files.Remove(op.Path);
        }

        Assert.Equal(("B", "A"), (files["Work/A.md"], files["Work/B.md"]));
    }

    [Fact]
    public void A_move_onto_a_file_that_stays_waits()
    {
        var old = E(TaskKey, "Work/A.md", "# A\n");
        var plan = Plan(Side("a", old, E(OtherKey, "Work/B.md", "# B\n")), Side("b", E(TaskKey, "Work/B.md", "# A\n")), Base(old));

        Assert.DoesNotContain(plan.Ops, o => o.Kind == SyncOpKind.Move);
    }

    [Fact]
    public void Rich_pages_made_on_both_devices_are_both_kept()
    {
        // Review finding: without a shared version, the other device's rich page was dropped.
        const string RichKey = "h/00000000000000000000000000000001";
        var mine = E(RichKey, "Work/Plan.html", "<p>laptop</p>");
        var theirs = E(RichKey, "Work/Plan.html", "<p>desktop</p>");

        var onA = Plan(Side("a", mine), Side("b", theirs));
        var onB = Plan(Side("b", theirs), Side("a", mine));

        Assert.Equal("<p>desktop</p>", Text(Assert.Single(onA.Ops, o => o.Path == "Work/Plan (from Desktop).html")));
        Assert.Equal("<p>desktop</p>", Text(Assert.Single(onB.Ops, o => o.Path == "Work/Plan (from Desktop).html")));
        Assert.Equal("<p>laptop</p>", Text(Assert.Single(onB.Ops, o => o.Path == "Work/Plan.html")));
    }

    [Fact]
    public void A_merge_that_is_not_a_valid_task_keeps_both_versions_whole()
    {
        var old = E(TaskKey, "Work/Call.md", "---\ndue: 1\n---\n# Call\n");
        var mine = E(TaskKey, "Work/Call.md", "---\ndue: 2\n---\n# Call\n");
        var theirs = E(TaskKey, "Work/Call.md", "---\ndue: 3\n---\n# Call\n");
        var onB = SyncPlanner.Plan(Side("b", theirs), Side("a", mine), Base(old), h => Blobs.GetValueOrDefault(h), new SyncPlanOptions { IsValidTask = (_, _) => false });

        Assert.Equal("---\ndue: 2\n---\n# Call\n", Text(Assert.Single(onB.Ops, o => o.Path == "Work/Call.md")));
        Assert.Equal("---\ndue: 3\n---\n# Call (from Desktop)\n", Text(Assert.Single(onB.Ops, o => o.Path == "Work/Call (from Desktop).md")));
        Assert.Single(onB.Conflicts);
    }

    [Fact]
    public void A_version_another_device_deleted_is_not_brought_back()
    {
        // Review finding: a stale device's snapshot brought deleted tasks back to a new device.
        var stale = E(TaskKey, "Work/Old.md", "# Old\n");
        var options = new SyncPlanOptions { Deleted = e => e.Hash == stale.Hash };

        Assert.Empty(SyncPlanner.Plan(Side("a"), Side("b", stale), Base(), h => Blobs.GetValueOrDefault(h), options).Ops);
        var delete = Assert.Single(SyncPlanner.Plan(Side("a", stale), Side("b"), Base(), h => Blobs.GetValueOrDefault(h), options).Ops);
        Assert.Equal(SyncOpKind.Delete, delete.Kind);
    }

    [Fact]
    public void The_next_base_records_only_what_both_devices_agree_on()
    {
        var agreed = E(TaskKey, "Work/Plan.md", "# Plan\n");
        var stale = E(OtherKey, "Work/Old.md", "# Old\n");
        var mine = E(OtherKey, "Work/Old.md", "# Old!\n");
        var theirs = E(OtherKey, "Work/Old.md", "# Old?\n");

        var next = SyncPlanner.NextBase(Base(agreed, mine), Base(agreed, theirs), Base(stale));

        Assert.Equal(agreed, next[TaskKey]);
        Assert.Equal(stale, next[OtherKey]);
        Assert.Empty(SyncPlanner.NextBase(Base(), Base(), Base(stale)));
    }
}
