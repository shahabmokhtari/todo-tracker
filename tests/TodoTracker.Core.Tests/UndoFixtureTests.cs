using System.Globalization;
using System.Text.Json;
using TodoTracker.Core;

namespace TodoTracker.Core.Tests;

/// <summary>tests/fixtures/undo.json: "not done after all" on a repeating task, the same in C#, Swift and Kotlin.</summary>
public sealed class UndoFixtureTests
{
    private static JsonElement Fixture()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "undo.json")));
        return doc.RootElement.Clone();
    }

    private static DateTimeOffset Time(JsonElement e, string name) => DateTimeOffset.Parse(e.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);

    private static DateTimeOffset? OptionalTime(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? DateTimeOffset.Parse(v.GetString()!, CultureInfo.InvariantCulture) : null;

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var c in Fixture().GetProperty("cases").EnumerateArray())
        {
            data.Add(c.GetProperty("name").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Undo_works_like_the_shared_fixture(string name)
    {
        var c = Fixture().GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        var board = new TaskBoard { TimeZone = TimeZoneInfo.FindSystemTimeZoneById(c.GetProperty("zone").GetString()!) };
        var tasks = new Dictionary<string, WorkItem>();
        var reminders = new Dictionary<string, Reminder>();
        var step = 0;
        foreach (var s in c.GetProperty("steps").EnumerateArray())
        {
            step++;
            var at = Time(s, "at");
            WorkItem Task() => tasks[s.GetProperty("task").GetString()!];
            switch (s.GetProperty("do").GetString())
            {
                case "add":
                    tasks[s.GetProperty("key").GetString()!] = board.AddTask(
                        new NewTask(s.GetProperty("title").GetString()!)
                        {
                            ParentId = s.TryGetProperty("parent", out var parent) ? tasks[parent.GetString()!].Id : null,
                            Deadline = OptionalTime(s, "due"),
                            Repeat = s.TryGetProperty("repeat", out var rule) ? rule.GetString() : null,
                            Sequential = s.TryGetProperty("inOrder", out var inOrder) && inOrder.GetBoolean(),
                        },
                        Actor.User,
                        at);
                    break;
                case "complete":
                    board.Complete(Task().Id, Actor.User, at);
                    break;
                case "reopen":
                    board.Reopen(Task().Id, Actor.User, at);
                    break;
                case "archive":
                    board.Archive(Task().Id, Actor.User, at);
                    break;
                case "remind":
                    reminders[s.GetProperty("key").GetString()!] = board.AddReminder(Task().Id, Time(s, "due"), s.GetProperty("message").GetString(), Actor.User, at);
                    break;
                case "check":
                    Check(board, s, at, tasks, reminders, $"{name}, step {step}");
                    break;
                default:
                    Assert.Fail($"{name}: unknown step {s.GetProperty("do")}");
                    break;
            }
        }
    }

    private static void Check(TaskBoard board, JsonElement s, DateTimeOffset at, Dictionary<string, WorkItem> tasks, Dictionary<string, Reminder> reminders, string where)
    {
        if (s.TryGetProperty("tasks", out var expected))
        {
            foreach (var t in expected.EnumerateObject())
            {
                var item = tasks[t.Name];
                var e = t.Value;
                var what = $"{where}: {t.Name}";
                if (e.TryGetProperty("done", out var done))
                {
                    Assert.True(done.GetBoolean() == item.IsDone, $"{what} done should be {done}");
                }

                if (e.TryGetProperty("doneAt", out _))
                {
                    Assert.True(OptionalTime(e, "doneAt") == item.CompletedAt, $"{what} done at {OptionalTime(e, "doneAt")} but was {item.CompletedAt}");
                }

                if (e.TryGetProperty("due", out _))
                {
                    Assert.True(OptionalTime(e, "due") == item.Deadline, $"{what} due {OptionalTime(e, "due")} but was {item.Deadline}");
                }

                if (e.TryGetProperty("scheduled", out _))
                {
                    Assert.True(OptionalTime(e, "scheduled") == item.NextActionAt, $"{what} scheduled {OptionalTime(e, "scheduled")} but was {item.NextActionAt}");
                }

                if (e.TryGetProperty("rule", out var rule))
                {
                    Assert.True(rule.GetString() == item.Repeat?.ToString(), $"{what} rule {rule} but was {item.Repeat}");
                }

                if (e.TryGetProperty("archived", out var archived))
                {
                    Assert.True(archived.GetBoolean() == (item.ArchivedAt is not null), $"{what} archived should be {archived}");
                }
            }
        }

        if (s.TryGetProperty("reminders", out var expectedReminders))
        {
            foreach (var r in expectedReminders.EnumerateObject())
            {
                Assert.True(r.Value.GetProperty("pending").GetBoolean() == reminders[r.Name].IsPending, $"{where}: reminder {r.Name} pending should be {r.Value.GetProperty("pending")}");
            }
        }

        if (s.TryGetProperty("doNow", out var doNow))
        {
            var keys = tasks.ToDictionary(kv => kv.Value.Id, kv => kv.Key);
            var actual = Agenda.Build(board, at).Now.Select(e => keys.GetValueOrDefault(e.Item.Id, "?")).ToList();
            Assert.Equal(doNow.EnumerateArray().Select(x => x.GetString()!).ToList(), actual);
        }
    }
}
