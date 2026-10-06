namespace TodoTracker.Cli.Tests;

public sealed class CliCommandTests : IDisposable
{
    private readonly CliHarness _tt = new();

    public void Dispose() => _tt.Dispose();

    [Fact]
    public async Task Add_writes_a_markdown_task_and_now_shows_it()
    {
        var output = await _tt.Ok("add", "Book dentist");

        Assert.Contains("Book dentist", output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_tt.VaultDirectory, "Work", "Book dentist.md")));
        var now = await _tt.Ok("now");
        Assert.Contains("Book dentist", now, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_understands_quick_capture_words_and_options()
    {
        var item = await _tt.Json("add", "Ship report !! due:3d #release", "--group", "personal", "--label", "Deep work", "--details", "Q3 numbers");

        Assert.Equal("Ship report", item.Str("title"));
        Assert.Equal("critical", item.Str("priority"));
        Assert.Equal(["release"], item["tags"].Strings());
        Assert.Equal("Deep work", item["labels"]![0].Str("name"));
        Assert.Equal("Q3 numbers", item.Str("details"));
        Assert.Equal(CliHarness.T0.AddDays(3).Date.AddHours(17), item["deadline"]!.GetValue<DateTimeOffset>().UtcDateTime);
        Assert.True(File.Exists(Path.Combine(_tt.VaultDirectory, "Personal", "Ship report.md")));
    }

    [Fact]
    public async Task Words_after_add_are_joined_so_quotes_are_optional()
    {
        var item = await _tt.Json("add", "Call", "the", "bank", "@2h");

        Assert.Equal("Call the bank", item.Str("title"));
        Assert.Equal("waiting", item.Str("state"));
    }

    [Fact]
    public async Task Tasks_are_found_by_id_prefix_full_id_or_unique_title()
    {
        var id = (await _tt.Json("add", "Renew passport")).Id();
        await _tt.Ok("add", "Pay rent");

        Assert.Equal(id, (await _tt.Json("show", id.ToString("N")[..6])).Id());
        Assert.Equal(id, (await _tt.Json("show", id.ToString())).Id());
        Assert.Equal(id, (await _tt.Json("show", "passport")).Id());
        Assert.Equal(id, (await _tt.Json("show", "renew", "PASSPORT")).Id());
    }

    [Fact]
    public async Task Ambiguous_or_unknown_tasks_are_errors_that_list_the_candidates()
    {
        await _tt.Ok("add", "Email Alex");
        await _tt.Ok("add", "Email Sam");

        var ambiguous = await _tt.Run("done", "email");
        Assert.Equal(1, ambiguous.ExitCode);
        Assert.Contains("Email Alex", ambiguous.Error, StringComparison.Ordinal);
        Assert.Contains("Email Sam", ambiguous.Error, StringComparison.Ordinal);

        var missing = await _tt.Run("done", "nothing like this");
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("No task matches", missing.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exact_title_wins_over_partial_matches()
    {
        var id = (await _tt.Json("add", "Report")).Id();
        await _tt.Ok("add", "Report to finance");

        Assert.Equal(id, (await _tt.Json("show", "report")).Id());
    }

    [Fact]
    public async Task Done_and_reopen()
    {
        var id = (await _tt.Json("add", "Water plants")).Id().ToString();

        Assert.Equal("done", (await _tt.Json("done", id)).Str("state"));
        Assert.Equal("actionable", (await _tt.Json("reopen", id)).Str("state"));
    }

    [Fact]
    public async Task Done_takes_several_tasks()
    {
        var a = (await _tt.Json("add", "First thing")).Id().ToString("N")[..8];
        var b = (await _tt.Json("add", "Second thing")).Id().ToString("N")[..8];

        var output = await _tt.Ok("done", a, b);

        Assert.Contains("First thing", output, StringComparison.Ordinal);
        Assert.Contains("Second thing", output, StringComparison.Ordinal);
        Assert.Empty((await _tt.Json("now"))["now"]!.AsArray());
    }

    [Fact]
    public async Task Done_on_a_title_only_considers_open_tasks()
    {
        var old = (await _tt.Json("add", "Weekly review")).Id().ToString();
        await _tt.Ok("done", old);
        var current = (await _tt.Json("add", "Weekly review")).Id();

        Assert.Equal(current, (await _tt.Json("done", "weekly review")).Id());
    }

    [Fact]
    public async Task Done_with_unquoted_title_words_completes_only_that_task()
    {
        await _tt.Ok("add", "Renew passport");
        await _tt.Ok("add", "passport photo");

        var done = await _tt.Json("done", "Renew", "passport");

        Assert.Equal("Renew passport", done.Str("title"));
        Assert.Equal(["passport photo"], (await _tt.Json("list")).Titles());
    }

    [Fact]
    public async Task Done_with_several_ids_returns_all_of_them()
    {
        var a = (await _tt.Json("add", "One")).Id().ToString("N")[..8];
        var b = (await _tt.Json("add", "Two")).Id().ToString("N")[..8];

        var done = await _tt.Json("done", a, b);

        Assert.Equal(["One", "Two"], done.Titles());
    }

    [Fact]
    public async Task A_failed_version_save_doesnt_turn_a_saved_change_into_a_failure()
    {
        using var tt = new CliHarness(history: true) { Git = Path.Combine(Path.GetTempPath(), "no-such-git", "git") };

        var result = await tt.Run("add", "Still saved");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["Still saved"], (await tt.Json("list")).Titles());
    }

    [Fact]
    public async Task Snooze_rejects_past_times_and_leftover_words()
    {
        var id = (await _tt.Json("add", "Milk")).Id().ToString();

        Assert.Contains("past", (await _tt.Run("snooze", id, "2026-01-01T08:00")).Error, StringComparison.Ordinal);
        Assert.Equal(1, (await _tt.Run("snooze", id, "2h", "garbage", "words")).ExitCode);
    }

    [Fact]
    public async Task A_vault_folder_given_with_dash_dash_vault_must_exist()
    {
        var result = await _tt.Run("now", "--vault", Path.Combine(_tt.DataDirectory, "typo"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("no folder", result.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_tt.DataDirectory, "typo")));
    }

    [Fact]
    public async Task List_includes_done_tasks_only_for_a_real_is_filter()
    {
        var id = (await _tt.Json("add", "Analysis: Q3")).Id().ToString();
        await _tt.Ok("done", id);

        Assert.Empty((await _tt.Json("list", "analysis:")).AsArray());
        Assert.Single((await _tt.Json("list", "analysis", "is:done")).AsArray());
    }

    [Fact]
    public async Task History_explains_when_it_was_turned_off_for_this_command()
    {
        var id = (await _tt.Json("add", "x")).Id().ToString();

        Assert.Contains("--no-history", (await _tt.Run("history", id, "--no-history")).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task First_puts_a_task_at_the_top_of_do_now()
    {
        await _tt.Ok("add", "Urgent thing !!");
        await _tt.Ok("add", "Call", "the", "bank");

        var dashboard = await _tt.Json("first", "call", "the", "bank");

        Assert.Equal("Call the bank", dashboard["focus"].Str("title"));
        Assert.Contains("Focus", await _tt.Ok("first", "urgent"), StringComparison.Ordinal);
        Assert.Equal("Urgent thing", (await _tt.Json("now"))["focus"].Str("title"));
    }

    [Fact]
    public async Task Subtasks_are_added_under_a_parent()
    {
        var parent = (await _tt.Json("add", "Plan trip")).Id().ToString("N")[..8];

        var child = await _tt.Json("add", "Book flights", "--under", parent);

        Assert.Equal(["Plan trip", "Book flights"], child["path"].Strings());
        var md = await File.ReadAllTextAsync(Path.Combine(_tt.VaultDirectory, "Work", "Plan trip.md"));
        Assert.Contains("- [ ] Book flights", md, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Steps_add_ordered_steps()
    {
        var parent = (await _tt.Json("add", "Roll out 2.3")).Id().ToString();

        await _tt.Ok("steps", parent, "Ring 0", "Ring 1", "--delay", "24");

        var item = await _tt.Json("show", parent);
        Assert.True(item["sequential"]!.GetValue<bool>());
        Assert.Equal(["Ring 0", "Ring 1"], item["children"].Titles());
    }

    [Fact]
    public async Task Note_logs_progress_from_arguments_or_stdin()
    {
        var id = (await _tt.Json("add", "Write blog post")).Id().ToString();

        await _tt.Ok("note", id, "Drafted", "intro");
        var piped = await _tt.RunWithInput("Outline done\nnext: examples", "note", id, "-");

        Assert.Equal(0, piped.ExitCode);
        var notes = (await _tt.Json("show", id))["notes"]!.AsArray().Select(n => n.Str("text")).ToList();
        Assert.Contains("Drafted intro", notes);
        Assert.Contains("Outline done\nnext: examples", notes);
    }

    [Theory]
    [InlineData("2h", 120)]
    [InlineData("45m", 45)]
    [InlineData("3d", 3 * 24 * 60)]
    public async Task Snooze_defers_a_task(string when, int minutes)
    {
        var id = (await _tt.Json("add", "Check build")).Id().ToString();

        var item = await _tt.Json("snooze", id, when);

        Assert.Equal("waiting", item.Str("state"));
        Assert.Equal(CliHarness.T0.AddMinutes(minutes), item["nextActionAt"]!.GetValue<DateTimeOffset>());
    }

    [Fact]
    public async Task Snooze_accepts_tomorrow_and_dates()
    {
        var id = (await _tt.Json("add", "Call plumber")).Id().ToString();

        Assert.Equal(new DateTimeOffset(2026, 1, 6, 9, 0, 0, TimeSpan.Zero), (await _tt.Json("snooze", id, "tomorrow"))["nextActionAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 9, 0, 0, TimeSpan.Zero), (await _tt.Json("snooze", id, "2026-02-01"))["nextActionAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(1, (await _tt.Run("snooze", id, "someday")).ExitCode);
    }

    [Fact]
    public async Task List_searches_with_the_query_language()
    {
        await _tt.Ok("add", "Fix login #bug");
        await _tt.Ok("add", "Write docs");
        await _tt.Ok("add", "Groceries", "--group", "Personal");

        Assert.Equal(["Fix login"], (await _tt.Json("list", "#bug")).Titles());
        Assert.Equal(["Groceries"], (await _tt.Json("list", "group:personal")).Titles());
        Assert.Equal(3, (await _tt.Json("list")).AsArray().Count);
        Assert.Contains("Write docs", await _tt.Ok("list", "docs"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_changes_fields_and_clears_the_deadline()
    {
        var id = (await _tt.Json("add", "Taxes due:2026-03-01")).Id().ToString();

        var item = await _tt.Json("edit", id, "--title", "File taxes", "--priority", "high", "--details", "Use the new form");
        Assert.Equal("File taxes", item.Str("title"));
        Assert.Equal("high", item.Str("priority"));
        Assert.Equal("Use the new form", item.Str("details"));
        Assert.NotNull(item["deadline"]);

        Assert.Null((await _tt.Json("edit", id, "--due", "none"))["deadline"]);
        Assert.NotNull((await _tt.Json("edit", id, "--due", "5d"))["deadline"]);
    }

    [Fact]
    public async Task Tag_and_label_add_and_remove()
    {
        var id = (await _tt.Json("add", "Refactor #tech")).Id().ToString();

        var tagged = await _tt.Json("tag", id, "+infra/k8s", "-tech", "debt");
        Assert.Equal(["infra/k8s", "debt"], tagged["tags"].Strings());

        var labeled = await _tt.Json("label", id, "+Deep work", "+Quick win");
        Assert.Equal(["Deep work", "Quick win"], labeled["labels"]!.AsArray().Select(l => l.Str("name")));
        Assert.Equal(["Quick win"], (await _tt.Json("label", id, "-Deep work"))["labels"]!.AsArray().Select(l => l.Str("name")));
    }

    [Fact]
    public async Task Move_reparents_promotes_and_changes_group()
    {
        var trip = (await _tt.Json("add", "Trip")).Id().ToString();
        var visa = (await _tt.Json("add", "Visa")).Id().ToString();

        Assert.Equal(["Trip", "Visa"], (await _tt.Json("move", visa, "--under", trip))["path"].Strings());
        Assert.Equal(["Visa"], (await _tt.Json("move", visa, "--top"))["path"].Strings());

        await _tt.Ok("move", visa, "--group", "Personal");
        Assert.True(File.Exists(Path.Combine(_tt.VaultDirectory, "Personal", "Visa.md")));
        Assert.Equal(2, (await _tt.Run("move", visa)).ExitCode);
    }

    [Fact]
    public async Task Attach_copies_a_file_into_the_vault()
    {
        var id = (await _tt.Json("add", "Investigate outage")).Id().ToString();
        Directory.CreateDirectory(_tt.DataDirectory);
        _tt.WorkingDirectory = _tt.DataDirectory;
        await File.WriteAllTextAsync(Path.Combine(_tt.DataDirectory, "log.txt"), "boom");

        var attachment = await _tt.Json("attach", id, "log.txt");

        Assert.Equal("log.txt", attachment.Str("fileName"));
        var item = await _tt.Json("show", id);
        Assert.Equal("log.txt", item["attachments"]![0].Str("fileName"));
        Assert.Single(Directory.GetFiles(Path.Combine(_tt.VaultDirectory, "_attachments"), "log.txt", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Show_prints_a_readable_summary()
    {
        var id = (await _tt.Json("add", "Plan party #fun", "--details", "Saturday")).Id().ToString();
        await _tt.Ok("add", "Invite friends", "--under", id);
        await _tt.Ok("note", id, "Booked the venue");

        var output = await _tt.Ok("show", id);

        Assert.Contains("Plan party", output, StringComparison.Ordinal);
        Assert.Contains("#fun", output, StringComparison.Ordinal);
        Assert.Contains("Saturday", output, StringComparison.Ordinal);
        Assert.Contains("[ ] ", output, StringComparison.Ordinal);
        Assert.Contains("Invite friends", output, StringComparison.Ordinal);
        Assert.Contains("Booked the venue", output, StringComparison.Ordinal);
        Assert.Contains(Path.Combine("Work", "Plan party.md"), output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Now_lists_focus_now_and_waiting_with_short_ids()
    {
        var id = (await _tt.Json("add", "Urgent fix !!")).Id();
        await _tt.Ok("add", "Later thing @2h");

        var output = await _tt.Ok("now");

        Assert.Contains(id.ToString("N")[..8], output, StringComparison.Ordinal);
        Assert.Contains("Urgent fix", output, StringComparison.Ordinal);
        Assert.Contains("Waiting", output, StringComparison.Ordinal);
        Assert.Contains("in 2h", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Now_with_no_tasks_suggests_adding_one()
    {
        var output = await _tt.Ok();

        Assert.Contains("tt add", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Groups_and_labels_are_listed()
    {
        await _tt.Ok("add", "x", "--label", "Errand");

        Assert.Equal(["Work", "Personal"], (await _tt.Json("groups")).AsArray().Select(g => g.Str("name")));
        Assert.Contains("Errand", await _tt.Ok("labels"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_changes_are_attributed()
    {
        var id = (await _tt.Json("add", "Summarize meeting", "--as", "claude")).Id();

        var timeline = await File.ReadAllTextAsync(Path.Combine(_tt.VaultDirectory, ".todo-tracker", "activity.jsonl"));

        Assert.Contains("claude", timeline, StringComparison.Ordinal);
        Assert.Contains(id.ToString(), timeline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Vault_shows_where_the_files_are_and_the_guide()
    {
        var info = await _tt.Json("vault");
        Assert.Equal(Path.GetFullPath(_tt.VaultDirectory), info.Str("path"));

        Assert.Contains("Subtasks", await _tt.Ok("vault", "guide"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_and_errors()
    {
        var help = await _tt.Run("help");
        Assert.Equal(0, help.ExitCode);
        foreach (var command in new[] { "now", "add", "list", "show", "done", "note", "snooze", "move", "mcp" })
        {
            Assert.Contains(command, help.Out, StringComparison.Ordinal);
        }

        Assert.Equal(0, (await _tt.Run("add", "--help")).ExitCode);
        Assert.Equal(2, (await _tt.Run("frobnicate")).ExitCode);
        Assert.Equal(2, (await _tt.Run("add")).ExitCode);
        Assert.Equal(2, (await _tt.Run("now", "--bogus")).ExitCode);
        Assert.Equal(2, (await _tt.Run("add", "x", "--group")).ExitCode);
        Assert.Matches(@"^\d+\.\d+\.\d+", (await _tt.Ok("--version")).Trim());
    }

    [Fact]
    public async Task Domain_errors_exit_1_with_a_friendly_message()
    {
        var result = await _tt.Run("add", "x", "--group", "Nope");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Unknown group", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Parameter", result.Error, StringComparison.Ordinal);
    }
}
