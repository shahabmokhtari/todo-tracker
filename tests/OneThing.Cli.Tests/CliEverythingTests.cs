using System.Text.Json.Nodes;

namespace OneThing.Cli.Tests;

/// <summary>Everything the app does is reachable from ot: the commands beyond add, done and snooze.</summary>
public sealed class CliEverythingTests : IDisposable
{
    private readonly CliHarness _tt = new();

    public void Dispose() => _tt.Dispose();

    private async Task<string> Add(string title, params string[] more) => (await _tt.Json(["add", title, .. more])).Str("id");

    [Fact]
    public async Task Unarchive_brings_an_archived_task_back_to_the_done_list()
    {
        var id = await Add("Old report");
        await _tt.Ok("done", id);
        await _tt.Ok("archive", id);

        var item = await _tt.Json("unarchive", id);

        Assert.Null(item["archivedAt"]);
        Assert.Equal("done", item.Str("state"));
    }

    [Fact]
    public async Task Remind_adds_a_reminder_without_deferring_and_dismiss_clears_it()
    {
        var id = await Add("Call the bank");

        var item = await _tt.Json("remind", id, "2h", "--message", "Ask about the fee");

        Assert.Equal("actionable", item.Str("state"));
        var reminder = item["reminders"]!.AsArray().Single()!;
        Assert.Equal("Ask about the fee", reminder.Str("message"));
        Assert.Equal(CliHarness.T0.AddHours(2), reminder["dueAt"]!.GetValue<DateTimeOffset>());
        var dismissed = await _tt.Json("remind", id, "--dismiss", reminder.Str("id")[..8]);
        Assert.NotNull(dismissed["reminders"]![0]!["dismissedAt"]);
    }

    [Fact]
    public async Task Snooze_clear_brings_a_task_back_to_do_now()
    {
        var id = await Add("Call the bank");
        await _tt.Ok("snooze", id, "tomorrow");

        var item = await _tt.Json("snooze", id, "clear");

        Assert.Equal("actionable", item.Str("state"));
        Assert.Null(item["nextActionAt"]);
    }

