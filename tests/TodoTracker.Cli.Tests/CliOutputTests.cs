namespace TodoTracker.Cli.Tests;

/// <summary>What people see without --json, plus the edges of the argument and time parsing.</summary>
public sealed class CliOutputTests : IDisposable
{
    private readonly CliHarness _tt = new();

    public void Dispose() => _tt.Dispose();

    [Fact]
    public async Task Show_lists_facts_steps_and_attachments_in_plain_text()
    {
        var id = (await _tt.Json("add", "Launch !! due:2d #release", "--label", "Deep work")).Id().ToString();
        await _tt.Ok("steps", id, "Ring 0", "Ring 1");
        _tt.WorkingDirectory = Directory.CreateDirectory(Path.Combine(_tt.DataDirectory, "files")).FullName;
        await File.WriteAllTextAsync(Path.Combine(_tt.WorkingDirectory, "plan.txt"), new string('x', 2048));
        await _tt.Ok("attach", id, "plan.txt");
        await _tt.Ok("snooze", id, "1h");

        var output = await _tt.Ok("show", id);

        Assert.Contains("critical", output, StringComparison.Ordinal);
        Assert.Contains("due 2026-01-07 17:00", output, StringComparison.Ordinal);
        Assert.Contains("wakes 2026-01-05 10:00", output, StringComparison.Ordinal);
        Assert.Contains("[Deep work]", output, StringComparison.Ordinal);
        Assert.Contains("Steps", output, StringComparison.Ordinal);
        Assert.Contains("plan.txt  (2 KB)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_of_a_subtask_says_where_it_lives()
    {
        var parent = (await _tt.Json("add", "Trip")).Id().ToString();
        var child = (await _tt.Json("add", "Visa", "--under", parent)).Id().ToString();

        Assert.Contains("in Trip", await _tt.Ok("show", child), StringComparison.Ordinal);
        Assert.Contains("under Trip", await _tt.Ok("move", child, "--under", parent, "--index", "0"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Now_shows_steps_deadlines_and_tags_on_cards()
    {
        var id = (await _tt.Json("add", "Rollout")).Id().ToString();
        await _tt.Ok("steps", id, "Ring 0", "Ring 1");
        await _tt.Ok("add", "Patch nodes #infra");
        await _tt.Ok("add", "Pay bill due:today", "--group", "Personal");
        await _tt.Ok("add", "Overdue thing due:2026-01-01");

        var work = await _tt.Ok("now");
        var personal = await _tt.Ok("now", "-g", "Personal");

        Assert.Contains("step 1/2", work, StringComparison.Ordinal);
        Assert.Contains("#infra", work, StringComparison.Ordinal);
        Assert.Contains("overdue", work, StringComparison.Ordinal);
        Assert.Contains("[Personal]", personal, StringComparison.Ordinal);
        Assert.Contains("due in 8h", personal, StringComparison.Ordinal);
        Assert.Contains("Nothing matches", await _tt.Ok("now", "nothing-like-this"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_text_marks_done_tasks_and_says_when_nothing_matches()
    {
        Assert.Contains("No open tasks", await _tt.Ok("list"), StringComparison.Ordinal);
        var id = (await _tt.Json("add", "Old thing !")).Id().ToString();
        await _tt.Ok("done", id);

        Assert.Contains("[x] Old thing", await _tt.Ok("list", "--all"), StringComparison.Ordinal);
        Assert.Contains("Nothing matches zzz", await _tt.Ok("list", "zzz"), StringComparison.Ordinal);
        Assert.Contains("Reopened", await _tt.Ok("reopen", "old thing"), StringComparison.Ordinal);
        Assert.Contains("high", await _tt.Ok("list", "-g", "Work"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Groups_labels_vault_and_tags_in_plain_text()
    {
        Assert.Contains("No labels yet", await _tt.Ok("labels"), StringComparison.Ordinal);
        var id = (await _tt.Json("add", "x")).Id().ToString();

        Assert.Contains("Tags on", await _tt.Ok("tag", id, "+a"), StringComparison.Ordinal);
        Assert.Contains("(none)", await _tt.Ok("tag", id, "-a"), StringComparison.Ordinal);
        Assert.Contains("Work  (1 now, 0 waiting)", await _tt.Ok("groups"), StringComparison.Ordinal);
        var vault = await _tt.Ok("vault");
        Assert.Contains("Tasks folder:", vault, StringComparison.Ordinal);
        Assert.Contains("obsidian://", vault, StringComparison.Ordinal);
        Assert.Equal(0, (await _tt.Run("vault", "obsidian")).ExitCode);
        Assert.Equal(2, (await _tt.Run("vault", "bogus")).ExitCode);
    }

    [Fact]
    public async Task Vault_use_remembers_another_folder_for_the_app()
    {
        var target = Path.Combine(_tt.DataDirectory, "elsewhere");

        var output = await _tt.Ok("vault", "use", target);

        Assert.Contains("Restart Todo Tracker", output, StringComparison.Ordinal);
        Assert.Contains("elsewhere", await File.ReadAllTextAsync(Path.Combine(_tt.DataDirectory, "settings.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_for_one_command_and_usage_errors()
    {
        var add = await _tt.Run("help", "add");
        Assert.Equal(0, add.ExitCode);
        Assert.Contains("Quick words", add.Out, StringComparison.Ordinal);
        Assert.Equal(2, (await _tt.Run("help", "nope")).ExitCode);
        Assert.Equal(2, (await _tt.Run("now", "--json=yes")).ExitCode);
        Assert.Equal(2, (await _tt.Run("now", "--title", "x")).ExitCode);
        Assert.Equal(0, (await _tt.Run("-v")).ExitCode);

        var id = (await _tt.Json("add", "x")).Id().ToString();
        Assert.Equal(2, (await _tt.Run("edit", id)).ExitCode);
        Assert.Equal(2, (await _tt.Run("move", id, "--top", "--index", "-1")).ExitCode);
        Assert.Equal(2, (await _tt.Run("steps", id, "a", "--delay", "soon")).ExitCode);
        Assert.Equal(2, (await _tt.Run("note", id)).ExitCode);
        Assert.Equal(1, (await _tt.Run("attach", id, "missing.txt")).ExitCode);
    }

    [Fact]
    public async Task Words_after_double_dash_are_never_options()
    {
        var result = await _tt.Run("add", "--json", "--", "--weird", "title", "--json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("--weird title --json", result.Json.Str("title"));
    }

    [Fact]
    public async Task Ids_that_are_not_tasks_and_long_ambiguities_are_explained()
    {
        Assert.Contains("No task has id", (await _tt.Run("show", Guid.NewGuid().ToString())).Error, StringComparison.Ordinal);
        for (var i = 0; i < 10; i++)
        {
            await _tt.Ok("add", $"Call {i}");
        }

        Assert.Contains("and 2 more", (await _tt.Run("show", "call")).Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2026-01-06T14:30", "2026-01-06T14:30:00+00:00")]
    [InlineData("2026-01-06T14:30-08:00", "2026-01-06T14:30:00-08:00")]
    [InlineData("2026-01-06T14:30Z", "2026-01-06T14:30:00+00:00")]
    public async Task Exact_times_are_understood(string when, string expected)
    {
        var id = (await _tt.Json("add", "Call")).Id().ToString();

        var item = await _tt.Json("snooze", id, when);

        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), item["nextActionAt"]!.GetValue<DateTimeOffset>());
    }

    [Fact]
    public async Task Deadlines_accept_spans_and_exact_times()
    {
        var id = (await _tt.Json("add", "Report")).Id().ToString();

        Assert.Equal(CliHarness.T0.AddHours(3), (await _tt.Json("edit", id, "--due", "3h"))["deadline"]!.GetValue<DateTimeOffset>());
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero), (await _tt.Json("edit", id, "--due", "2026-03-01T12:00"))["deadline"]!.GetValue<DateTimeOffset>());
        Assert.Equal(1, (await _tt.Run("edit", id, "--due", "whenever")).ExitCode);
    }
}
