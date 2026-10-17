using System.Globalization;
using System.Text.Json;
using TodoTracker.Core;
using TodoTracker.Core.Vault;

namespace TodoTracker.Core.Tests;

/// <summary>Energy per task and per day; tests/fixtures/energy.json is the same rule in C#, Swift and Kotlin.</summary>
public sealed class EnergyTests
{
    private static readonly DateTimeOffset Morning = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
    private static readonly TaskMarkdownContext Context = new(TimeZoneInfo.Utc, Morning, "Work");
    private readonly TaskBoard _board = new() { TimeZone = TimeZoneInfo.Utc };

    [Fact]
    public void A_day_takes_what_the_shared_fixture_says()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "energy.json")));
        var fixture = doc.RootElement;
        var board = BoardSerializer.Deserialize(fixture.GetProperty("board").GetRawText());
        var zone = TimeZoneInfo.FindSystemTimeZoneById(fixture.GetProperty("zone").GetString()!);

        Assert.Equal(fixture.GetProperty("perDay").GetInt32(), board.DailyEnergy);
        foreach (var day in fixture.GetProperty("days").EnumerateArray())
        {
            var date = DateOnly.ParseExact(day.GetProperty("day").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            Assert.Equal(day.GetProperty("planned").GetDouble(), Energy.PlannedOn(board, date, zone));
        }
    }

    [Fact]
    public void A_task_takes_one_to_three_an_hour_and_can_say_nothing()
    {
        var task = _board.AddTask(new NewTask("Deep work") { Energy = 3 }, Actor.User, Morning);
        Assert.Equal(3, Energy.Of(task));

        Assert.Throws<ArgumentOutOfRangeException>(() => _board.AddTask(new NewTask("Too much") { Energy = 4 }, Actor.User, Morning));
        Assert.Throws<ArgumentOutOfRangeException>(() => _board.Update(task.Id, new TaskChanges { Energy = 0 }, Actor.User, Morning));
        _board.Update(task.Id, new TaskChanges { Title = "Deeper work" }, Actor.User, Morning);
        Assert.Equal(3, task.Energy);
        _board.Update(task.Id, new TaskChanges { ClearEnergy = true }, Actor.User, Morning);
        Assert.Null(task.Energy);
        Assert.Equal(Energy.Unset, Energy.Of(task));
    }

    [Fact]
    public void A_day_has_ten_unless_said_otherwise()
    {
        Assert.Equal(Energy.DefaultPerDay, _board.DailyEnergy);
        _board.SetEnergyPerDay(6);
        Assert.Equal(6, _board.DailyEnergy);
        Assert.Throws<ArgumentOutOfRangeException>(() => _board.SetEnergyPerDay(0));
        Assert.Equal(6, BoardSerializer.Deserialize(BoardSerializer.Serialize(_board)).DailyEnergy);
        _board.SetEnergyPerDay(null);
        Assert.Equal(Energy.DefaultPerDay, _board.DailyEnergy);
    }

    [Fact]
    public void Energy_survives_the_files_and_a_wrong_value_is_left_out()
    {
        var root = _board.AddTask(new NewTask("Release") { Energy = 2 }, Actor.User, Morning);
        _board.AddTask(new NewTask("Notes") { ParentId = root.Id, Energy = 3 }, Actor.User, Morning);

        var text = TaskMarkdown.Render(root, Context);
        var md = TaskMarkdown.Parse(text, "Release", Context).Root;

        Assert.Contains("energy: 2\n", text, StringComparison.Ordinal);
        Assert.Equal(2, md.Energy);
        Assert.Equal(3, md.Children.Single().Energy);
        Assert.Null(TaskMarkdown.Parse("---\nenergy: 7\n---\n# Taxes\n", "Taxes", Context).Root.Energy);
        Assert.Equal(2, BoardSerializer.Deserialize(BoardSerializer.Serialize(_board)).Get(root.Id).Energy);
    }
}
