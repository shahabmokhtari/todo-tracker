using System.Globalization;
using System.Text.Json;
using TodoTracker.Core;

namespace TodoTracker.Core.Tests;

/// <summary>tests/fixtures/snooze.json: the same snooze choices and wording in C# and Swift (apple/: Snooze.swift).</summary>
public sealed class SnoozeFixtureTests
{
    private static JsonElement Fixture()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "snooze.json")));
        return doc.RootElement.Clone();
    }

    private static DateTimeOffset Time(JsonElement e, string name) => DateTimeOffset.Parse(e.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);

    private static TimeZoneInfo Zone(JsonElement e) => TimeZoneInfo.FindSystemTimeZoneById(e.GetProperty("zone").GetString()!);

    [Fact]
    public void Choices_match_the_shared_fixture()
    {
        var cases = Fixture().GetProperty("choices").EnumerateArray().ToList();
        Assert.True(cases.Count >= 4);
        foreach (var c in cases)
        {
            var name = c.GetProperty("name").GetString();
            var expected = c.GetProperty("expect").EnumerateArray()
                .Select(x => (x.GetProperty("id").GetString(), x.GetProperty("label").GetString(), Time(x, "at").UtcDateTime)).ToList();
            var actual = Snooze.Choices(Time(c, "now"), Zone(c)).Select(x => ((string?)x.Id, (string?)x.Label, x.At.UtcDateTime)).ToList();
            Assert.True(expected.SequenceEqual(actual), $"{name}: expected {string.Join(", ", expected)} but got {string.Join(", ", actual)}");
        }
    }

    [Fact]
    public void Describe_matches_the_shared_fixture()
    {
        foreach (var c in Fixture().GetProperty("describe").EnumerateArray())
        {
            Assert.Equal(c.GetProperty("text").GetString(), Snooze.Describe(Time(c, "at"), Time(c, "now"), Zone(c)));
        }
    }
}
