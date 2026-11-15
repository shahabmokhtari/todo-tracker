using Microsoft.Extensions.Time.Testing;
using OneThing.Core;

namespace OneThing.Desktop.Tests;

/// <summary>The sidebar's calmer layout: one timer strip, rows that move between groups, the empty card's words.</summary>
public sealed class SidebarRedesignTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 14, 30, 0, TimeSpan.Zero);
    private readonly InMemoryBoardStore _store = new();
    private readonly FakeTimeProvider _time = new(T0);
    private readonly SidebarViewModel _vm;

    public SidebarRedesignTests()
    {
        _vm = new SidebarViewModel(_store, _time, new FakeShell(), new SidebarOptions("http://127.0.0.1:5317", path => $"launch:{path}", "{}", TimeZoneInfo.Utc, "secret-token"));
    }

    public void Dispose()
    {
        _vm.Dispose();
        _store.Dispose();
    }

    [Theory]
    [InlineData(false, false, true, false, true, true)]
    [InlineData(false, false, false, false, false, false)]
    [InlineData(true, false, true, true, true, false)]
    [InlineData(true, false, false, true, false, false)]
    [InlineData(false, true, true, false, true, true)]
    [InlineData(true, true, true, true, true, false)]
    public void The_timer_strip_shows_the_task_timer_and_the_focus_timer_the_way_the_other_apps_do(
        bool tracking, bool focusActive, bool showIdle, bool tracker, bool focus, bool focusFull)
    {
        var layout = TimerStripLayout.For(tracking, focusActive, showIdle);

        Assert.Equal(tracker, layout.Tracker);
        Assert.Equal(focus, layout.Focus);
        Assert.Equal(focusFull, layout.FocusFull);
        Assert.Equal(tracker || focus, layout.IsVisible);
        Assert.Equal(tracker && focus, layout.HasDivider);
    }

    [Fact]
    public async Task The_strip_shrinks_the_focus_timer_while_a_task_is_timed_and_hides_it_when_the_plugin_is_off()
    {
        await _store.UpdateAsync(b => b.AddTask(new NewTask("Write report"), Actor.User, T0));
        await _vm.RefreshAsync();
        Assert.True(_vm.TimerStrip.FocusFull);
        Assert.False(_vm.TimerStrip.Tracker);

        await _vm.ToggleTimerCommand.ExecuteAsync(_vm.Focus);
        Assert.True(_vm.TimerStrip.Tracker);
        Assert.True(_vm.TimerStrip.Focus);
        Assert.False(_vm.TimerStrip.FocusFull);

        _vm.ShowFocusTimer = false;
        Assert.True(_vm.TimerStrip.Tracker);
        Assert.False(_vm.TimerStrip.Focus);

        await _vm.StopTimerCommand.ExecuteAsync(null);
        Assert.False(_vm.TimerStrip.IsVisible);
    }

    [Fact]
    public async Task A_focus_session_on_its_own_takes_the_whole_strip_and_one_on_a_task_shares_it_with_the_task_timer()
    {
        await _store.UpdateAsync(b => b.StartFocus(null, Actor.User, T0));
        await _vm.RefreshAsync();

        Assert.True(_vm.TimerStrip.FocusFull);
        Assert.False(_vm.TimerStrip.HasDivider);

        await _store.UpdateAsync(b => b.ResetFocus(Actor.User, T0));
        await _store.UpdateAsync(b => b.AddTask(new NewTask("Deep work"), Actor.User, T0));
        await _vm.RefreshAsync();
        await _vm.StartFocusCommand.ExecuteAsync(_vm.Focus);

        Assert.True(_vm.TimerStrip.HasDivider);
        Assert.False(_vm.TimerStrip.FocusFull);
    }

    [Fact]
    public async Task A_row_lists_every_group_it_can_move_to_with_its_own_marked()
    {
        var work = await _store.UpdateAsync(b => b.AddGroup("Errands", "#3b82f6", Actor.User, T0));
        var home = await _store.UpdateAsync(b => b.AddGroup("Garden", "#22c55e", Actor.User, T0));
        await _store.UpdateAsync(b => b.AddTask(new NewTask("Call the bank") { GroupId = work.Id }, Actor.User, T0));
        await _vm.RefreshAsync();

        var targets = await _vm.MoveTargetsAsync(_vm.Focus!);

        Assert.Equal(["Errands", "Garden"], targets.Where(t => t.Name is "Errands" or "Garden").Select(t => t.Name));
        Assert.True(targets.Single(t => t.Id == work.Id).IsCurrent);
        Assert.False(targets.Single(t => t.Id == home.Id).IsCurrent);
    }

    [Fact]
    public async Task Moving_a_row_to_another_group_moves_the_task_as_you()
    {
        var work = await _store.UpdateAsync(b => b.AddGroup("Errands", null, Actor.User, T0));
        var home = await _store.UpdateAsync(b => b.AddGroup("Garden", null, Actor.User, T0));
        var task = await _store.UpdateAsync(b => b.AddTask(new NewTask("Call the bank") { GroupId = work.Id }, Actor.User, T0));
        await _vm.RefreshAsync();

        await _vm.MoveToGroupCommand.ExecuteAsync(new GroupMoveRequest(_vm.Focus!, home.Id));

        Assert.Equal(home.Id, await _store.ReadAsync(b => b.Find(task.Id)!.GroupId));
        Assert.Equal("Moved to Garden", _vm.StatusMessage);
    }

    [Fact]
    public async Task A_subtask_offers_no_group_to_move_to_because_it_follows_its_parent()
    {
        var parent = await _store.UpdateAsync(b => b.AddTask(new NewTask("Ship it"), Actor.User, T0));
        await _store.UpdateAsync(b => b.AddTask(new NewTask("Write notes") { ParentId = parent.Id }, Actor.User, T0));
        await _vm.RefreshAsync();
        var sub = _vm.Now.Append(_vm.Focus!).Single(c => c.Title == "Write notes");

        Assert.Empty(await _vm.MoveTargetsAsync(sub));
    }

    [Fact]
    public async Task With_nothing_due_the_card_says_what_comes_next_in_plain_words()
    {
        var task = await _store.UpdateAsync(b => b.AddTask(new NewTask("Water the plants"), Actor.User, T0));
        await _store.UpdateAsync(b => b.ScheduleNextAction(task.Id, T0.AddHours(2), Actor.User, T0));

        await _vm.RefreshAsync();

        Assert.False(_vm.HasFocus);
        Assert.StartsWith("Next up: Water the plants", _vm.NextUpText, StringComparison.Ordinal);
    }
}
