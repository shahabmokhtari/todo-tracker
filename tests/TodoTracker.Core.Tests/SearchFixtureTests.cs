using System.Text.Json;
using TodoTracker.Core;

namespace TodoTracker.Core.Tests;

/// <summary>tests/fixtures/search.json: the search syntax (TaskQuery here), the same in C#, Swift and Kotlin.</summary>
public sealed class SearchFixtureTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    private static JsonElement Fixture()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "search.json")));
        return doc.RootElement.Clone();
    }

    private static List<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var list) ? list.EnumerateArray().Select(x => x.GetString()!).ToList() : [];

    /// <summary>The fixture's board, and each task's key by id.</summary>
    private static (TaskBoard Board, Dictionary<Guid, string> Keys) Board(JsonElement fixture)
    {
        var board = new TaskBoard();
        Assert.Equal(Strings(fixture, "groups"), board.Groups.Select(g => g.Name).ToList());
        var tasks = new Dictionary<string, WorkItem>();
        foreach (var t in fixture.GetProperty("tasks").EnumerateArray())
        {
            var group = t.TryGetProperty("group", out var g) ? board.Groups.Single(x => x.Name == g.GetString()).Id : board.DefaultGroupId;
            var item = board.AddTask(
                new NewTask(t.GetProperty("title").GetString()!)
                {
                    ParentId = t.TryGetProperty("parent", out var parent) ? tasks[parent.GetString()!].Id : null,
                    GroupId = group,
                    Details = t.TryGetProperty("details", out var details) ? details.GetString() : null,
                    Stage = t.TryGetProperty("stage", out var stage) ? Enum.Parse<Stage>(stage.GetString()!, ignoreCase: true) : Stage.Next,
                },
                Actor.User,
                T0);
            tasks[t.GetProperty("key").GetString()!] = item;
            if (Strings(t, "tags") is { Count: > 0 } tags)
            {
                board.SetTags(item.Id, tags, Actor.User, T0);
            }

            if (Strings(t, "labels") is { Count: > 0 } labels)
            {
                board.SetLabels(item.Id, labels, Actor.User, T0);
            }

            if (t.TryGetProperty("done", out var done) && done.GetBoolean())
            {
                board.Complete(item.Id, Actor.User, T0);
            }

            if (t.TryGetProperty("archived", out var archived) && archived.GetBoolean())
            {
                board.Archive(item.Id, Actor.User, T0);
            }
        }

        return (board, tasks.ToDictionary(kv => kv.Value.Id, kv => kv.Key));
    }

    [Fact]
    public void Search_finds_what_the_shared_fixture_says()
    {
        var fixture = Fixture();
        var (board, keys) = Board(fixture);
        foreach (var q in fixture.GetProperty("queries").EnumerateArray())
        {
            var text = q.GetProperty("q").GetString();
            var found = TaskQuery.Parse(text).Apply(board).Select(i => keys[i.Id]).ToList();
            Assert.True(Strings(q, "expect").SequenceEqual(found), $"\"{text}\": expected [{string.Join(", ", Strings(q, "expect"))}] but found [{string.Join(", ", found)}]");
        }
    }
}
