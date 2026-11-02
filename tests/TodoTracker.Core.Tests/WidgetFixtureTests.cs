using System.Globalization;
using System.Text.Json.Nodes;
using TodoTracker.Core;
using TodoTracker.Core.Tests.Fixtures;

namespace TodoTracker.Core.Tests;

/// <summary>
/// tests/fixtures/widget.json: what the home-screen widgets show over the next day, from a board as the apps export it
/// (the snapshot the app writes, in the format the widgets read) and what they show at some moments (with a task just
/// marked done from the widget left out). The Swift and Kotlin ports (WidgetFixtureTests) read the same file.
/// Regenerate: set TT_WRITE_FIXTURES=1 and run this test.
/// </summary>
public sealed class WidgetFixtureTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

    // Monday 2 November 2026, 9:30 in Amsterdam.
    private static readonly DateTimeOffset Now = At("2026-11-02T08:30:00Z");

    [Fact]
    public void The_widget_fixture_is_what_the_core_shows()
    {
        if (FixtureFile.Writing)
        {
            var built = FixtureFile.FixIds(BoardSerializer.Serialize(Build()), 0x100);
            File.WriteAllText(Path.Combine(FixtureFiles.Source(), "widget.json"), Fixture(BoardSerializer.Deserialize(built), JsonNode.Parse(built)!).ToJsonString(FixtureFiles.Pretty).ReplaceLineEndings("\n") + "\n");
            return;
        }

        var fixture = FixtureFile.Read("widget.json");
        var board = BoardSerializer.Deserialize(fixture["board"]!.ToJsonString());
        var again = Fixture(board, fixture["board"]!.DeepClone());
        Assert.True(JsonNode.DeepEquals(fixture, again), "The widget rules changed: regenerate tests/fixtures/widget.json (TT_WRITE_FIXTURES=1) and make Swift and Kotlin agree.");
    }

    [Fact]
    public void The_widget_shows_the_thing_to_do_now_and_what_comes_back_later_today()
    {
        var board = Build();
        var snapshot = WidgetSnapshot.Build(board, Now, Zone);
        var view = snapshot.ViewAt(Now);

        Assert.Equal("Answer mail", view.Focus?.Title);
        Assert.Equal("Work", view.Focus?.Group);
        Assert.Equal(3, view.Next.Count);
        Assert.Equal(["Stretch", "Call the bank", "Move house"], view.Later.Select(l => l.Item.Title));
        Assert.Equal(view.NowCount - 4 + 3, view.More);
        // Nothing about a task but its title and group goes into the snapshot.
        Assert.DoesNotContain(snapshot.Items, i => i.Title.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public void A_snoozed_task_comes_back_in_the_widget_when_its_snooze_ends()
    {
        var snapshot = WidgetSnapshot.Build(Build(), Now, Zone);

        Assert.DoesNotContain(snapshot.ViewAt(Now).Today, i => i.Title == "Call the bank");
        Assert.Equal("Call the bank", snapshot.ViewAt(At("2026-11-02T13:00:00Z")).Focus?.Title);
        Assert.Contains(snapshot.Frames, f => f.At == At("2026-11-02T13:00:00Z"));
    }

    [Fact]
    public void A_task_marked_done_from_the_widget_leaves_it_at_once()
    {
        var snapshot = WidgetSnapshot.Build(Build(), Now, Zone);
        var mail = snapshot.Items.Single(i => i.Title == "Answer mail").Id;

        var view = snapshot.ViewAt(Now, new HashSet<Guid> { mail });

        Assert.NotEqual(mail, view.Focus?.Id);
        Assert.Equal(snapshot.ViewAt(Now).NowCount - 1, view.NowCount);
    }

    [Fact]
    public void An_empty_board_shows_nothing_to_do()
    {
        var board = new TaskBoard();
        var view = WidgetSnapshot.Build(board, Now, Zone).ViewAt(Now);

        Assert.Null(view.Focus);
        Assert.Equal(0, view.More);
    }

    private static JsonObject Fixture(TaskBoard board, JsonNode boardJson)
    {
        var snapshot = WidgetSnapshot.Build(board, Now, Zone);
        var mail = snapshot.Items.Single(i => i.Title == "Answer mail").Id;
        var views = new JsonArray(
            View(snapshot, At("2026-11-02T07:00:00Z"), []),
            View(snapshot, Now, []),
            View(snapshot, Now, [mail]),
            View(snapshot, At("2026-11-02T09:15:00Z"), []),
            View(snapshot, At("2026-11-02T10:10:00Z"), []),
            View(snapshot, At("2026-11-02T13:05:00Z"), [mail]),
            View(snapshot, At("2026-11-02T15:30:00Z"), []),
            View(snapshot, At("2026-11-02T23:30:00Z"), []),
            View(snapshot, At("2026-11-03T09:00:00Z"), []));
        return new JsonObject
        {
            ["about"] = "The home-screen widgets: the snapshot the app writes (its tasks' titles and groups, and what the Do now list and Later today are at every moment they change in the next 24 hours: a snooze ends, a reminder or a planned block starts or ends, a deadline passes, midnight; the agenda's own rules at each), then what a widget shows at some moments, leaving out tasks just marked done from it. Checked by the C# core, Swift and Kotlin (WidgetFixtureTests).",
            ["zone"] = "Europe/Amsterdam",
            ["now"] = Instant(Now),
            ["board"] = boardJson,
            ["snapshot"] = Snapshot(snapshot),
            ["views"] = views,
        };
    }

    /// <summary>The snapshot as the app writes it for the widgets (Swift and Kotlin read and write this format).</summary>
    private static JsonObject Snapshot(WidgetSnapshot s) => new()
    {
        ["at"] = Instant(s.At),
        ["items"] = new JsonArray([.. s.Items.Select(i => (JsonNode)new JsonObject
        {
            ["id"] = i.Id.ToString(),
            ["title"] = i.Title,
            ["group"] = i.Group,
            ["color"] = i.Color,
            ["openSubtasks"] = i.OpenSubtasks,
        })]),
        ["frames"] = new JsonArray([.. s.Frames.Select(f => (JsonNode)new JsonObject
        {
            ["at"] = Instant(f.At),
            ["now"] = Ids(f.Now),
            ["nowCount"] = f.NowCount,
            ["later"] = new JsonArray([.. f.Later.Select(l => (JsonNode)new JsonObject { ["id"] = l.Id.ToString(), ["at"] = Instant(l.At) })]),
            ["laterCount"] = f.LaterCount,
        })]),
    };

    private static JsonObject View(WidgetSnapshot snapshot, DateTimeOffset at, Guid[] hidden)
    {
        var view = snapshot.ViewAt(at, hidden.ToHashSet());
        return new JsonObject
        {
            ["at"] = Instant(at),
            ["hidden"] = Ids(hidden),
            ["focus"] = view.Focus?.Id.ToString(),
            ["next"] = Ids(view.Next.Select(i => i.Id)),
            ["more"] = view.More,
            ["today"] = Ids(view.Today.Select(i => i.Id)),
            ["later"] = new JsonArray([.. view.Later.Select(l => (JsonNode)new JsonObject { ["id"] = l.Item.Id.ToString(), ["at"] = Instant(l.At) })]),
            ["nowCount"] = view.NowCount,
            ["laterCount"] = view.LaterCount,
        };
    }

    private static JsonArray Ids(IEnumerable<Guid> ids) => new([.. ids.Select(id => (JsonNode)id.ToString())]);

    private static TaskBoard Build()
    {
        var board = new TaskBoard { TimeZone = Zone };
        var user = Actor.User;
        var t0 = Now.AddDays(-2);
        var work = board.Groups.Single(g => g.Name == "Work").Id;
        var team = board.AddGroup("Launch team", "#f97316", user, t0);
        var minute = 0;
        WorkItem Add(string title, Priority priority = Priority.Normal, Guid? group = null, DateTimeOffset? deadline = null, Guid? parent = null, string? details = null) =>
            board.AddTask(new NewTask(title) { Priority = priority, GroupId = parent is null ? group ?? work : null, ParentId = parent, Deadline = deadline, Details = details }, user, t0.AddMinutes(minute++));

        var mail = Add("Answer mail", Priority.High, details: "A secret only the app shows");
        Add("Write the report", deadline: At("2026-11-02T12:00:00Z"));
        var bank = Add("Call the bank");
        board.ScheduleNextAction(bank.Id, At("2026-11-02T13:00:00Z"), user, t0, notify: true);
        var plants = Add("Water the plants");
        board.ScheduleNextAction(plants.Id, At("2026-11-03T07:00:00Z"), user, t0);
        var stretch = Add("Stretch", Priority.Low);
        board.ScheduleNextAction(stretch.Id, At("2026-11-02T09:00:00Z"), user, t0);
        var sync = Add("Team sync", group: team.Id);
        board.Plan(sync.Id, At("2026-11-02T10:00:00Z"), 45, user, t0);
        var summary = Add("Send the summary");
        board.WaitFor(summary.Id, mail.Id, user, t0);
        var move = Add("Move house");
        board.ScheduleNextAction(move.Id, At("2026-11-02T15:00:00Z"), user, t0, notify: true);
        Add("Pack books", parent: move.Id);
        Add("Book the van", parent: move.Id);
        for (var i = 1; i <= 5; i++)
        {
            Add($"Small thing {i}", Priority.Low, board.Groups.Single(g => g.Name == "Personal").Id);
        }

        var old = Add("Old thing");
        board.Complete(old.Id, user, t0);
        var later = Add("Next week");
        board.ScheduleNextAction(later.Id, At("2026-11-09T08:00:00Z"), user, t0);
        return board;
    }

    private static DateTimeOffset At(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);

    private static string Instant(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
