using Microsoft.Extensions.Time.Testing;
using TodoTracker.Core;

namespace TodoTracker.Desktop.Tests;

/// <summary>The tray icon's menu and tooltip follow what's going on.</summary>
public sealed class TrayMenuTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 14, 30, 0, TimeSpan.Zero);
    private readonly InMemoryBoardStore _store = new();
    private readonly FakeTimeProvider _time = new(T0);
    private readonly SidebarViewModel _vm;

    public TrayMenuTests()
    {
        _vm = new SidebarViewModel(_store, _time, new FakeShell(), new SidebarOptions("http://127.0.0.1:5317", path => $"launch:{path}", "{}", TimeZoneInfo.Utc, "secret-token"));
    }

    public void Dispose()
    {
        _vm.Dispose();
        _store.Dispose();
    }

    private static List<string?> Ids(IEnumerable<TrayItem> items) => items.Select(i => i.Id).ToList();

    [Fact]
    public async Task At_rest_it_offers_to_open_add_and_focus_on_the_task_at_hand()
    {
        await _store.UpdateAsync(b => b.AddTask(new NewTask("Write the report"), Actor.User, T0));
        await _vm.RefreshAsync();

        var items = TrayMenu.Items(_vm, sidebarShown: true);

        Assert.Equal([TrayMenu.Open, TrayMenu.Add, null, TrayMenu.StartFocus, null, TrayMenu.ToggleSidebar, TrayMenu.Settings, null, TrayMenu.Quit], Ids(items));
        Assert.Equal("Focus on “Write the report”", items.Single(i => i.Id == TrayMenu.StartFocus).Label);
        Assert.Equal("Hide the sidebar", items.Single(i => i.Id == TrayMenu.ToggleSidebar).Label);
        Assert.Equal("Show the sidebar", TrayMenu.Items(_vm, sidebarShown: false).Single(i => i.Id == TrayMenu.ToggleSidebar).Label);
        Assert.Equal("Todo Tracker · 1 thing to do now", TrayMenu.Tooltip(_vm));
    }

    [Fact]
    public async Task While_focusing_it_pauses_and_resumes_and_the_tooltip_shows_the_time_left()
    {
        await _store.UpdateAsync(b => b.AddTask(new NewTask("Write the report"), Actor.User, T0));
        await _vm.RefreshAsync();
        await _vm.StartFocusCommand.ExecuteAsync(_vm.Focus);
        await _vm.RefreshAsync();

        Assert.Equal("Pause focus (25:00 left)", TrayMenu.Items(_vm, true).Single(i => i.Id == TrayMenu.PauseFocus).Label);
        Assert.Contains("25:00", TrayMenu.Tooltip(_vm), StringComparison.Ordinal);

        await _vm.PausePomodoroCommand.ExecuteAsync(null);
        await _vm.RefreshAsync();
        Assert.Contains(TrayMenu.Items(_vm, true), i => i.Id == TrayMenu.ResumeFocus);
    }

    [Fact]
    public async Task A_timed_task_can_be_stopped_from_the_tray_and_long_titles_are_shortened()
    {
        var title = new string('x', 200);
        var task = await _store.UpdateAsync(b => b.AddTask(new NewTask(title), Actor.User, T0));
        await _store.UpdateAsync(b => b.StartTimer(task.Id, Actor.User, T0));
        await _vm.RefreshAsync();

        var stop = TrayMenu.Items(_vm, true).Single(i => i.Id == TrayMenu.StopTimer);
        Assert.StartsWith("Stop timing “xxxx", stop.Label, StringComparison.Ordinal);
        Assert.True(stop.Label.Length < 70);
        Assert.True(TrayMenu.Tooltip(_vm).Length <= TrayMenu.MaxTooltip);
    }

    [Fact]
    public void Shortening_never_splits_an_emoji()
    {
        var cut = TrayMenu.Cut(new string('a', 38) + "🎉🎉", 40);

        Assert.Equal(new string('a', 38) + "…", cut);
        Assert.Equal("short", TrayMenu.Cut("short", 40));
    }
}