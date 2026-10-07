using System.Globalization;
using System.Text;
using TodoTracker.Core;
using TodoTracker.Core.Vault;

namespace TodoTracker.Core.Tests.Vault;

public class TaskMarkdownTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly TaskMarkdownContext Utc = new(TimeZoneInfo.Utc, Now, "Work");

    /// <summary>The canonical format: everything the app writes looks exactly like this (LF, whatever this file uses).</summary>
    internal static readonly string Golden = GoldenText.ReplaceLineEndings("\n");

    private const string GoldenText = """
        ---
        id: 7b0c2f9e-0000-4000-8000-000000000001
        status: open
        priority: high
        created: 2026-01-05T09:00:00.000Z
        due: 2026-01-07T17:00
        scheduled: 2026-01-06T09:00
        sequential: true
        step-delay: 1d
        tags:
          - release
        labels:
          - Deep work
        reminders:
          - id: 7b0c2f9e-0000-4000-8000-0000000000a1
            at: 2026-01-06T09:00:00.000Z
            message: "Time to act on: Ship release 2.3"
            kind: next-action
        ---
        # Ship release 2.3

        Why: customers asked for it.

        ## Steps

        1. [x] Roll out A ✅ 2026-01-05 ^t0000002 %%{"id":"7b0c2f9e-0000-4000-8000-000000000002","created":"2026-01-05T09:00:00.000Z","done":"2026-01-05T10:00:00.000Z"}%%
        	Watch the error rate.
        	- [ ] Check dashboards #risky ⏫ ^t0000003 %%{"id":"7b0c2f9e-0000-4000-8000-000000000003","created":"2026-01-05T09:00:00.000Z"}%%
        2. [ ] Roll out B 📅 2026-01-08 ⏳ 2026-01-06 ^t0000004 %%{"id":"7b0c2f9e-0000-4000-8000-000000000004","created":"2026-01-05T09:00:00.000Z","due":"2026-01-08T17:00:00.000Z","next":"2026-01-06T10:00:00.000Z","labels":["Quick win"]}%%

        ## Notes

        > [!note] 2026-01-05 10:00 · Agent: copilot · [[#^t0000002|Roll out A]] %%{"id":"7b0c2f9e-0000-4000-8000-0000000000b1","at":"2026-01-05T10:00:00.000Z"}%%
        > deployed ring 0
        > second line
        > 🔗 [Release dashboard](https://example.com/rel)

        > [!note] 2026-01-05 11:30 · You %%{"id":"7b0c2f9e-0000-4000-8000-0000000000b2","at":"2026-01-05T11:30:00.000Z"}%%
        > looks good

        ## Attachments

        - [plan v2.pdf](../_attachments/7b0c2f9e/plan%20v2.pdf) %%{"id":"7b0c2f9e-0000-4000-8000-0000000000c1","size":42,"at":"2026-01-05T09:30:00.000Z","by":"You"}%%

        """;

    private static Guid G(string suffix) => Guid.Parse("7b0c2f9e-0000-4000-8000-" + suffix.PadLeft(12, '0'));

    [Fact]
    public void Golden_file_parses_into_the_full_task_tree()
    {
        var parsed = TaskMarkdown.Parse(Golden, "Ship release 2.3", Utc);
        var root = parsed.Root;

        Assert.False(parsed.NeedsWrite);
        Assert.Equal(G("1"), root.Id);
        Assert.Equal("Ship release 2.3", root.Title);
        Assert.Equal("Why: customers asked for it.", root.Details);
        Assert.Equal(Priority.High, root.Priority);
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero), root.CreatedAt);
        Assert.Equal(new DateTimeOffset(2026, 1, 7, 17, 0, 0, TimeSpan.Zero), root.Deadline);
        Assert.Equal(new DateTimeOffset(2026, 1, 6, 9, 0, 0, TimeSpan.Zero), root.NextActionAt);
        Assert.True(root.Sequential);
        Assert.Equal(TimeSpan.FromDays(1), root.StepDelay);
        Assert.Equal(["release"], root.Tags);
        Assert.Equal(["Deep work"], root.Labels);
        var reminder = Assert.Single(root.Reminders);
        Assert.Equal((G("a1"), ReminderKind.NextAction, "Time to act on: Ship release 2.3"), (reminder.Id, reminder.Kind, reminder.Message));

        Assert.Equal(["Roll out A", "Roll out B"], root.Children.Select(c => c.Title));
        var a = root.Children[0];
        Assert.Equal(G("2"), a.Id);
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero), a.CompletedAt);
        Assert.Equal("Watch the error rate.", a.Details);
        var check = Assert.Single(a.Children);
        Assert.Equal(("Check dashboards", Priority.High, false), (check.Title, check.Priority, check.IsDone));
        Assert.Equal(["risky"], check.Tags);
        var b = root.Children[1];
        Assert.Equal(new DateTimeOffset(2026, 1, 8, 17, 0, 0, TimeSpan.Zero), b.Deadline);
        Assert.Equal(new DateTimeOffset(2026, 1, 6, 10, 0, 0, TimeSpan.Zero), b.NextActionAt);
        Assert.Equal(["Quick win"], b.Labels);

        var deployed = Assert.Single(a.Notes);
        Assert.Equal((G("b1"), "deployed ring 0\nsecond line", ActorKind.Agent, "copilot", "https://example.com/rel", "Release dashboard"),
            (deployed.Id, deployed.Text, deployed.Author.Kind, deployed.Author.Name, deployed.SourceUrl, deployed.SourceTitle));
        var mine = Assert.Single(root.Notes);
        Assert.Equal(("looks good", ActorKind.User), (mine.Text, mine.Author.Kind));

        var file = Assert.Single(root.Attachments);
        Assert.Equal((G("c1"), "plan v2.pdf", "_attachments/7b0c2f9e/plan v2.pdf", 42L), (file.Id, file.FileName, file.Path, file.Size));
    }

    [Fact]
    public void Golden_file_renders_back_byte_for_byte()
    {
        var parsed = TaskMarkdown.Parse(Golden, "Ship release 2.3", Utc);

        Assert.Equal(Golden, TaskMarkdown.Render(parsed.Root, Utc, parsed));
    }

    [Fact]
    public void Tasks_created_in_the_app_round_trip_through_markdown()
    {
        var board = new TaskBoard();
        var t0 = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
        var root = board.AddTask(new NewTask("Plan trip") { Tags = ["travel"], Labels = ["Later"], Details = "Line 1\n\nLine 3", Deadline = t0.AddDays(3) }, Actor.User, t0);
        var step = board.AddTask(new NewTask("Book flights") { ParentId = root.Id, Priority = Priority.Critical }, Actor.Agent("claude"), t0);
        board.AddNote(step.Id, "Found a cheap one", Actor.Agent("claude"), t0.AddHours(1), "https://flights.example/x", "Flights");
        board.AddReminder(step.Id, t0.AddDays(1), "Check prices", Actor.User, t0);
        board.ScheduleNextAction(root.Id, t0.AddHours(5), Actor.User, t0, notify: true);
        board.AddAttachment(step.Id, "itinerary.pdf", "_attachments/x/itinerary.pdf", 7, Actor.User, t0);

        var text = TaskMarkdown.Render(root, Utc);
        var parsed = TaskMarkdown.Parse(text, "Plan trip", Utc);

        Assert.False(parsed.NeedsWrite);
        Assert.Equal(Describe(root), Describe(parsed.Root));
        Assert.Equal(text, TaskMarkdown.Render(parsed.Root, Utc, parsed));
    }

    [Fact]
    public void A_note_typed_in_obsidian_becomes_a_task_with_ids_assigned()
    {
        const string typed = "# Buy groceries\n\nRemember the list.\n\n- [ ] Milk #dairy\n- [ ] Bread 🔺 📅 2026-01-09\n\t- [x] Check the bakery hours\n- Eggs\n";

        var parsed = TaskMarkdown.Parse(typed, "Buy groceries", Utc);
        var root = parsed.Root;

        Assert.True(parsed.NeedsWrite);
        Assert.NotEqual(Guid.Empty, root.Id);
        Assert.Equal(Now, root.CreatedAt);
        Assert.Equal("Remember the list.", root.Details);
        Assert.False(root.Sequential);
        Assert.Equal(["Milk", "Bread", "Eggs"], root.Children.Select(c => c.Title));
        Assert.Equal(["dairy"], root.Children[0].Tags);
        Assert.Equal(Priority.Critical, root.Children[1].Priority);
        Assert.Equal(new DateTimeOffset(2026, 1, 9, 17, 0, 0, TimeSpan.Zero), root.Children[1].Deadline);
        Assert.Equal(Now, root.Children[1].Children[0].CompletedAt);

        // Writing it back adds ids and the canonical sections, and the next read is stable.
        var written = TaskMarkdown.Render(root, Utc, parsed);
        var reread = TaskMarkdown.Parse(written, "Buy groceries", Utc);
        Assert.False(reread.NeedsWrite);
        Assert.Equal(root.Children.Select(c => c.Id), reread.Root.Children.Select(c => c.Id));
        Assert.Contains("## Subtasks", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Multi_word_tags_survive_the_file_on_a_task_and_on_a_subtask()
    {
        var board = new TaskBoard();
        var root = board.AddTask(new NewTask("Plan launch") { Tags = ["deep work", "q3"] }, Actor.User, Now);
        var step = board.AddTask(new NewTask("Draft") { ParentId = root.Id, Tags = ["deep work", "writing"] }, Actor.User, Now);

        var written = TaskMarkdown.Render(root, Utc);
        var reread = TaskMarkdown.Parse(written, "Plan launch", Utc).Root;

        Assert.Equal(["deep work", "q3"], reread.Tags);
        Assert.Equal(["deep work", "writing"], reread.Children.Single(c => c.Id == step.Id).Tags.Order());
        // Single-word tags stay Obsidian tags on the line; a multi-word one can't be one, so it isn't written there.
        Assert.Contains("Draft #writing", written, StringComparison.Ordinal);
        Assert.DoesNotContain("#deep work", written, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordered_list_makes_the_steps_sequential()
    {
        var parsed = TaskMarkdown.Parse("# Rollout\n\n1. [ ] Ring 0\n2. [ ] Ring 1\n   1. [ ] Prep\n   2. [ ] Go\n", "Rollout", Utc);

        Assert.True(parsed.Root.Sequential);
        Assert.True(parsed.Root.Children[1].Sequential);
        Assert.False(parsed.Root.Children[0].Sequential);
        Assert.Equal(["Prep", "Go"], parsed.Root.Children[1].Children.Select(c => c.Title));
    }

    [Fact]
    public void Checking_a_box_in_obsidian_completes_and_unchecking_reopens()
    {
        var edited = Golden.Replace("2. [ ] Roll out B", "2. [x] Roll out B", StringComparison.Ordinal)
            .Replace("1. [x] Roll out A ✅ 2026-01-05", "1. [ ] Roll out A", StringComparison.Ordinal);

        var parsed = TaskMarkdown.Parse(edited, "Ship release 2.3", Utc);

        Assert.True(parsed.NeedsWrite);
        Assert.Equal(Now, parsed.Root.Children[1].CompletedAt);
        Assert.Null(parsed.Root.Children[0].CompletedAt);
    }

    [Fact]
    public void Obsidian_tasks_done_dates_and_cancelled_boxes_count_as_done()
    {
        var parsed = TaskMarkdown.Parse("# T\n\n- [x] Shipped ✅ 2026-01-02\n- [-] Dropped\n", "T", Utc);

        Assert.Equal("Shipped", parsed.Root.Children[0].Title);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero), parsed.Root.Children[0].CompletedAt);
        Assert.True(parsed.Root.Children[1].IsDone);
    }

    [Fact]
    public void Editing_a_visible_date_wins_over_the_hidden_exact_time()
    {
        var edited = Golden.Replace("📅 2026-01-08", "📅 2026-01-12", StringComparison.Ordinal);

        var b = TaskMarkdown.Parse(edited, "Ship release 2.3", Utc).Root.Children[1];

        Assert.Equal(new DateTimeOffset(2026, 1, 12, 17, 0, 0, TimeSpan.Zero), b.Deadline);
    }

    [Fact]
    public void Removing_a_visible_date_clears_it()
    {
        var edited = Golden.Replace(" 📅 2026-01-08", string.Empty, StringComparison.Ordinal);

        Assert.Null(TaskMarkdown.Parse(edited, "Ship release 2.3", Utc).Root.Children[1].Deadline);
    }

    [Fact]
    public void Dates_use_the_local_time_zone()
    {
        var pacific = TimeZoneInfo.CreateCustomTimeZone("PST", TimeSpan.FromHours(-8), "PST", "PST");
        var ctx = new TaskMarkdownContext(pacific, Now, "Work");

        var root = TaskMarkdown.Parse("---\ndue: 2026-01-07T17:00\n---\n# T\n\n- [ ] S 📅 2026-01-09\n", "T", ctx).Root;

        Assert.Equal(new DateTimeOffset(2026, 1, 7, 17, 0, 0, TimeSpan.FromHours(-8)), root.Deadline);
        Assert.Equal(new DateTimeOffset(2026, 1, 9, 17, 0, 0, TimeSpan.FromHours(-8)), root.Children[0].Deadline);
        var rendered = TaskMarkdown.Render(root, ctx);
        Assert.Contains("📅 2026-01-09", rendered, StringComparison.Ordinal);
        Assert.Contains("due: 2026-01-07T17:00\n", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_frontmatter_details_and_sections_are_preserved()
    {
        var withExtras = Golden
            .Replace("status: open\n", "status: open\naliases:\n  - SR23\ncssclasses: wide\n", StringComparison.Ordinal)
            .Replace("Why: customers asked for it.\n", "Why: customers asked for it.\n\n### Context\n\n| a | b |\n|---|---|\n| 1 | 2 |\n", StringComparison.Ordinal)
            .Replace("## Notes\n", "## Notes\n\nOlder notes are in the wiki.\n", StringComparison.Ordinal);

        var parsed = TaskMarkdown.Parse(withExtras, "Ship release 2.3", Utc);
        var rendered = TaskMarkdown.Render(parsed.Root, Utc, parsed);

        Assert.Contains("aliases:\n  - SR23\ncssclasses: wide\n", rendered, StringComparison.Ordinal);
        Assert.Contains("### Context\n\n| a | b |", rendered, StringComparison.Ordinal);
        Assert.Contains("Older notes are in the wiki.", rendered, StringComparison.Ordinal);
        var again = TaskMarkdown.Parse(rendered, "x", Utc);
        Assert.Equal(rendered, TaskMarkdown.Render(again.Root, Utc, again));
    }

    [Fact]
    public void Tags_inside_a_sentence_stay_in_the_title()
    {
        var root = TaskMarkdown.Parse("# T\n\n- [ ] Talk to #design about #123 #ux\n", "T", Utc).Root;

        Assert.Equal("Talk to #design about #123", root.Children[0].Title);
        Assert.Equal(["design", "ux"], root.Children[0].Tags);
        Assert.Contains("- [ ] Talk to #design about #123 #ux ^", TaskMarkdown.Render(root, Utc), StringComparison.Ordinal);
    }

    [Fact]
    public void Notes_written_by_hand_are_understood()
    {
        var root = TaskMarkdown.Parse("# T\n\n## Notes\n\n> [!info] 2026-01-04 08:15\n> called the vendor\n", "T", Utc).Root;

        var note = Assert.Single(root.Notes);
        Assert.Equal(("called the vendor", ActorKind.User, new DateTimeOffset(2026, 1, 4, 8, 15, 0, TimeSpan.Zero)), (note.Text, note.Author.Kind, note.At));
    }

    [Fact]
    public void Plain_files_without_frontmatter_take_the_title_from_the_file_name()
    {
        var parsed = TaskMarkdown.Parse("Just some text\n", "Call mom", Utc);

        Assert.Equal("Call mom", parsed.Root.Title);
        Assert.Equal("Just some text", parsed.Root.Details);
        Assert.True(parsed.NeedsWrite);
    }

    [Fact]
    public void Windows_line_endings_and_bom_are_kept()
    {
        var crlf = "\uFEFF" + Golden.Replace("\n", "\r\n", StringComparison.Ordinal);

        var parsed = TaskMarkdown.Parse(crlf, "Ship release 2.3", Utc);

        Assert.Equal(crlf, TaskMarkdown.Render(parsed.Root, Utc, parsed));
    }

    [Fact]
    public void Broken_frontmatter_is_reported_not_guessed()
    {
        Assert.Throws<VaultFormatException>(() => TaskMarkdown.Parse("---\nid: [unclosed\n---\n# T\n", "T", Utc));
    }

    [Fact]
    public void Done_root_tasks_record_status_and_completion()
    {
        var done = Golden.Replace("status: open\n", "status: done\ncompleted: 2026-01-06T08:00:00.000Z\n", StringComparison.Ordinal);

        var root = TaskMarkdown.Parse(done, "Ship release 2.3", Utc).Root;

        Assert.Equal(new DateTimeOffset(2026, 1, 6, 8, 0, 0, TimeSpan.Zero), root.CompletedAt);
    }

    [Fact]
    public void Marking_a_root_done_by_hand_fills_in_the_time()
    {
        var parsed = TaskMarkdown.Parse(Golden.Replace("status: open", "status: done", StringComparison.Ordinal), "x", Utc);

        Assert.Equal(Now, parsed.Root.CompletedAt);
        Assert.True(parsed.NeedsWrite);
    }

    [Fact]
    public void Copied_lines_with_duplicate_ids_get_fresh_ones()
    {
        var copied = Golden.Replace("\n\n## Notes", "\n3. [ ] Roll out B copy ^t0000004 %%{\"id\":\"7b0c2f9e-0000-4000-8000-000000000004\"}%%\n\n## Notes", StringComparison.Ordinal);

        var parsed = TaskMarkdown.Parse(copied, "x", Utc);

        Assert.True(parsed.NeedsWrite);
        Assert.Equal(3, parsed.Root.Children.Select(c => c.Id).Distinct().Count());
    }

    /// <summary>Parse, render, parse again: the second render must be identical and keep <paramref name="mustKeep"/>.</summary>
    private static string RoundTrip(string text, params string[] mustKeep)
    {
        var parsed = TaskMarkdown.Parse(text, "T", Utc);
        var rendered = TaskMarkdown.Render(parsed.Root, Utc, parsed);
        foreach (var keep in mustKeep)
        {
            Assert.Contains(keep, rendered, StringComparison.Ordinal);
        }

        var again = TaskMarkdown.Parse(rendered, "T", Utc);
        Assert.Equal(rendered, TaskMarkdown.Render(again.Root, Utc, again));
        return rendered;
    }

    [Fact]
    public void An_unclosed_code_fence_in_details_cannot_swallow_the_subtasks()
    {
        var parsed = TaskMarkdown.Parse("# T\n\n- [ ] a\n- [ ] b\n", "T", Utc);
        parsed.Root.Details = "Paste:\n```\nlet x = 1";

        var reread = TaskMarkdown.Parse(TaskMarkdown.Render(parsed.Root, Utc, parsed), "T", Utc);

        Assert.Equal(["a", "b"], reread.Root.Children.Select(c => c.Title));
    }

    [Fact]
    public void Fences_close_only_on_the_same_marker()
    {
        var root = TaskMarkdown.Parse("# T\n\n````\n```\n## Subtasks\n````\n\n## Subtasks\n\n- [ ] real\n", "T", Utc).Root;

        Assert.Equal(["real"], root.Children.Select(c => c.Title));
        Assert.Contains("## Subtasks\n````", root.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Example_checklists_inside_code_blocks_stay_examples()
    {
        var root = TaskMarkdown.Parse("# T\n\n## Subtasks\n\n- [ ] Document it\n\t```md\n\t- [ ] example\n\t```\n", "T", Utc).Root;

        var only = Assert.Single(root.Children);
        Assert.Empty(only.Children);
        Assert.Contains("- [ ] example", only.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Obsidian_tasks_conventions_survive_a_save()
    {
        RoundTrip(
            "# T\n\n## Subtasks\n\n- [/] Halfway\n- [>] Forwarded\n- [-] Dropped\n- [ ] Medium 🔼\n",
            "- [/] Halfway", "- [>] Forwarded", "- [-] Dropped", "- [ ] Medium 🔼");
    }

    [Fact]
    public void Attachment_links_keep_their_exact_form()
    {
        RoundTrip(
            "# T\n\n## Attachments\n\n- ![[diagram.png|300]]\n- [[spec.pdf]]\n- ![shot](shot.png)\n",
            "- ![[diagram.png|300]]", "- [[spec.pdf]]", "- ![shot](shot.png)");
    }

    [Fact]
    public void Headings_between_subtasks_and_sections_after_them_stay_in_place()
    {
        var text = RoundTrip(
            "# T\n\n## Subtasks\n\n### Phase 1\n\n- [ ] a\n\n### Phase 2\n\n- [ ] b\n\n## References\n\nSee the wiki.\n",
            "### Phase 1");

        Assert.True(text.IndexOf("### Phase 1", StringComparison.Ordinal) < text.IndexOf("- [ ] a", StringComparison.Ordinal));
        Assert.True(text.IndexOf("- [ ] a", StringComparison.Ordinal) < text.IndexOf("### Phase 2", StringComparison.Ordinal));
        Assert.True(text.IndexOf("### Phase 2", StringComparison.Ordinal) < text.IndexOf("- [ ] b", StringComparison.Ordinal));
        Assert.True(text.IndexOf("- [ ] b", StringComparison.Ordinal) < text.IndexOf("## References", StringComparison.Ordinal));
    }

    [Fact]
    public void Markdown_hard_line_breaks_are_kept()
    {
        var parsed = TaskMarkdown.Parse("# T\n\nfirst line  \nsecond line\n", "T", Utc);

        Assert.Equal("first line  \nsecond line", parsed.Root.Details);
    }

    [Fact]
    public void Property_values_the_app_does_not_use_are_never_replaced()
    {
        var text = RoundTrip(
            "---\nid: 202401011200\nstatus: waiting\npriority: medium\n# keep this comment\n---\n# T\n",
            "id: 202401011200", "status: waiting", "priority: medium", "# keep this comment");

        var parsed = TaskMarkdown.Parse(text, "T", Utc);
        Assert.Contains($"tt-id: {parsed.Root.Id}", text, StringComparison.Ordinal);
        Assert.False(parsed.Root.IsDone);
        Assert.Equal(Priority.Normal, parsed.Root.Priority);
    }

    [Fact]
    public void Completing_a_task_with_a_custom_status_writes_done()
    {
        var parsed = TaskMarkdown.Parse("---\nstatus: waiting\n---\n# T\n", "T", Utc);
        parsed.Root.CompletedAt = Now;

        Assert.Contains("status: done", TaskMarkdown.Render(parsed.Root, Utc, parsed), StringComparison.Ordinal);
    }

    [Fact]
    public void A_checklist_typed_into_the_details_of_an_app_task_stays_in_the_details()
    {
        var board = new TaskBoard();
        var root = board.AddTask(new NewTask("T") { Details = "Checklist:\n- [ ] typed in app" }, Actor.User, Now);

        var parsed = TaskMarkdown.Parse(TaskMarkdown.Render(root, Utc), "T", Utc);

        Assert.Empty(parsed.Root.Children);
        Assert.False(parsed.NeedsWrite);
        Assert.Contains("- [ ] typed in app", parsed.Root.Details, StringComparison.Ordinal);
    }

    /// <summary>Everything the markdown must carry, flattened for comparison.</summary>
    private static string Describe(WorkItem item)
    {
        static string T(DateTimeOffset? t) => t?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? "-";
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{item.Id}|{item.Title}|{item.Details}|{item.Priority}|{T(item.CreatedAt)}|{T(item.CompletedAt)}|{T(item.Deadline)}|{T(item.NextActionAt)}|{item.Sequential}|{item.StepDelay}|{string.Join(',', item.Tags)}|{string.Join(',', item.Labels)}\n");
        foreach (var r in item.Reminders)
        {
            sb.Append(CultureInfo.InvariantCulture, $" R {r.Id}|{T(r.DueAt)}|{r.Message}|{r.Kind}|{T(r.NotifiedAt)}|{T(r.DismissedAt)}\n");
        }

        foreach (var n in item.Notes)
        {
            sb.Append(CultureInfo.InvariantCulture, $" N {n.Id}|{T(n.At)}|{n.Text}|{n.Author.Kind}:{n.Author.Name}|{n.SourceUrl}|{n.SourceTitle}\n");
        }

        foreach (var a in item.Attachments)
        {
            sb.Append(CultureInfo.InvariantCulture, $" A {a.Id}|{a.FileName}|{a.Path}|{a.Size}|{T(a.AddedAt)}|{a.AddedBy.Kind}:{a.AddedBy.Name}\n");
        }

        foreach (var c in item.Children)
        {
            sb.Append(Describe(c));
        }

        return sb.ToString();
    }
}
