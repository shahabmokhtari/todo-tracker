using System.Globalization;
using System.Text.Json.Nodes;
using TodoTracker.Core;

namespace TodoTracker.Core.Tests;

/// <summary>
/// tests/fixtures/reports.json: a board with tracked time (across midnight, the change to winter time, a running timer,
/// a focus session, an archived task) and the reports for some ranges: totals, per day, per group, top tasks, the
/// weekday × hour grid and the timeline. The Swift port (ReportFixtureTests) computes the same numbers from the same file.
/// Regenerate: set TT_WRITE_FIXTURES=1 and run this test.
/// </summary>
public sealed class ReportFixtureTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

    // Wednesday 28 October 2026, 15:00 in Amsterdam.
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-28T14:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void The_reports_fixture_is_what_the_core_reports()
    {
        if (Environment.GetEnvironmentVariable("TT_WRITE_FIXTURES") == "1")
        {
            var built = BoardSerializer.Serialize(Build());
            File.WriteAllText(Path.Combine(FixtureFiles.Source(), "reports.json"), Fixture(BoardSerializer.Deserialize(built), JsonNode.Parse(built)!).ToJsonString(FixtureFiles.Pretty).ReplaceLineEndings("\n") + "\n");
            return;
        }

        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "reports.json")))!;
        var board = BoardSerializer.Deserialize(fixture["board"]!.ToJsonString());
        var again = Fixture(board, fixture["board"]!.DeepClone());
        Assert.True(JsonNode.DeepEquals(fixture, again), "The report rules changed: regenerate tests/fixtures/reports.json (TT_WRITE_FIXTURES=1) and make Swift and Kotlin agree.");
    }

    private static JsonObject Fixture(TaskBoard board, JsonNode boardJson)
    {
        var team = board.Groups.Single(g => g.Name == "Launch team").Id;
        return new JsonObject
        {
            ["about"] = "Reports between two local dates (inclusive) in the zone: time is the tracked time entries (a running timer counts until now), cut to the range and split at local midnights and hours; archived tasks count; a task's subtasks count for it. Seconds are whole (summed exactly, cut at the end). Groups: most time first; tasks: the top 15; hourGrid: [weekday 0 = Sunday][hour]; timeline: the 40 most worked rows, by when their work (else finish or creation) began. Checked by the C# core (ReportFixtureTests) and Swift (ReportFixtureTests).",
            ["zone"] = "Europe/Amsterdam",
            ["now"] = PlanFixtureTests.Instant(Now),
            ["board"] = boardJson,
            ["cases"] = new JsonArray(
                Case(board, new DateOnly(2026, 10, 19), new DateOnly(2026, 11, 1), null),
                Case(board, new DateOnly(2026, 10, 19), new DateOnly(2026, 11, 1), team),
                Case(board, new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 25), null),
                Case(board, new DateOnly(2026, 10, 22), new DateOnly(2026, 10, 28), null)),
        };
    }

    private static JsonObject Case(TaskBoard board, DateOnly from, DateOnly to, Guid? group)
    {
        var r = TimeReport.Build(board, from, to, Zone, Now, group);
        static string? At(DateTimeOffset? at) => at is { } a ? PlanFixtureTests.Instant(a) : null;
        return new JsonObject
        {
            ["from"] = Day(from),
            ["to"] = Day(to),
            ["group"] = group?.ToString(),
            ["expected"] = new JsonObject
            {
                ["trackedSeconds"] = r.TrackedSeconds,
                ["focusSessions"] = r.FocusSessions,
                ["completed"] = r.Completed,
                ["created"] = r.Created,
                ["activeDays"] = r.ActiveDays,
                ["longestStreak"] = r.LongestStreak,
                ["days"] = new JsonArray([.. r.Days.Select(d => (JsonNode)new JsonObject
                {
                    ["date"] = Day(d.Date), ["trackedSeconds"] = d.TrackedSeconds, ["focusSeconds"] = d.FocusSeconds, ["completed"] = d.Completed, ["created"] = d.Created,
                })]),
                ["groups"] = new JsonArray([.. r.Groups.Select(g => (JsonNode)new JsonObject { ["groupId"] = g.GroupId.ToString(), ["name"] = g.Name, ["color"] = g.Color, ["trackedSeconds"] = g.TrackedSeconds })]),
                ["tasks"] = new JsonArray([.. r.Tasks.Select(t => (JsonNode)new JsonObject { ["id"] = t.Id.ToString(), ["title"] = t.Title, ["groupId"] = t.GroupId.ToString(), ["done"] = t.Done, ["trackedSeconds"] = t.TrackedSeconds })]),
                ["hourGrid"] = new JsonArray([.. r.HourGrid.Select(h => (JsonNode)new JsonArray([.. h.Select(s => (JsonNode)s)]))]),
                ["timeline"] = new JsonArray([.. r.Timeline.Select(row => (JsonNode)new JsonObject
                {
                    ["id"] = row.Id.ToString(),
                    ["title"] = row.Title,
                    ["groupId"] = row.GroupId.ToString(),
                    ["createdAt"] = At(row.CreatedAt),
                    ["completedAt"] = At(row.CompletedAt),
                    ["deadline"] = At(row.Deadline),
                    ["work"] = new JsonArray([.. row.Work.Select(w => (JsonNode)new JsonObject { ["start"] = At(w.Start), ["end"] = At(w.End), ["source"] = w.Source == TimeSource.Focus ? "focus" : "manual" })]),
                })]),
            },
        };
    }

    private static TaskBoard Build()
    {
        var board = new TaskBoard { TimeZone = Zone };
        var user = Actor.User;
        static DateTimeOffset T(string utc) => DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture);
        var team = board.AddGroup("Launch team", "#3b82f6", user, T("2026-10-01T08:00:00Z"));
        WorkItem Add(string title, string createdAt, Guid? group = null, Guid? parent = null, DateTimeOffset? deadline = null) =>
            board.AddTask(new NewTask(title) { GroupId = parent is null ? group : null, ParentId = parent, Deadline = deadline }, user, T(createdAt));

        // A task and its subtask: the subtask's time counts for the task; an evening that runs past midnight.
        var report = Add("Write the report", "2026-10-15T08:00:00Z");
        var draft = Add("Draft", "2026-10-19T07:00:00Z", parent: report.Id);
        board.AddTime(report.Id, T("2026-10-19T07:00:00Z"), T("2026-10-19T08:30:00Z"), user, Now);
        board.AddTime(draft.Id, T("2026-10-20T21:30:00Z"), T("2026-10-20T23:45:00Z"), user, Now);
        board.Complete(draft.Id, user, T("2026-10-21T10:00:00Z"));
        board.Complete(report.Id, user, T("2026-10-21T13:00:00Z"));

        // In another group: a focus session, and a night across the change to winter time (01:30 to 03:30, three hours).
        var launch = Add("Launch the site", "2026-10-10T08:00:00Z", team.Id, deadline: T("2026-10-30T16:00:00Z"));
        board.StartFocus(launch.Id, user, T("2026-10-22T08:00:00Z"));
        board.TickPomodoro(T("2026-10-22T08:30:00Z"));
        board.AddTime(launch.Id, T("2026-10-24T23:30:00Z"), T("2026-10-25T02:30:00Z"), user, Now);

        // Started before the range: only what's in it counts.
        var early = Add("Inbox zero", "2026-10-01T08:00:00Z");
        board.AddTime(early.Id, T("2026-10-18T21:00:00Z"), T("2026-10-18T23:00:00Z"), user, Now);

        // Done and put away: the work still happened.
        var old = Add("Tax forms", "2026-10-05T08:00:00Z");
        board.AddTime(old.Id, T("2026-10-23T12:00:00Z"), T("2026-10-23T12:20:00Z"), user, Now);
        board.Complete(old.Id, user, T("2026-10-23T12:20:00Z"));
        board.Archive(old.Id, user, T("2026-10-23T13:00:00Z"));

        // Only created in the range, and one never touched in it.
        Add("Plan the offsite", "2026-10-27T09:00:00Z", team.Id, deadline: T("2026-11-05T09:00:00Z"));
        Add("Someday", "2026-09-01T08:00:00Z", team.Id);

        // Still running: counts until now.
        var call = Add("Prep the call", "2026-10-28T12:00:00Z");
        board.StartTimer(call.Id, user, T("2026-10-28T13:20:00Z"));
        return board;
    }

    private static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
