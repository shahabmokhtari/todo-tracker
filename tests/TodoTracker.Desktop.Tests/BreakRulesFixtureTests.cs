using System.Text.Json;
using TodoTracker.Core;

namespace TodoTracker.Desktop.Tests;

/// <summary>The same break rules as the web app (wwwroot/js/breaks.js): both check tests/fixtures/breaks.json.</summary>
public sealed class BreakRulesFixtureTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var c in Load().EnumerateArray())
        {
            data.Add(c.GetProperty("name").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_desktop_decides_like_the_web_app(string name)
    {
        var c = Load().EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        var p = c.GetProperty("pomodoro");
        var endsAt = p.GetProperty("endsAt").ValueKind == JsonValueKind.Null ? (DateTimeOffset?)null : p.GetProperty("endsAt").GetDateTimeOffset();
        var phase = Enum.Parse<PomodoroPhase>(p.GetProperty("phase").GetString()!, ignoreCase: true);
        var settings = new PomodoroSettings(25, p.GetProperty("shortBreakMinutes").GetInt32(), p.GetProperty("longBreakMinutes").GetInt32(), p.GetProperty("focusesBeforeLongBreak").GetInt32());
        var state = new PomodoroState(phase, endsAt, TimeSpan.Zero, p.GetProperty("running").GetBoolean(), TimeSpan.Zero, p.GetProperty("completedFocusCount").GetInt32(), settings);
        var dismissed = c.GetProperty("dismissed").ValueKind == JsonValueKind.Null ? (DateTimeOffset?)null : c.GetProperty("dismissed").GetDateTimeOffset();

        var due = BreakScreenViewModel.Due(state, c.GetProperty("now").GetDateTimeOffset());
        var shown = due is { } d && d.Until != dismissed;

        var expect = c.GetProperty("expect");
        Assert.Equal(expect.GetProperty("show").GetBoolean(), shown);
        if (shown)
        {
            Assert.Equal(expect.GetProperty("until").GetDateTimeOffset(), due!.Value.Until);
            Assert.Equal(expect.GetProperty("long").GetBoolean(), due.Value.Long);
            Assert.Equal(expect.GetProperty("tip").GetString(), BreakScreenViewModel.TipFor(due.Value.Until));
        }

        if (expect.TryGetProperty("over", out var over))
        {
            DateTimeOffset? At(string key) => c.GetProperty(key).ValueKind == JsonValueKind.Null ? null : c.GetProperty(key).GetDateTimeOffset();
            Assert.Equal(over.GetBoolean(), BreakScreenViewModel.Over(state, c.GetProperty("now").GetDateTimeOffset(), At("seen"), At("dismissedOver")));
        }
    }

    private static JsonElement Load() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "breaks.json"))).RootElement;
}
