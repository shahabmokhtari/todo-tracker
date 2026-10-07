using Microsoft.Extensions.Time.Testing;
using TodoTracker.Core;

namespace TodoTracker.Desktop.Tests;

/// <summary>The full-screen break the desktop app shows when a focus session ends.</summary>
public sealed class BreakScreenTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 14, 30, 0, TimeSpan.Zero);
    private readonly InMemoryBoardStore _store = new();
    private readonly FakeTimeProvider _time = new(T0);
    private readonly SidebarViewModel _vm;

    public BreakScreenTests()
    {
        _vm = new SidebarViewModel(_store, _time, new FakeShell(), new SidebarOptions("http://127.0.0.1:5317", path => $"launch:{path}", "{}", TimeZoneInfo.Utc, "secret-token"));
    }

    public void Dispose()
    {
        _vm.Dispose();
        _store.Dispose();
    }

    private async Task StartFocusAsync()
    {
        await _store.UpdateAsync(b => b.AddTask(new NewTask("Deep work"), Actor.User, T0));
        await _vm.RefreshAsync();
        await _vm.StartFocusCommand.ExecuteAsync(_vm.Focus);
    }

    [Fact]
    public async Task It_shows_the_moment_focus_ends_without_waiting_for_the_background_tick()
    {
        await StartFocusAsync();
        _time.Advance(TimeSpan.FromMinutes(24));
        Assert.False(_vm.Break.IsShown);

        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.True(_vm.Break.IsShown);
        Assert.Equal("Time for a break", _vm.Break.Title);
        Assert.Equal("5:00", _vm.Break.TimeText);
        Assert.False(string.IsNullOrWhiteSpace(_vm.Break.Tip));
        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("3:59", _vm.Break.TimeText);
    }

    [Fact]
    public async Task Taking_the_break_hides_it_for_good_even_after_the_server_switches_to_the_break()
    {
        await StartFocusAsync();
        _time.Advance(TimeSpan.FromMinutes(25));
        _vm.Break.TakeBreakCommand.Execute(null);
        Assert.False(_vm.Break.IsShown);

        await _store.UpdateAsync(b => b.TickPomodoro(_time.GetUtcNow()));
        await _vm.RefreshAsync();
        _time.Advance(TimeSpan.FromSeconds(2));

        Assert.False(_vm.Break.IsShown);
        Assert.True(_vm.Pomodoro.IsBreak);
    }

    [Fact]
    public async Task Skipping_ends_the_break_and_counts_the_session()
    {
        await StartFocusAsync();
        _time.Advance(TimeSpan.FromMinutes(25).Add(TimeSpan.FromSeconds(2)));

        await _vm.Break.SkipBreakCommand.ExecuteAsync(null);

        Assert.False(_vm.Break.IsShown);
        var (phase, count) = await _store.ReadAsync(b => (b.Pomodoro.Phase, b.Pomodoro.CompletedFocusCount));
        Assert.Equal(PomodoroPhase.Idle, phase);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task It_shows_during_a_break_and_goes_away_when_the_break_is_over()
    {
        await StartFocusAsync();
        _time.Advance(TimeSpan.FromMinutes(25));
        await _store.UpdateAsync(b => b.TickPomodoro(_time.GetUtcNow()));
        await _vm.RefreshAsync();
        Assert.True(_vm.Break.IsShown);

        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.False(_vm.Break.IsShown);
    }

    [Fact]
    public async Task A_long_break_comes_after_the_set_number_of_sessions()
    {
        await StartFocusAsync();
        for (var i = 0; i < 3; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(25));
            await _store.UpdateAsync(b => b.TickPomodoro(_time.GetUtcNow()));
            _time.Advance(TimeSpan.FromMinutes(5));
            await _store.UpdateAsync(b =>
            {
                b.TickPomodoro(_time.GetUtcNow());
                b.StartFocus(null, Actor.User, _time.GetUtcNow());
            });
            await _vm.RefreshAsync();
        }

        _time.Advance(TimeSpan.FromMinutes(25));

        Assert.True(_vm.Break.IsShown);
        Assert.Equal("Time for a longer break", _vm.Break.Title);
        Assert.Equal("15:00", _vm.Break.TimeText);
    }

    [Fact]
    public async Task A_paused_session_never_shows_the_break()
    {
        await StartFocusAsync();
        await _vm.PausePomodoroCommand.ExecuteAsync(null);

        _time.Advance(TimeSpan.FromHours(1));

        Assert.False(_vm.Break.IsShown);
    }
}
