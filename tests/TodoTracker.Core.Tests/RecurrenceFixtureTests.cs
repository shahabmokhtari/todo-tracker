using System.Globalization;
using System.Text.Json;
using TodoTracker.Core;

namespace TodoTracker.Core.Tests;

/// <summary>tests/fixtures/repeat.json: the same repeat rules in C#, Swift (Recurrence.swift) and Kotlin (Recurrence.kt).</summary>
public sealed class RecurrenceFixtureTests
{
    private static JsonElement Fixture()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "repeat.json")));
        return doc.RootElement.Clone();
    }

    private static DateOnly Day(JsonElement e, string name) => DateOnly.ParseExact(e.GetProperty(name).GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Time(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? DateTimeOffset.Parse(v.GetString()!, CultureInfo.InvariantCulture) : null;

    [Fact]
    public void Rules_read_and_write_like_the_shared_fixture()
    {
        foreach (var c in Fixture().GetProperty("parse").EnumerateArray())
        {
            var text = c.GetProperty("text").GetString();
            var ok = Recurrence.TryParse(text, out var rule, out var problem);
            if (c.TryGetProperty("error", out _))
            {
                Assert.False(ok, $"\"{text}\" should not be understood but reads as \"{(ok ? rule : null)}\"");
                Assert.Contains("every day", problem, StringComparison.Ordinal);
                continue;
            }

            Assert.True(ok, $"\"{text}\": {problem}");
            var canonical = c.GetProperty("canonical").GetString();
            Assert.Equal(canonical, rule.ToString());
            Assert.Equal(canonical, Recurrence.Parse(canonical).ToString());
        }
    }

    [Fact]
    public void Next_days_match_the_shared_fixture()
    {
        foreach (var c in Fixture().GetProperty("next").EnumerateArray())
        {
            var rule = Recurrence.Parse(c.GetProperty("rule").GetString());
            var expected = c.GetProperty("next").ValueKind == JsonValueKind.Null ? (DateOnly?)null : Day(c, "next");
            Assert.True(expected == rule.Next(Day(c, "anchor"), Day(c, "after")), $"{rule} after {Day(c, "after")}: expected {expected} but got {rule.Next(Day(c, "anchor"), Day(c, "after"))}");
        }
    }

    [Fact]
    public void Repeating_tasks_move_on_like_the_shared_fixture()
    {
        foreach (var c in Fixture().GetProperty("roll").EnumerateArray())
        {
            var name = c.GetProperty("name").GetString();
            var board = new TaskBoard { TimeZone = TimeZoneInfo.FindSystemTimeZoneById(c.GetProperty("zone").GetString()!) };
            var now = Time(c, "now")!.Value;
            var item = board.AddTask(new NewTask("Water the plants") { Repeat = c.GetProperty("rule").GetString(), Deadline = Time(c, "due") }, Actor.User, now.AddDays(-30));
            if (Time(c, "scheduled") is { } scheduled)
            {
                board.ScheduleNextAction(item.Id, scheduled, Actor.User, now.AddDays(-30));
            }

            board.Complete(item.Id, Actor.User, now);

            var expect = c.GetProperty("expect");
            Assert.True(expect.GetProperty("done").GetBoolean() == item.IsDone, $"{name}: done should be {expect.GetProperty("done")}");
            if (item.IsDone)
            {
                continue;
            }

            Assert.True(Time(expect, "due") == item.Deadline, $"{name}: due {Time(expect, "due")} but got {item.Deadline}");
            Assert.True(Time(expect, "scheduled") == item.NextActionAt, $"{name}: scheduled {Time(expect, "scheduled")} but got {item.NextActionAt}");
            Assert.Equal(expect.GetProperty("rule").GetString(), item.Repeat?.ToString());
        }
    }
}
