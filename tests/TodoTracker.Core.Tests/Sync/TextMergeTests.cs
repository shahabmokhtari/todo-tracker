using TodoTracker.Core.Sync;
using Xunit;

namespace TodoTracker.Core.Tests.Sync;

public class TextMergeTests
{
    private static string L(params string[] lines) => string.Concat(lines.Select(l => l + "\n"));

    [Fact]
    public void Changes_to_different_lines_are_combined()
    {
        var b = L("# Plan trip", "- [ ] Book flights", "- [ ] Pack", "- [ ] Hotel");
        var mine = L("# Plan trip", "- [x] Book flights", "- [ ] Pack", "- [ ] Hotel");
        var theirs = L("# Plan trip", "- [ ] Book flights", "- [ ] Pack", "- [ ] Hotel", "- [ ] Visa");

        var result = TextMerge.Merge(b, mine, theirs);

        Assert.False(result.Conflicted);
        Assert.Equal(L("# Plan trip", "- [x] Book flights", "- [ ] Pack", "- [ ] Hotel", "- [ ] Visa"), result.Text);
    }

    [Fact]
    public void The_same_change_on_both_sides_is_taken_once()
    {
        var b = L("a", "b", "c");
        var both = L("a", "B", "c");

        var result = TextMerge.Merge(b, both, both);

        Assert.False(result.Conflicted);
        Assert.Equal(both, result.Text);
    }

    [Fact]
    public void Insertions_at_the_start_and_end_merge()
    {
        var b = L("x", "y");
        var result = TextMerge.Merge(b, L("first", "x", "y"), L("x", "y", "last"));

        Assert.False(result.Conflicted);
        Assert.Equal(L("first", "x", "y", "last"), result.Text);
    }

    [Fact]
    public void A_deletion_and_an_unrelated_edit_merge()
    {
        var b = L("a", "b", "c", "d");
        var result = TextMerge.Merge(b, L("a", "c", "d"), L("a", "b", "c", "D"));

        Assert.False(result.Conflicted);
        Assert.Equal(L("a", "c", "D"), result.Text);
    }

    [Fact]
    public void Conflicting_edits_keep_both_versions_lines_with_the_first_side_first()
    {
        var b = L("# Title", "Call mom", "end");
        var result = TextMerge.Merge(b, L("# Title", "Call mom today", "end"), L("# Title", "Call mom tomorrow", "end"));

        Assert.True(result.Conflicted);
        Assert.Equal(L("# Title", "Call mom today", "Call mom tomorrow", "end"), result.Text);
    }

    [Fact]
    public void Clean_merges_do_not_depend_on_the_side_order()
    {
        var b = L("a", "b", "c");

        var forward = TextMerge.Merge(b, L("a", "b", "c", "1"), L("0", "a", "b", "c"));
        var backward = TextMerge.Merge(b, L("0", "a", "b", "c"), L("a", "b", "c", "1"));

        Assert.False(forward.Conflicted);
        Assert.Equal(forward.Text, backward.Text);
    }

    [Fact]
    public void Text_without_a_final_newline_and_crlf_lines_survive()
    {
        var result = TextMerge.Merge("a\r\nb\r\nc\r\nd", "a\r\nB\r\nc\r\nd", "a\r\nb\r\nc\r\nD");

        Assert.False(result.Conflicted);
        Assert.Equal("a\r\nB\r\nc\r\nD", result.Text);
    }

    [Fact]
    public void Lines_with_their_own_keys_merge_one_by_one()
    {
        // Checking off one step while renaming the next (adjacent lines) is not a conflict.
        string?[] Keys(IReadOnlyList<string> lines) => [.. lines.Select(line => line.Contains('^', StringComparison.Ordinal) ? line[line.IndexOf('^', StringComparison.Ordinal)..].Trim() : null)];
        var b = L("# Trip", "- [ ] Flights ^a1", "- [ ] Hotel ^b2", "end");

        var result = TextMerge.Merge(b, L("# Trip", "- [x] Flights ^a1", "- [ ] Hotel ^b2", "end"), L("# Trip", "- [ ] Flights ^a1", "- [ ] Beach hotel ^b2", "end"), Keys);

        Assert.False(result.Conflicted);
        Assert.Equal(L("# Trip", "- [x] Flights ^a1", "- [ ] Beach hotel ^b2", "end"), result.Text);
        Assert.True(TextMerge.Merge(b, L("# Trip", "- [x] Flights ^a1", "- [ ] Hotel ^b2", "end"), L("# Trip", "- [ ] Flights ^a1", "- [ ] Beach hotel ^b2", "end")).Conflicted);
    }

    [Fact]
    public void Time_logged_on_two_devices_is_kept_from_both_without_a_conflict()
    {
        // Each device appended its own time line at the same place: different ids, so both stay.
        const string a = "- 2026-01-05 09:00–09:25 · 25 min %%{\"id\":\"7b0c2f9e-0000-4000-8000-0000000000d1\",\"start\":\"x\"}%%";
        const string b = "- 2026-01-05 10:00–10:30 · 30 min %%{\"id\":\"7b0c2f9e-0000-4000-8000-0000000000d2\",\"start\":\"y\"}%%";
        var start = L("---", "status: open", "---", "# Report", "", "## Time", "");

        var result = TextMerge.Merge(start, start + L(a), start + L(b), SyncPlanner.TaskLineKeys);

        Assert.False(result.Conflicted);
        Assert.Equal(start + L(a, b), result.Text);
    }