    [Fact]
    public async Task An_agent_corrects_only_its_own_notes()
    {
        var id = await Add("Ship release");
        await _tt.Ok("note", id, "Waiting for legal");

        var refused = await _tt.Run("note", id, "--edit", "last", "Legal said yes", "--as", "bot");
        _tt.Time.Advance(TimeSpan.FromMinutes(1));
        await _tt.Ok("note", id, "Legal said yes", "--as", "bot");
        var own = await _tt.Json("note", id, "--edit", "last", "Legal said yes, in writing", "--as", "bot");

        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("add a new note instead", refused.Error, StringComparison.Ordinal);
        Assert.Equal("Legal said yes, in writing", own.Str("text"));
        Assert.Equal(["Legal said yes, in writing", "Waiting for legal"], (await _tt.Json("show", id))!["notes"]!.AsArray().Select(n => n.Str("text")).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_note_can_carry_a_link_and_be_corrected()
    {
        var id = await Add("Ship release");

        var note = await _tt.Json("note", id, "Read", "the", "changelog", "--url", "https://example.com/changes", "--title", "Changes");
        var fixedNote = await _tt.Json("note", id, "--edit", "last", "Read the whole changelog");

        Assert.Equal("https://example.com/changes", note.Str("sourceUrl"));
        Assert.Equal("Changes", note.Str("sourceTitle"));
        Assert.Equal(note.Str("id"), fixedNote.Str("id"));
        Assert.Equal("Read the whole changelog", fixedNote.Str("text"));
    }

    [Fact]
    public async Task Order_arranges_do_now_and_move_before_and_after_reorder_subtasks()
    {
        var a = await Add("Alpha");
        var b = await Add("Beta");
        var c = await Add("Gamma");

        var now = await _tt.Json("order", c, a, b);
        Assert.Equal(["Gamma", "Alpha", "Beta"], now["now"]!.AsArray().Select(n => n.Str("title")));

        var parent = await Add("Trip");
        var one = await Add("Book flights", "--under", parent);
        var two = await Add("Book hotel", "--under", parent);
        var three = await Add("Pack", "--under", parent);
        await _tt.Ok("move", three, "--before", one);
        await _tt.Ok("move", one, "--after", two);

        var children = (await _tt.Json("show", parent))["children"]!.AsArray().Select(n => n.Str("title"));
        Assert.Equal(["Pack", "Book hotel", "Book flights"], children);
    }

    [Fact]
    public async Task Log_records_time_and_can_correct_an_entry()
    {
        var id = await Add("Weekly review");

        var entry = await _tt.Json("log", id, "1h30m", "--at", "2026-01-05T07:00:00Z");
        var fixedEntry = await _tt.Json("log", id, "45m", "--edit", "last");

        Assert.Equal(90 * 60, entry["seconds"]!.GetValue<long>());
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 7, 0, 0, TimeSpan.Zero), entry["start"]!.GetValue<DateTimeOffset>());
        Assert.Equal(entry.Str("id"), fixedEntry.Str("id"));
        Assert.Equal(45 * 60, fixedEntry["seconds"]!.GetValue<long>());
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 7, 0, 0, TimeSpan.Zero), fixedEntry["start"]!.GetValue<DateTimeOffset>());
    }

    [Fact]
    public async Task A_logged_time_without_a_start_ends_now()
    {
        var id = await Add("Meeting");

        var entry = await _tt.Json("log", id, "30");

        Assert.Equal(CliHarness.T0, entry["end"]!.GetValue<DateTimeOffset>());
    }

    [Fact]
    public async Task Focus_starts_pauses_resumes_and_resets_the_timer()
    {
        var id = await Add("Write the draft");

        var started = await _tt.Json("focus", "start", id);
        Assert.Equal("focus", started.Str("phase"));
        Assert.True(started["running"]!.GetValue<bool>());
        Assert.Equal("Write the draft", started.Str("itemTitle"));

        Assert.False((await _tt.Json("focus", "pause"))["running"]!.GetValue<bool>());
        Assert.True((await _tt.Json("focus", "resume"))["running"]!.GetValue<bool>());
        Assert.Equal("focus", (await _tt.Json("focus"))["phase"]!.GetValue<string>());
        Assert.Equal("idle", (await _tt.Json("focus", "reset")).Str("phase"));
    }

    [Fact]
    public async Task Focus_lengths_are_shown_and_changed_leaving_the_rest_and_say_who_changed_them()
    {
        var before = await _tt.Json("focus", "lengths");
        Assert.Equal((25, 5, 15, 4), Lengths(before));
        Assert.Contains("25 min focus", (await _tt.Ok("focus", "lengths")), StringComparison.Ordinal);

        var set = await _tt.Json("focus", "lengths", "--focus", "50", "--long-break", "30", "--as", "claude");
        Assert.Equal((50, 5, 30, 4), Lengths(set));
        Assert.Equal((50, 10, 30, 3), Lengths(await _tt.Json("focus", "lengths", "--break", "10", "--sessions", "3")));
        Assert.Equal(50 * 60, (await _tt.Json("focus", "start"))["remainingSeconds"]!.GetValue<int>());
        var timeline = await _tt.Json("timeline");
        Assert.Contains(timeline.AsArray(), e => e.Str("summary").StartsWith("Focus timer: 50 min focus", StringComparison.Ordinal) && e.Str("actorKind") == "agent");

        var refused = await _tt.Run("focus", "lengths", "--focus", "500");
        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("Focus is 1 to 180 minutes", refused.Error, StringComparison.Ordinal);
        Assert.Equal(2, (await _tt.Run("focus", "lengths", "--focus", "lots")).ExitCode);
        Assert.Equal(50, (await _tt.Json("focus", "lengths"))["focusMinutes"]!.GetValue<int>());

        static (int, int, int, int) Lengths(JsonNode n) => (n["focusMinutes"]!.GetValue<int>(), n["shortBreakMinutes"]!.GetValue<int>(), n["longBreakMinutes"]!.GetValue<int>(), n["focusesBeforeLongBreak"]!.GetValue<int>());
    }

    [Fact]
    public async Task Plan_puts_a_block_on_the_plan_and_takes_it_off()
    {
        var id = await Add("Deep work");

        var planned = await _tt.Json("plan", id, "2026-01-05T14:00:00Z", "--minutes", "90");
        Assert.Equal(90, planned["plannedMinutes"]!.GetValue<int>());

        var plan = await _tt.Json("plan", "--from", "2026-01-05", "--days", "1");
        Assert.Equal("Deep work", plan["blocks"]!.AsArray().Single().Str("title"));

        var off = await _tt.Json("plan", id, "off");
        Assert.Null(off["plannedAt"]);
    }

    [Fact]
    public async Task Plan_settings_are_shown_and_changed()
    {
        var settings = await _tt.Json("plan", "settings", "--view", "day", "--work-hours", "8:30-16:00", "--work-days", "1-4", "--week-start", "sun");

        Assert.Equal("day", settings.Str("view"));
        Assert.Equal(8 * 60 + 30, settings["workStart"]!.GetValue<int>());
        Assert.Equal(16 * 60, settings["workEnd"]!.GetValue<int>());
        Assert.Equal([1, 2, 3, 4], settings["workDays"]!.AsArray().Select(d => d!.GetValue<int>()));
        Assert.Equal(7, settings["weekStart"]!.GetValue<int>());
        Assert.Equal("day", (await _tt.Json("plan", "settings")).Str("view"));
    }

    [Fact]
    public async Task Edit_sets_repeat_icon_color_sequential_and_step_delay()
    {
        var id = await Add("Water plants", "--repeat", "every 3 days", "--icon", "🌱");

        var item = await _tt.Json("edit", id, "--color", "#22c55e", "--sequential", "on", "--step-delay", "24");
        Assert.Equal("🌱", item.Str("icon"));
        Assert.Equal("#22c55e", item.Str("color"));
        Assert.True(item["sequential"]!.GetValue<bool>());
        Assert.Equal(24 * 60, item["stepDelayMinutes"]!.GetValue<int>());
        Assert.StartsWith("every 3 days", item.Str("repeat"), StringComparison.Ordinal);

        var cleared = await _tt.Json("edit", id, "--repeat", "none", "--icon", "none", "--color", "none", "--sequential", "off", "--step-delay", "none");
        Assert.Null(cleared["repeat"]);
        Assert.Null(cleared["icon"]);
        Assert.Null(cleared["color"]);
        Assert.False(cleared["sequential"]!.GetValue<bool>());
        Assert.Null(cleared["stepDelayMinutes"]);
    }

    [Fact]
    public async Task Labels_are_defined_renamed_and_recolored_and_tags_are_counted()
    {
        await _tt.Ok("labels", "add", "Deep", "work", "--color", "#7c3aed");
        await _tt.Ok("labels", "rename", "Deep work", "Focus");
        var labels = await _tt.Json("labels", "color", "Focus", "#0ea5e9");

        Assert.Contains(labels.AsArray(), l => l.Str("name") == "Focus" && l.Str("color") == "#0ea5e9");
        await Add("One #admin");
        await Add("Two #admin #home");
        var tags = await _tt.Json("tags");
        Assert.Equal("admin", tags[0].Str("name"));
        Assert.Equal(2, tags[0]!["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Rich_html_is_saved_read_and_removed()
    {
        var id = await Add("Dashboard");
        var file = Path.Combine(_tt.DataDirectory, "table.html");
        Directory.CreateDirectory(_tt.DataDirectory);
        await File.WriteAllTextAsync(file, "<table><tr><td>42</td></tr></table>");

        Assert.True((await _tt.Json("rich", id, file))["hasRich"]!.GetValue<bool>());
        Assert.Contains("<td>42</td>", await _tt.Ok("rich", id), StringComparison.Ordinal);
        Assert.False((await _tt.Json("rich", id, "none"))["hasRich"]!.GetValue<bool>());
        var fromInput = await _tt.RunWithInput("<p>typed</p>", "rich", id, "-");
        Assert.Equal(0, fromInput.ExitCode);
        Assert.Contains("<p>typed</p>", await _tt.Ok("rich", id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_on_stdin_or_an_empty_file_never_removes_the_rich_version()
    {
        var id = await Add("Dashboard");
        await _tt.RunWithInput("<p>kept</p>", "rich", id, "-");
        var empty = Path.Combine(_tt.DataDirectory, "empty.html");
        await File.WriteAllTextAsync(empty, "  \n", TestContext.Current.CancellationToken);

        var fromNothing = await _tt.RunWithInput(string.Empty, "rich", id, "-");
        var fromEmpty = await _tt.Run("rich", id, empty);

        Assert.Equal((1, 1), (fromNothing.ExitCode, fromEmpty.ExitCode));
        Assert.Contains("ot rich <task> none", fromNothing.Error, StringComparison.Ordinal);
        Assert.Contains("<p>kept</p>", await _tt.Ok("rich", id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_attachment_is_saved_out_of_the_tasks_folder()
    {
        var id = await Add("Taxes");
        Directory.CreateDirectory(_tt.DataDirectory);
        var source = Path.Combine(_tt.DataDirectory, "receipt.txt");
        await File.WriteAllTextAsync(source, "paid 42");
        await _tt.Ok("attach", id, source);
        var target = Path.Combine(_tt.DataDirectory, "out", "copy.txt");

        var saved = await _tt.Json("attachment", id, "receipt.txt", "--to", target);

        Assert.Equal("paid 42", await File.ReadAllTextAsync(target));
        Assert.Equal(target, saved.Str("savedTo"));
        Assert.Equal(0, (await _tt.Run("attachment", id, "receipt", "--to", "-")).ExitCode);
        Assert.Equal("paid 42"u8.ToArray(), _tt.RawOutput.ToArray());
    }

    [Fact]
    public async Task An_attachment_goes_to_stdout_byte_for_byte()
    {
        var id = await Add("Scan");
        Directory.CreateDirectory(_tt.DataDirectory);
        var source = Path.Combine(_tt.DataDirectory, "scan.bin");
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).Concat(new byte[] { 0xFF, 0xFE, 0x0D, 0x0A, 0x00 }).ToArray();
        await File.WriteAllBytesAsync(source, bytes, TestContext.Current.CancellationToken);
        await _tt.Ok("attach", id, source);

        var result = await _tt.Run("attachment", id, "scan.bin", "--to", "-");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(bytes, _tt.RawOutput.ToArray());
    }

    [Fact]
    public async Task Export_prints_the_whole_board_as_json()
    {
        await Add("Exported");

        var result = await _tt.Run("export");

        Assert.Equal(0, result.ExitCode);
        var board = JsonNode.Parse(result.Out)!;
        Assert.NotNull(board["groups"]);
        Assert.Contains("Exported", result.Out, StringComparison.Ordinal);
        var file = Path.Combine(_tt.DataDirectory, "board.json");
        await _tt.Ok("export", "--to", file);
        Assert.Contains("Exported", await File.ReadAllTextAsync(file), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_never_overwrites_a_file_unless_told_and_never_writes_into_the_tasks_folder()
    {
        await Add("Exported");
        Directory.CreateDirectory(_tt.DataDirectory);
        var file = Path.Combine(_tt.DataDirectory, "mine.json");
        await File.WriteAllTextAsync(file, "my own file", TestContext.Current.CancellationToken);
        var task = Directory.GetFiles(_tt.VaultDirectory, "Exported.md", SearchOption.AllDirectories).Single();
        var before = await File.ReadAllTextAsync(task, TestContext.Current.CancellationToken);

        var refused = await _tt.Run("export", "--to", file);
        var intoTasks = await _tt.Run("export", "--to", task, "--force");
        var beside = await _tt.Run("export", "--to", Path.Combine(_tt.VaultDirectory, "board.json"), "--force");

        Assert.Equal((1, 1, 1), (refused.ExitCode, intoTasks.ExitCode, beside.ExitCode));
        Assert.Contains("--force", refused.Error, StringComparison.Ordinal);
        Assert.Contains("tasks folder", intoTasks.Error, StringComparison.Ordinal);
        Assert.Equal("my own file", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
        Assert.Equal(before, await File.ReadAllTextAsync(task, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(_tt.VaultDirectory, "board.json")));

        await _tt.Ok("export", "--to", file, "--force");
        Assert.Contains("Exported", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_change_the_theme_and_only_you_set_where_reminders_go()
    {
        Assert.Equal("dark", (await _tt.Json("settings", "theme", "dark")).Str("theme"));
        Assert.Equal("dark", (await _tt.Json("settings")).Str("theme"));

        var agent = await _tt.Run("settings", "teams", "https://example.webhook.office.com/x", "--as", "claude");
        Assert.Equal(1, agent.ExitCode);
        Assert.Contains("Only you", agent.Error, StringComparison.Ordinal);
        Assert.True((await _tt.Json("settings", "teams", "https://example.webhook.office.com/x"))["teamsConfigured"]!.GetValue<bool>());
        Assert.False((await _tt.Json("settings", "teams", "off"))["teamsConfigured"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Plugins_are_listed_and_only_you_switch_them()
    {
        var plugins = await _tt.Json("plugins");
        Assert.Contains(plugins.AsArray(), p => p.Str("id") == "plan");

        var agent = await _tt.Run("plugins", "off", "plan", "--as", "claude");
        Assert.Equal(1, agent.ExitCode);
        Assert.Contains("Only you", agent.Error, StringComparison.Ordinal);

        await _tt.Ok("plugins", "off", "plan");
        Assert.Contains((await _tt.Json("plugins")).AsArray(), p => p.Str("id") == "plan" && !p!["enabled"]!.GetValue<bool>());
        await _tt.Ok("plugins", "on", "plan");
    }

    [Fact]
    public async Task An_agent_changes_only_the_hooks_it_added()
    {
        var yours = (await _tt.Json("hooks", "add", "taskCompleted", "--bash", "true")).Str("id");
        var its = (await _tt.Json("hooks", "add", "noteAdded", "--bash", "true", "--as", "claude")).Str("id");

        var refused = await _tt.Run("hooks", "edit", yours, "--bash", "curl https://example.com", "--as", "claude");
        var own = await _tt.Json("hooks", "edit", its, "--bash", "false", "--as", "claude");

        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("only change hooks it added", refused.Error, StringComparison.Ordinal);
        var mine = (await _tt.Json("hooks")).AsArray().Single(h => h.Str("id") == yours);
        Assert.Equal(("on", "true"), (mine.Str("state"), mine!["hook"].Str("bash")));
        Assert.Equal("needs approval", own.Str("state"));
    }

    [Fact]
    public async Task Only_you_turn_down_a_hook_an_agent_added()
    {
        var added = await _tt.Json("hooks", "add", "taskCompleted", "--bash", "true", "--as", "claude");
        var id = added.Str("id");
        var version = added.Str("version");

        var agent = await _tt.Run("hooks", "reject", id, version, "--as", "claude");
        Assert.Equal(1, agent.ExitCode);
        Assert.Contains("Only you", agent.Error, StringComparison.Ordinal);

        await _tt.Ok("hooks", "reject", id, version);
        Assert.Empty((await _tt.Json("hooks")).AsArray());
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("rm")]
    [InlineData("remove")]
    public async Task There_is_no_delete_and_ot_says_what_to_do_instead(string command)
    {
        var id = await Add("Keep me");

        var result = await _tt.Run(command, id);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("ot archive", result.Error, StringComparison.Ordinal);
        Assert.Single((await _tt.Json("list")).AsArray());
    }
}
