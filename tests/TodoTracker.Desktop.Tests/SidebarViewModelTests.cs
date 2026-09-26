using Microsoft.Extensions.Time.Testing;
using TodoTracker.Core;

namespace TodoTracker.Desktop.Tests;

public sealed class FakeShell : IDesktopShell
{
    public List<string> OpenedUrls { get; } = [];

    public List<string> Clipboard { get; } = [];

    public Queue<string?> PromptAnswers { get; } = new();

    public bool ConfirmAnswer { get; set; } = true;

    public void OpenUrl(string url) => OpenedUrls.Add(url);

    public void CopyToClipboard(string text) => Clipboard.Add(text);

    public void RunOnUi(Action action) => action();

    public string? Prompt(string title, string message, string? initialValue = null) => PromptAnswers.Count > 0 ? PromptAnswers.Dequeue() : null;

    public bool Confirm(string message) => ConfirmAnswer;
}

public sealed class SidebarViewModelTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 14, 30, 0, TimeSpan.Zero);
    private readonly InMemoryBoardStore _store = new();
    private readonly FakeTimeProvider _time = new(T0);
    private readonly FakeShell _shell = new();
    private readonly SidebarViewModel _vm;

    public SidebarViewModelTests()
    {
        _vm = new SidebarViewModel(_store, _time, _shell, new SidebarOptions("http://127.0.0.1:5317", path => $"launch:{path}", "{\"mcpServers\":{}}", TimeZoneInfo.Utc, "secret-token"));
    }

    public void Dispose()
    {
        _vm.Dispose();
        _store.Dispose();
    }

    private Task<WorkItem> Seed(string title, Priority priority = Priority.Normal, Guid? parent = null, Guid? group = null) =>
        _store.UpdateAsync(b => b.AddTask(new NewTask(title) { Priority = priority, ParentId = parent, GroupId = group }, Actor.User, T0));

    [Fact]
    public async Task Refresh_projects_focus_now_waiting_workstreams_and_tabs()
    {
        var x = await Seed("Roll out feature X", Priority.High);
        await _store.UpdateAsync(b => b.AddSteps(x.Id, ["Ring 0", "Ring 1"], TimeSpan.FromHours(24), Actor.User, T0));
        var later = await Seed("Later thing");
        await _store.UpdateAsync(b => b.ScheduleNextAction(later.Id, T0.AddHours(2), Actor.User, T0));

        await _vm.RefreshAsync();

        Assert.Equal("Ring 0", _vm.Focus?.Title);
        Assert.Equal("Roll out feature X", _vm.Focus?.Breadcrumb);
        Assert.Contains("Step 1 of 2", _vm.Focus?.Meta, StringComparison.Ordinal);
        Assert.Equal("#f97316", _vm.Focus?.PriorityColor);
        Assert.Empty(_vm.Now);
        var waiting = Assert.Single(_vm.Waiting);
        Assert.Equal("Later thing", waiting.Title);
        Assert.Contains("back in 2h", waiting.Meta, StringComparison.Ordinal);
        // Changed by the UI refresh (intentional): Workstreams now lists only tasks with subtasks, like the web
        // dashboard. "Later thing" is a single task, so only the rollout remains.
        Assert.Equal("Roll out feature X", Assert.Single(_vm.Workstreams).Title);
        Assert.Equal(["All", "Work", "Personal"], _vm.Groups.Select(g => g.Name));
        Assert.Equal(1, _vm.NowCount);
        Assert.Equal(1, _vm.WaitingCount);
    }

    [Fact]
    public async Task Cards_expose_toned_chips_matching_the_web_dashboard()
    {
        var x = await Seed("Roll out feature X", Priority.High);
        await _store.UpdateAsync(b => b.AddSteps(x.Id, ["Ring 0", "Ring 1"], TimeSpan.FromHours(24), Actor.User, T0));
        var overdue = await _store.UpdateAsync(b => b.AddTask(new NewTask("Overdue doc") { Deadline = T0.AddHours(-1) }, Actor.User, T0));
        await _store.UpdateAsync(b => b.AddNote(overdue.Id, "started", Actor.User, T0));
        await _vm.RefreshAsync();

        Assert.Equal([new ChipViewModel("Step 1 of 2", "step")], _vm.Focus!.Chips);
        Assert.Equal([new ChipViewModel("overdue 1h ago", "danger"), new ChipViewModel("1 note", "muted")], _vm.Now.Single().Chips);
    }

    [Fact]
    public async Task Summary_line_reads_like_the_web_dashboard()
    {
        await _vm.RefreshAsync();
        Assert.Equal("All clear. Nothing needs you right now.", _vm.Summary);

        var item = await Seed("A");
        await Seed("B");
        await _store.UpdateAsync(b => b.AddReminder(item.Id, T0, "go", Actor.User, T0));
        var later = await Seed("C");
        await _store.UpdateAsync(b => b.ScheduleNextAction(later.Id, T0.AddHours(1), Actor.User, T0));
        await _vm.WhenIdleAsync();

        Assert.Equal("2 things to do now · 1 reminder · 1 waiting.", _vm.Summary);
    }

    [Fact]
    public async Task Pomodoro_exposes_elapsed_fraction_for_the_progress_ring()
    {
        await Seed("Deep work");
        await _vm.RefreshAsync();
        Assert.Equal(0, _vm.Pomodoro.Fraction);

        await _vm.StartFocusCommand.ExecuteAsync(_vm.Focus);
        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(0.2, _vm.Pomodoro.Fraction, 3);
    }

    [Fact]
    public async Task Now_list_excludes_the_focus_card()
    {
        await Seed("First", Priority.Critical);
        await Seed("Second");

        await _vm.RefreshAsync();

        Assert.Equal("First", _vm.Focus?.Title);
        Assert.Equal(["Second"], _vm.Now.Select(c => c.Title));
        Assert.Equal(2, _vm.NowCount);
    }

    [Fact]
    public async Task Selecting_a_group_tab_filters_the_board()
    {
        await Seed("Work thing");
        await _vm.RefreshAsync();
        var personal = _vm.Groups.Single(g => g.Name == "Personal");
        await Seed("Dentist", group: personal.Id);

        await _vm.SelectGroupCommand.ExecuteAsync(personal);

        Assert.True(personal.IsSelected);
        Assert.Equal("Dentist", _vm.Focus?.Title);
        Assert.Empty(_vm.Now);
    }

    [Fact]
    public async Task Quick_capture_adds_to_selected_group_and_clears_text()
    {
        await _vm.RefreshAsync();
        await _vm.SelectGroupCommand.ExecuteAsync(_vm.Groups.Single(g => g.Name == "Personal"));
        _vm.QuickText = "Call mom !! @2h";

        await _vm.CaptureCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, _vm.QuickText);
        var item = await _store.ReadAsync(b => b.Items.Single());
        Assert.Equal("Call mom", item.Title);
        Assert.Equal(Priority.Critical, item.Priority);
        Assert.Equal(T0.AddHours(2), item.NextActionAt);
        Assert.Equal("Personal", await _store.ReadAsync(b => b.GetGroup(item.GroupId).Name));
        Assert.Single(_vm.Waiting);
    }

    [Fact]
    public async Task Quick_capture_with_blank_text_shows_a_message_instead_of_throwing()
    {
        _vm.QuickText = "  !! ";

        await _vm.CaptureCommand.ExecuteAsync(null);

        Assert.Equal("Type a task title.", _vm.StatusMessage);
    }

    [Fact]
    public async Task Complete_moves_rollout_to_next_gated_step()
    {
        var x = await Seed("Feature A");
        await _store.UpdateAsync(b => b.AddSteps(x.Id, ["Ring 0", "Ring 1"], TimeSpan.FromHours(24), Actor.User, T0));
        await _vm.RefreshAsync();

        await _vm.CompleteCommand.ExecuteAsync(_vm.Focus);

        Assert.Null(_vm.Focus);
        Assert.Equal("Ring 1", Assert.Single(_vm.Waiting).Title);
        Assert.Contains("Done", _vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Complete_out_of_order_reports_the_reason()
    {
        var x = await Seed("Feature A");
        var steps = await _store.UpdateAsync(b => b.AddSteps(x.Id, ["Ring 0", "Ring 1"], null, Actor.User, T0));
        await _vm.RefreshAsync();
        var fake = new CardViewModel { Id = steps[1].Id, Title = "Ring 1" };

        await _vm.CompleteCommand.ExecuteAsync(fake);

        Assert.Contains("Ring 0", _vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snooze_defers_with_reminder()
    {
        await Seed("Feature B");
        await _vm.RefreshAsync();
        var option = _vm.SnoozeOptions.Single(o => o.Label == "1 hour");

        await _vm.SnoozeCommand.ExecuteAsync(new SnoozeRequest(_vm.Focus!, option));

        var item = await _store.ReadAsync(b => b.Items.Single());
        Assert.Equal(T0.AddHours(1), item.NextActionAt);
        Assert.Single(item.Reminders);
        Assert.Single(_vm.Waiting);
    }

    [Fact]
    public void Snooze_options_include_tomorrow_morning_in_local_zone()
    {
        var tomorrow = _vm.SnoozeOptions.Single(o => o.Label == "Tomorrow 9:00");
        Assert.Equal(new DateTimeOffset(2026, 1, 6, 9, 0, 0, TimeSpan.Zero), T0.AddMinutes(tomorrow.Minutes(T0, TimeZoneInfo.Utc)));
    }

    [Fact]
    public async Task Inline_note_is_saved_and_cleared()
    {
        await Seed("Feature B");
        await _vm.RefreshAsync();
        var card = _vm.Focus!;
        card.NoteDraft = "Ring 2 looks good";

        await _vm.AddNoteCommand.ExecuteAsync(card);

        Assert.Equal("Ring 2 looks good", Assert.Single(_vm.RecentNotes).Text);
        Assert.Equal("Ring 2 looks good", await _store.ReadAsync(b => b.Items[0].Notes[0].Text));
    }

    [Fact]
    public async Task Dismiss_reminder_clears_attention()
    {
        var item = await Seed("Feature B");
        await _store.UpdateAsync(b => b.AddReminder(item.Id, T0, "Now!", Actor.User, T0));
        await _vm.RefreshAsync();
        Assert.True(_vm.Focus!.NeedsAttention);
        Assert.Equal("Now!", _vm.Focus.ReminderMessage);

        await _vm.DismissReminderCommand.ExecuteAsync(_vm.Focus);

        Assert.False(_vm.Focus!.NeedsAttention);
    }

    [Fact]
    public async Task Add_subtask_prompts_for_title()
    {
        await Seed("Feature X");
        await _vm.RefreshAsync();
        _shell.PromptAnswers.Enqueue("Write rollout plan");

        await _vm.AddSubtaskCommand.ExecuteAsync(_vm.Focus);

        Assert.Equal("Write rollout plan", _vm.Focus?.Title);
        Assert.Equal("Feature X", _vm.Focus?.Breadcrumb);
    }

    [Fact]
    public async Task Pomodoro_start_pause_and_tick_update_the_clock()
    {
        await Seed("Deep work");
        await _vm.RefreshAsync();

        await _vm.StartFocusCommand.ExecuteAsync(_vm.Focus);
        Assert.Equal("25:00", _vm.Pomodoro.TimeText);
        Assert.Equal("Focus · Deep work", _vm.Pomodoro.Label);
        Assert.True(_vm.Pomodoro.IsRunning);

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("24:00", _vm.Pomodoro.TimeText);

        await _vm.PausePomodoroCommand.ExecuteAsync(null);
        Assert.False(_vm.Pomodoro.IsRunning);
        await _vm.ResetPomodoroCommand.ExecuteAsync(null);
        Assert.Equal("Focus", _vm.Pomodoro.Label);
    }

    [Fact]
    public async Task Store_changes_from_other_clients_refresh_the_sidebar()
    {
        await _vm.RefreshAsync();

        await Seed("Added by an agent");

        Assert.Equal("Added by an agent", _vm.Focus?.Title);
    }

    [Fact]
    public async Task Periodic_refresh_wakes_waiting_items()
    {
        var item = await Seed("Later");
        await _store.UpdateAsync(b => b.ScheduleNextAction(item.Id, T0.AddMinutes(1), Actor.User, T0));
        await _vm.RefreshAsync();
        Assert.Null(_vm.Focus);

        _time.Advance(TimeSpan.FromSeconds(30));
        _time.Advance(TimeSpan.FromSeconds(30));
        await _vm.WhenIdleAsync();

        Assert.Equal("Later", _vm.Focus?.Title);
    }

    [Fact]
    public async Task Browser_integrations_open_the_right_urls()
    {
        var item = await Seed("Feature X");
        await _vm.RefreshAsync();

        _vm.OpenDashboardCommand.Execute(null);
        _vm.OpenReportCommand.Execute(_vm.Focus);
        _vm.EditInBrowserCommand.Execute(_vm.Focus);
        _vm.CopyMcpConfigCommand.Execute(null);

        Assert.Equal(
            ["launch:/", $"launch:/report.html?id={item.Id}", $"launch:/?item={item.Id}"],
            _shell.OpenedUrls);
        _vm.CopyApiTokenCommand.Execute(null);

        Assert.Equal(["{\"mcpServers\":{}}", "secret-token"], _shell.Clipboard);
    }

    [Fact]
    public async Task Add_group_prompts_and_selects_it()
    {
        await _vm.RefreshAsync();
        _shell.PromptAnswers.Enqueue("Side project");

        await _vm.AddGroupCommand.ExecuteAsync(null);

        var tab = _vm.Groups.Single(g => g.Name == "Side project");
        Assert.True(tab.IsSelected);
    }

    [Fact]
    public async Task Handle_reminder_toast_actions()
    {
        var item = await Seed("Feature B");
        await _store.UpdateAsync(b => b.AddReminder(item.Id, T0, "Go", Actor.User, T0));
        await _vm.RefreshAsync();

        await _vm.HandleToastActionAsync(ToastAction.Snooze, item.Id, _vm.Focus!.ReminderId);

        var stored = await _store.ReadAsync(b => b.Get(item.Id));
        Assert.Equal(T0.AddHours(1), stored.NextActionAt);
        Assert.NotNull(stored.Reminders[0].DismissedAt);

        await _vm.HandleToastActionAsync(ToastAction.Done, item.Id, null);
        Assert.True(await _store.ReadAsync(b => b.Get(item.Id).IsDone));

        await _vm.HandleToastActionAsync(ToastAction.Open, item.Id, null);
        Assert.Equal($"launch:/?item={item.Id}", _shell.OpenedUrls[^1]);
    }

    [Fact]
    public async Task Identical_recent_notes_do_not_break_refresh()
    {
        // Review finding: value-equal NoteViewModels made CollectionSync.Move throw, freezing the sidebar.
        var item = await Seed("Build");
        await _store.UpdateAsync(b => b.AddNote(item.Id, "still failing", Actor.Agent("ci"), T0));
        await _store.UpdateAsync(b => b.AddNote(item.Id, "still failing", Actor.Agent("ci"), T0));

        await _vm.RefreshAsync();
        await _vm.RefreshAsync();

        Assert.Equal(2, _vm.RecentNotes.Count);
    }

    [Fact]
    public async Task Draft_survives_when_a_card_moves_from_now_to_focus()
    {
        var first = await Seed("First", Priority.Critical);
        await Seed("Second");
        await _vm.RefreshAsync();
        var second = _vm.Now.Single();
        second.IsNoteOpen = true;
        second.NoteDraft = "half-typed thought";

        await _store.UpdateAsync(b => b.Complete(first.Id, Actor.User, T0));
        await _vm.WhenIdleAsync();

        Assert.Same(second, _vm.Focus);
        Assert.Equal("half-typed thought", _vm.Focus!.NoteDraft);
        Assert.True(_vm.Focus.IsNoteOpen);
    }

    [Fact]
    public async Task Save_failures_are_reported_instead_of_crashing()
    {
        // Review finding: IOException from a locked board file escaped the command and killed the app.
        using var failing = new FailingStore(_store);
        using var vm = new SidebarViewModel(failing, _time, _shell, new SidebarOptions("http://x", p => p, "{}", TimeZoneInfo.Utc));
        vm.QuickText = "Anything";
        failing.Fail = true;

        await vm.CaptureCommand.ExecuteAsync(null);

        Assert.Contains("couldn't save", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Anything", vm.QuickText);
    }

    private sealed class FailingStore(IBoardStore inner) : IBoardStore, IDisposable
    {
        public bool Fail { get; set; }

        public event EventHandler? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public Task<T> ReadAsync<T>(Func<TaskBoard, T> read, CancellationToken cancellationToken = default) => inner.ReadAsync(read, cancellationToken);

        public Task<T> UpdateAsync<T>(Func<TaskBoard, T> mutate, CancellationToken cancellationToken = default) =>
            Fail ? throw new IOException("The process cannot access the file because it is being used by another process.") : inner.UpdateAsync(mutate, cancellationToken);

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task Compact_mode_summarizes_counts()
    {
        await Seed("A", Priority.Critical);
        await Seed("B");
        await _vm.RefreshAsync();

        _vm.ToggleCompactCommand.Execute(null);

        Assert.True(_vm.IsCompact);
        Assert.Equal("2", _vm.CompactSummary);
    }
}


