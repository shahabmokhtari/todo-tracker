using System.Text.Json.Nodes;
using TodoTracker.Core;

namespace TodoTracker.Core.Tests;

/// <summary>
/// tests/fixtures/export.json is a board as the server's <c>/api/export</c> sends it, with everything newer versions
/// add (tags, labels, columns, archive, time, notes from Obsidian and connected apps). The Mac app reads that export
/// (apple/: BoardCodec), and its tests read this file, so the two can't drift apart unnoticed.
/// Regenerate: set TT_WRITE_FIXTURES=1 and run this test.
/// </summary>
public sealed class ExportFixtureTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-05T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "fixtures", "export.json");

    [Fact]
    public void The_export_fixture_is_exactly_what_this_version_writes()
    {
        if (Environment.GetEnvironmentVariable("TT_WRITE_FIXTURES") == "1")
        {
            File.WriteAllText(Path.Combine(SourceFixtures(), "export.json"), BoardSerializer.Serialize(Build()).ReplaceLineEndings("\n") + "\n");
            return;
        }

        var fixture = File.ReadAllText(FixturePath);

        // Read and written again: nothing lost, nothing added (a new field shows up here, so the fixture and the Mac
        // app's reader get updated with it).
        var again = BoardSerializer.Serialize(BoardSerializer.Deserialize(fixture));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(fixture), JsonNode.Parse(again)), "Regenerate tests/fixtures/export.json (TT_WRITE_FIXTURES=1).");
    }

    [Fact]
    public void The_export_fixture_covers_what_the_mac_app_must_read()
    {
        var board = BoardSerializer.Deserialize(File.ReadAllText(FixturePath));
        var all = board.AllItems().ToList();

        // Every kind of author: the Swift reader once failed on ones it didn't know.
        Assert.Equal(Enum.GetValues<ActorKind>().Order(), board.Activity.Select(a => a.Actor.Kind).Concat(all.SelectMany(i => i.Notes).Select(n => n.Author.Kind)).Distinct().Order());
        Assert.Contains(all, i => i.Tags.Contains("deep work"));
        Assert.Contains(all, i => i.Labels.Count > 0);
        Assert.Contains(all, i => i.ArchivedAt is not null);
        Assert.Contains(all, i => i.TimeEntries.Count > 0);
        Assert.Contains(all, i => i.Parent is null && i.Stage == Stage.Doing);
        Assert.Contains(all, i => i.AfterId is not null);
    }

    private static TaskBoard Build()
    {
        var board = new TaskBoard();
        var user = Actor.User;
        var work = board.AddGroup("Launch team", "#3b82f6", user, T0);
        board.DefineLabel("Urgent", "#e11d48", user, T0);
        var launch = board.AddTask(new NewTask("Launch the site") { GroupId = work.Id, Priority = Priority.High, Details = "Hero copy first.", Deadline = T0.AddDays(3), Tags = ["deep work", "q3"], Labels = ["Urgent"] }, user, T0);
        var copy = board.AddTask(new NewTask("Write the copy") { ParentId = launch.Id, Tags = ["writing"] }, new Actor(ActorKind.Agent, "copilot"), T0);
        var images = board.AddTask(new NewTask("Pick images") { ParentId = launch.Id }, new Actor(ActorKind.Browser, "edge"), T0);
        board.Complete(images.Id, user, T0.AddHours(1));
        board.AddReminder(copy.Id, T0.AddDays(1), "Send the draft", user, T0);
        board.ScheduleNextAction(launch.Id, T0.AddHours(2), new Actor(ActorKind.Teams, "Teams"), T0, notify: true);
        board.SetStage(launch.Id, Stage.Doing, user, T0);
        board.AddNote(launch.Id, "Edited in Obsidian", new Actor(ActorKind.Vault, "Obsidian"), T0.AddMinutes(5));
        board.AddNote(launch.Id, "From Notion", new Actor(ActorKind.Connector, "Notion"), T0.AddMinutes(6));
        board.AddNote(copy.Id, "Draft 1", new Actor(ActorKind.System), T0.AddMinutes(7));
        board.AddTime(copy.Id, T0, T0.AddMinutes(25), user, T0.AddMinutes(25));
        var announce = board.AddTask(new NewTask("Announce it") { GroupId = work.Id }, user, T0);
        board.WaitFor(announce.Id, launch.Id, user, T0);
        var old = board.AddTask(new NewTask("Old task") { GroupId = work.Id }, user, T0);
        board.Complete(old.Id, user, T0);
        board.Archive(old.Id, user, T0);
        return board;
    }

    private static string SourceFixtures()
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
