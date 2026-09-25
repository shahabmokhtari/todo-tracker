using System.Text.Json;
using TodoTracker.Core;

namespace TodoTracker.Core.Tests;

/// <summary>
/// Shared cross-platform scenarios. The Swift core (apple/) runs the same fixture files,
/// so C# and Swift agenda behavior cannot silently drift apart.
/// </summary>
public class SharedScenarioTests
{
    public static TheoryData<string> Scenarios()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixtures", "scenarios"), "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Scenario_matches_expected_agenda(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "scenarios", file)));
        var root = doc.RootElement;
        var now = DateTimeOffset.Parse(root.GetProperty("now").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var board = BoardSerializer.Deserialize(root.GetProperty("board").GetRawText());
        Guid? group = root.TryGetProperty("group", out var g) ? Guid.Parse(g.GetString()!) : null;
        var expect = root.GetProperty("expect");

        var dashboard = Agenda.Build(board, now, groupId: group);

        Assert.Equal(Ids(expect.GetProperty("now")), dashboard.Now.Select(e => e.Item.Id));
        Assert.Equal(Ids(expect.GetProperty("waiting")), dashboard.Waiting.Select(e => e.Item.Id));
        Assert.Equal(Ids(expect.GetProperty("attention")), dashboard.Now.Where(e => e.NeedsAttention).Select(e => e.Item.Id));
        var focus = expect.GetProperty("focus");
        Assert.Equal(focus.ValueKind == JsonValueKind.Null ? null : Guid.Parse(focus.GetString()!), dashboard.Focus?.Item.Id);
        foreach (var state in expect.GetProperty("states").EnumerateObject())
        {
            var item = board.Get(Guid.Parse(state.Name));
            Assert.Equal(state.Value.GetString(), Agenda.StateOf(item, now).ToString().ToLowerInvariant());
        }
    }

    private static List<Guid> Ids(JsonElement array) => array.EnumerateArray().Select(e => Guid.Parse(e.GetString()!)).ToList();
}