    [Fact]
    public void Task_lines_are_keyed_by_frontmatter_key_inside_the_frontmatter_and_block_id_below()
    {
        var keys = SyncPlanner.TaskLineKeys([
            "---\n",
            "priority: high\n",
            "---\n",
            "# Book\n",
            "Note: call Bob\n",
            "\t- [ ] Book #trip 📅 2026-10-07 ^x7k2m9 %%{\"seq\":1}%%\n",
            "- [x] Pack ^a1b2c3\r\n",
        ]);

        // Review finding: "Note: call Bob" in the body was taken for a frontmatter key.
        Assert.Equal([null, "priority:", null, null, null, "^x7k2m9", "^a1b2c3"], keys);
    }

    [Fact]
    public void A_clash_can_be_settled_for_one_side_while_every_other_change_stays()
    {
        var b = L("a", "b", "c", "d");
        var mine = L("A", "b", "mine", "d");
        var theirs = L("a", "b", "theirs", "d", "e");

        Assert.Equal(L("A", "b", "mine", "d", "e"), TextMerge.Merge(b, mine, theirs, policy: ConflictPolicy.First).Text);
        Assert.Equal(L("A", "b", "theirs", "d", "e"), TextMerge.Merge(b, mine, theirs, policy: ConflictPolicy.Second).Text);
    }

    [Fact]
    public void Empty_base_with_different_additions_is_a_conflict()
    {
        var result = TextMerge.Merge(string.Empty, L("mine"), L("theirs"));

        Assert.True(result.Conflicted);
        Assert.Equal(L("mine", "theirs"), result.Text);
    }

    [Fact]
    public void Merging_the_merge_again_changes_nothing()
    {
        var b = L("a", "b", "c");
        var first = TextMerge.Merge(b, L("a", "x", "c"), L("a", "b", "c", "d")).Text;

        Assert.Equal(first, TextMerge.Merge(b, first, L("a", "b", "c", "d")).Text);
        Assert.Equal(first, TextMerge.Merge(b, L("a", "b", "c", "d"), first).Text);
    }

    /// <summary>Merged both ways round, the result is <paramref name="expected"/>, without a conflict.</summary>
    private static void MergesTo(string expected, string b, string first, string second)
    {
        foreach (var (x, y) in new[] { (first, second), (second, first) })
        {
            var result = TextMerge.Merge(b, x, y);
            Assert.False(result.Conflicted);
            Assert.Equal(expected, result.Text);
        }
    }

    [Fact]
    public void A_line_added_on_both_sides_is_kept_once_where_the_diffs_place_it_differently()
    {
        // Live: a note added on the phone, merged by a computer that then changed the title, ended up twice. The
        // added line equals the one before it, so one side's diff saw it after that line and the other's before.
        MergesTo(L("Title changed", "b", "b"), L("", "b"), L("", "b", "b"), L("Title changed", "b", "b"));
    }

    [Fact]
    public void A_note_added_on_one_device_and_merged_with_a_title_change_is_kept_once()
    {
        var b = L("# Trip", string.Empty, "## Notes", string.Empty, "> [!note] 2026-01-05 10:00 · You", "> ok");
        var note = new[] { string.Empty, "> [!note] 2026-01-05 11:00 · You", "> ok" };
        var withNote = b + L(note);
        var renamed = withNote.Replace("# Trip", "# Lisbon trip", StringComparison.Ordinal);

        MergesTo(renamed, b, withNote, renamed);
    }

    [Fact]
    public void A_step_checked_on_one_side_and_merged_then_the_next_renamed_on_the_other_merges_step_by_step()
    {
        // The second side already has the first's change (it merged it) and renamed the next step: the shared change
        // must not hide that both lines are the same steps.
        var b = L("# Trip", "- [ ] Flights ^s1", "- [ ] Hotel ^s2");
        var first = L("# Trip", "- [x] Flights ^s1", "- [ ] Hotel ^s2");
        var second = L("# Trip", "- [x] Flights ^s1", "- [ ] A hotel ^s2");

        foreach (var (x, y) in new[] { (first, second), (second, first) })
        {
            var result = TextMerge.Merge(b, x, y, SyncPlanner.TaskLineKeys);
            Assert.False(result.Conflicted);
            Assert.Equal(second, result.Text);
        }
    }

    [Fact]
    public void The_same_line_added_at_a_place_with_repeats_is_kept_once()
    {
        MergesTo(L("x", "x", "x", "Y"), L("x", "x", "y"), L("x", "x", "x", "y"), L("x", "x", "x", "Y"));
    }

    [Fact]
    public void The_same_line_added_on_both_next_to_different_edits_is_kept_once()
    {
        MergesTo(L("A", "m", "m", "B"), L("a", "m", "b"), L("A", "m", "m", "b"), L("a", "m", "m", "B"));
    }

    [Fact]
    public void The_same_line_added_on_both_between_different_deletes_is_kept_once()
    {
        MergesTo(L("b", "b"), L("a", "b", "c"), L("b", "b", "c"), L("a", "b", "b"));
    }

    [Fact]
    public void One_of_two_equal_lines_deleted_on_both_is_deleted_once()
    {
        // The other way round: both removed one "b"; the diffs may say different ones, and the other "b" must stay.
        var b = L("t", "b", "b", "e");
        Assert.Equal(L("T", "b", "e"), TextMerge.Merge(b, L("t", "b", "e"), L("T", "b", "e")).Text);

        // Merged the other way round, the first side's own diff pairs the "t" change with the "b" it removed: that
        // overlaps the second's removal, so it's a conflict, with both versions' lines kept and the other "b" too.
        var other = TextMerge.Merge(b, L("T", "b", "e"), L("t", "b", "e"));
        Assert.True(other.Conflicted);
        Assert.Equal(L("T", "t", "b", "e"), other.Text);
    }
}
