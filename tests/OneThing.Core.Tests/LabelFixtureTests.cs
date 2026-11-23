using System.Text.Json;
using OneThing.Core;

namespace OneThing.Core.Tests;

/// <summary>Labels in Settings (rename, recolor, remove, add), and a task's icon and color: tests/fixtures/labels.json, the same in C#, Swift and Kotlin.</summary>
public sealed class LabelFixtureTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Labels_change_as_the_shared_fixture_says()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "labels.json")));
        var fixture = doc.RootElement;
        var board = BoardSerializer.Deserialize(fixture.GetProperty("board").GetRawText());

        foreach (var step in fixture.GetProperty("steps").EnumerateArray())
        {
            var label = step.TryGetProperty("label", out var l) ? l.GetString()! : string.Empty;
            var color = step.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            Action change = step.GetProperty("do").GetString() switch
            {
                "update" => () => board.UpdateLabel(label, step.GetProperty("name").GetString()!, color!, Actor.User, T0),
                "define" => () => board.DefineLabel(label, color, Actor.User, T0),
                "remove" => () => board.DeleteLabel(label, Actor.User, T0),
                "set" => () => board.SetLabels(Guid.Parse(step.GetProperty("task").GetString()!), step.GetProperty("names").EnumerateArray().Select(n => n.GetString()!), Actor.User, T0),
                "icon" => () => board.Update(Guid.Parse(step.GetProperty("task").GetString()!), new TaskChanges { Icon = step.GetProperty("value").GetString() }, Actor.User, T0),
                "color" => () => board.Update(Guid.Parse(step.GetProperty("task").GetString()!), new TaskChanges { Color = step.GetProperty("value").GetString() }, Actor.User, T0),
                var other => throw new InvalidOperationException(other),
            };

            var before = Snapshot(board);
            if (step.TryGetProperty("error", out _))
            {
                Assert.ThrowsAny<Exception>(change);
                Assert.Equal(before, Snapshot(board));
                continue;
            }

            change();
            if (step.TryGetProperty("icon", out var icon))
            {
                Assert.Equal(icon.GetString(), board.Get(Guid.Parse(step.GetProperty("task").GetString()!)).Icon);
                continue;
            }

            if (step.TryGetProperty("taskColor", out var taskColor))
            {
                Assert.Equal(taskColor.GetString(), board.Get(Guid.Parse(step.GetProperty("task").GetString()!)).Color);
                continue;
            }

            Assert.Equal(
                step.GetProperty("labels").EnumerateArray().Select(x => $"{x.GetProperty("name").GetString()} {x.GetProperty("color").GetString()}").ToList(),
                board.Labels.Select(x => $"{x.Name} {x.Color}").ToList());
            foreach (var task in step.GetProperty("tasks").EnumerateObject())
            {
                Assert.Equal(task.Value.EnumerateArray().Select(x => x.GetString()!).ToList(), board.Get(Guid.Parse(task.Name)).Labels.ToList());
            }
        }
    }

    private static string Snapshot(TaskBoard board) =>
        string.Join(";", board.Labels.Select(x => $"{x.Name} {x.Color}")) + "|" + string.Join(";", board.AllItems().Select(i => $"{string.Join(",", i.Labels)} {i.Icon} {i.Color}"));
}
