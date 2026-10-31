using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TodoTracker.Core;

namespace TodoTracker.Core.Tests;

/// <summary>
/// tests/fixtures/plan.json: what the Plan view shows for some days (blocks, deadlines, not planned, later, energy) and
/// what planning a task does, from a board as the apps export it. The Swift port (PlanFixtureTests) reads the same file.
/// Regenerate: set TT_WRITE_FIXTURES=1 and run this test.
/// </summary>
public sealed class PlanFixtureTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

    // Wednesday 28 October 2026, 11:15 in Amsterdam (winter time since Sunday).
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-28T10:15:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void The_plan_fixture_is_what_the_core_shows_and_does()
    {
        if (Environment.GetEnvironmentVariable("TT_WRITE_FIXTURES") == "1")
        {
            var built = BoardSerializer.Serialize(Build());
            File.WriteAllText(Path.Combine(FixtureFiles.Source(), "plan.json"), Fixture(BoardSerializer.Deserialize(built), JsonNode.Parse(built)!).ToJsonString(FixtureFiles.Pretty).ReplaceLineEndings("\n") + "\n");
            return;
        }

        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "plan.json")))!;
        var board = BoardSerializer.Deserialize(fixture["board"]!.ToJsonString());
        var again = Fixture(board, fixture["board"]!.DeepClone());
        Assert.True(JsonNode.DeepEquals(fixture, again), "The plan rules changed: regenerate tests/fixtures/plan.json (TT_WRITE_FIXTURES=1) and make Swift and Kotlin agree.");
    }

    private static JsonObject Fixture(TaskBoard board, JsonNode boardJson)
    {
        var team = board.Groups.Single(g => g.Name == "Launch team").Id;
        var cases = new JsonArray(
            Case(board, new DateOnly(2026, 10, 26), 7, null),
            Case(board, new DateOnly(2026, 10, 26), 7, team),
            Case(board, new DateOnly(2026, 10, 30), 1, null),
            Case(board, new DateOnly(2026, 10, 31), 1, null));
        return new JsonObject
        {
            ["about"] = "The Plan view: blocks overlapping the days shown (in the zone), deadlines in them, the tasks to do not planned yet (no block, or its block is over; Do now order), the ones put off (Later), and each day's energy. Then planning: a block is 5 minutes to 12 hours, 30 is the default (kept as none), no length keeps what it had, a done task can't be planned. Checked by the C# core (PlanFixtureTests) and Swift (PlanFixtureTests).",
            ["zone"] = "Europe/Amsterdam",
            ["now"] = Instant(Now),
            ["board"] = boardJson,
            ["focus"] = Agenda.Build(board, Now).Focus?.Item.Id.ToString(),
            ["cases"] = cases,
            ["plan"] = Operations(BoardSerializer.Deserialize(boardJson.ToJsonString())),
        };
    }

    private static JsonObject Case(TaskBoard board, DateOnly from, int days, Guid? group)
    {
        var plan = PlanDays.Build(board, from, days, Zone, Now, group);
        var energy = new JsonObject();
        foreach (var (day, value) in plan.Energy)
        {
            energy[Day(day)] = value;
        }

        return new JsonObject
        {
            ["from"] = Day(from),
            ["days"] = days,
            ["group"] = group?.ToString(),
            ["blocks"] = new JsonArray([.. plan.Blocks.Select(i => (JsonNode)new JsonObject { ["id"] = i.Id.ToString(), ["start"] = Instant(i.PlannedAt!.Value), ["minutes"] = (int)i.PlannedLength.TotalMinutes, ["done"] = i.IsDone })]),
            ["deadlines"] = new JsonArray([.. plan.Deadlines.Select(i => (JsonNode)i.Id.ToString())]),
            ["unplanned"] = new JsonArray([.. plan.Unplanned.Select(e => (JsonNode)e.Item.Id.ToString())]),
            ["later"] = new JsonArray([.. plan.Later.Select(e => (JsonNode)new JsonObject { ["id"] = e.Item.Id.ToString(), ["wakeAt"] = e.WakeAt is { } w ? Instant(w) : null, ["waitingFor"] = e.WaitingFor?.Id.ToString() })]),
            ["energy"] = energy,
        };
    }

    /// <summary>Planning one task, step by step: what it then has, or why it was refused.</summary>
    private static JsonArray Operations(TaskBoard board)
    {
        board.TimeZone = Zone;
        var mail = Find(board, "Answer mail");
        var done = Find(board, "Done block");
        var at = Now.AddHours(3);
        var steps = new (Guid Id, DateTimeOffset? Start, int? Minutes)[]
        {
            (mail.Id, at, 2),
            (mail.Id, at, 721),
            (mail.Id, at, 45),
            (mail.Id, at.AddDays(1), null),
            (mail.Id, at, 30),
            (mail.Id, at, 720),
            (mail.Id, null, null),
            (mail.Id, at, null),
            (done.Id, at, 30),
        };
        var result = new JsonArray();
        foreach (var (id, start, minutes) in steps)
        {
            var step = new JsonObject { ["id"] = id.ToString(), ["start"] = start is { } s ? Instant(s) : null, ["minutes"] = minutes };
            try
            {
                if (start is { } s2)
                {
                    board.Plan(id, s2, minutes, Actor.User, Now);
                }
                else
                {
                    board.Unplan(id, Actor.User, Now);
                }

                var item = board.Get(id);
                step["plannedAt"] = item.PlannedAt is { } p ? Instant(p) : null;
                step["plannedMinutes"] = item.PlannedMinutes;
            }
            catch (ArgumentOutOfRangeException)
            {
                step["error"] = "length";
            }
            catch (InvalidOperationException)
            {
                step["error"] = "done";
            }

            result.Add(step);
        }

        return result;
    }

    private static TaskBoard Build()
    {
        var board = new TaskBoard { TimeZone = Zone };
        var user = Actor.User;
        var t0 = Now.AddDays(-5);
        var team = board.AddGroup("Launch team", "#3b82f6", user, t0);
        WorkItem Add(string title, Priority priority = Priority.Normal, Guid? group = null, DateTimeOffset? deadline = null, int? energy = null) =>
            board.AddTask(new NewTask(title) { Priority = priority, GroupId = group, Deadline = deadline, Energy = energy }, user, t0);

        var mail = Add("Answer mail", Priority.High);
        var report = Add("Write the report", Priority.Low, energy: 2);
        board.Plan(report.Id, Now.AddMinutes(-15), 60, user, t0); // on now: the thing to do
        var deep = Add("Deep work", energy: 3);
        board.Plan(deep.Id, DateTimeOffset.Parse("2026-10-29T08:00:00Z", CultureInfo.InvariantCulture), 120, user, t0);
        var late = Add("Late call");
        board.Plan(late.Id, DateTimeOffset.Parse("2026-10-30T22:30:00Z", CultureInfo.InvariantCulture), 90, user, t0); // Fri 23:30 to Sat 01:00
        var old = Add("Old block");
        board.Plan(old.Id, DateTimeOffset.Parse("2026-10-26T08:00:00Z", CultureInfo.InvariantCulture), null, user, t0); // over: planned again from the list
        var doneBlock = Add("Done block");
        board.Plan(doneBlock.Id, DateTimeOffset.Parse("2026-10-27T13:00:00Z", CultureInfo.InvariantCulture), 45, user, t0);
        board.Complete(doneBlock.Id, user, DateTimeOffset.Parse("2026-10-27T14:00:00Z", CultureInfo.InvariantCulture));
        var snoozed = Add("Call the bank");
        board.ScheduleNextAction(snoozed.Id, DateTimeOffset.Parse("2026-10-29T09:00:00Z", CultureInfo.InvariantCulture), user, t0);
        var waiting = Add("Send the summary");
        board.WaitFor(waiting.Id, mail.Id, user, t0);
        Add("Ship it", Priority.High, team.Id, DateTimeOffset.Parse("2026-10-30T15:00:00Z", CultureInfo.InvariantCulture));
        var teamBlock = Add("Team sync", group: team.Id);
        board.Plan(teamBlock.Id, DateTimeOffset.Parse("2026-10-28T12:00:00Z", CultureInfo.InvariantCulture), 45, user, t0);
        var nextWeek = Add("Next week");
        board.Plan(nextWeek.Id, DateTimeOffset.Parse("2026-11-02T09:00:00Z", CultureInfo.InvariantCulture), 30, user, t0);
        var archived = Add("Put away");
        board.Plan(archived.Id, DateTimeOffset.Parse("2026-10-27T08:00:00Z", CultureInfo.InvariantCulture), 60, user, t0);
        board.Complete(archived.Id, user, t0);
        board.Archive(archived.Id, user, t0);
        return board;
    }

    private static WorkItem Find(TaskBoard board, string title) => board.AllItems().Single(i => i.Title == title);

    private static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static string Instant(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>Where the shared fixtures are written from (the source tree) and how.</summary>
internal static class FixtureFiles
{
    public static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Source()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "fixtures");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("tests/fixtures");
    }
}
