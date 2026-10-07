using Microsoft.Extensions.Time.Testing;
using TodoTracker.Core;

namespace TodoTracker.Desktop.Tests;

/// <summary>Timing a task from the sidebar (the same single timer the web app and the CLI use).</summary>
public sealed class SidebarTimerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 14, 30, 0, TimeSpan.Zero);
    private readonly InMemoryBoardStore _store = new();
    private readonly FakeTimeProvider _time = new(T0);
    private readonly SidebarViewModel _vm;

    public SidebarTimerTests()
    {
        _vm = new SidebarViewModel(_store, _time, new FakeShell(), new SidebarOptions("http://127.0.0.1:5317", path => $"launch:{path}", "{}", TimeZoneInfo.Utc, "secret-token"));
    }

    public void Dispose()
    {
        _vm.Dispose();
        _store.Dispose();
    }

    [Fact]
    public async Task A_card_starts_the_timer_which_ticks_and_stops_from_the_same_button()
    {
        var task = await _store.UpdateAsync(b => b.AddTask(new NewTask("Write report"), Actor.User, T0));
        await _vm.RefreshAsync();

        await _vm.ToggleTimerCommand.ExecuteAsync(_vm.Focus);

        Assert.True(_vm.Timer.IsRunning);
        Assert.Equal("Write report", _vm.Timer.Title);
        Assert.Equal("0:00", _vm.Timer.TimeText);
        Assert.True(_vm.Focus!.IsTiming);
        _time.Advance(TimeSpan.FromSeconds(65));
        Assert.Equal("1:05", _vm.Timer.TimeText);

        await _vm.ToggleTimerCommand.ExecuteAsync(_vm.Focus);

        Assert.False(_vm.Timer.IsRunning);
        Assert.False(_vm.Focus!.IsTiming);
        var entry = Assert.Single(await _store.ReadAsync(b => b.Find(task.Id)!.TimeEntries.ToList()));
        Assert.Equal(TimeSpan.FromSeconds(65), entry.End - entry.Start);
    }

    [Fact]
    public async Task Starting_another_card_moves_the_timer_and_the_strip_stops_it()
    {
        await _store.UpdateAsync(b => b.AddTask(new NewTask("A"), Actor.User, T0));
        await _store.UpdateAsync(b => b.AddTask(new NewTask("B"), Actor.User, T0));
        await _vm.RefreshAsync();
        var b = _vm.Now.Single(c => c.Title == "B");

        await _vm.ToggleTimerCommand.ExecuteAsync(_vm.Focus);
        await _vm.ToggleTimerCommand.ExecuteAsync(b);

        Assert.Equal("B", _vm.Timer.Title);
        Assert.False(_vm.Focus!.IsTiming);
        Assert.True(b.IsTiming);

        await _vm.StopTimerCommand.ExecuteAsync(null);
        Assert.False(_vm.Timer.IsRunning);
    }

    [Fact]
    public async Task A_timer_started_elsewhere_shows_after_a_refresh_with_hours_when_long()
    {
        var task = await _store.UpdateAsync(b => b.AddTask(new NewTask("Long haul"), Actor.User, T0));
        await _store.UpdateAsync(b => b.StartTimer(task.Id, Actor.User, T0));
        _time.Advance(TimeSpan.FromMinutes(75));

        await _vm.RefreshAsync();

        Assert.True(_vm.Timer.IsRunning);
        Assert.Equal("1:15:00", _vm.Timer.TimeText);
    }
}
