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
    public async Task A_note_saves_itself_after_a_pause_and_keeps_updating_the_same_note()
    {
        var task = await Seed("Ship");
        await _vm.RefreshAsync();
        var card = _vm.Focus!;

        card.NoteDraft = "deployed";
        _time.Advance(TimeSpan.FromSeconds(2));
        await _vm.WhenNotesSavedAsync();
        card.NoteDraft = "deployed ring 0";
        _time.Advance(TimeSpan.FromSeconds(2));
        await _vm.WhenNotesSavedAsync();

        var note = Assert.Single(await _store.ReadAsync(b => b.Get(task.Id).Notes.ToList()));
        Assert.Equal("deployed ring 0", note.Text);
        Assert.Equal("deployed ring 0", card.NoteDraft);
    }

    [Fact]
    public async Task A_pasted_picture_is_attached_and_embedded_like_obsidian_does()
    {
        var attached = new List<(Guid Task, string Name, int Bytes)>();
        using var vm = new SidebarViewModel(_store, _time, _shell, new SidebarOptions("http://x", p => p, "{}", TimeZoneInfo.Utc)
        {
            Attach = (task, name, bytes) =>
            {
                attached.Add((task, name, bytes.Length));
                return Task.FromResult(name.Replace(".png", " 2.png", StringComparison.Ordinal));
            },
        });
        var task = await Seed("Ship");
        await vm.RefreshAsync();

        var embed = await vm.AttachPastedImageAsync(vm.Focus!, [1, 2, 3]);

        // The stored name (made unique by the vault) is the one embedded.
        Assert.Equal("![[Pasted image 20260105143000 2.png]]", embed);
        Assert.Equal([(task.Id, "Pasted image 20260105143000.png", 3)], attached);
    }

    [Fact]
    public async Task A_picture_that_can_not_be_stored_says_so()
    {
        using var vm = new SidebarViewModel(_store, _time, _shell, new SidebarOptions("http://x", p => p, "{}", TimeZoneInfo.Utc)
        {
            Attach = (_, _, _) => throw new ArgumentException("Attachments can be at most 25 MB."),
        });
        await Seed("Ship");
        await vm.RefreshAsync();

        Assert.Null(await vm.AttachPastedImageAsync(vm.Focus!, [1]));
        Assert.Contains("25 MB", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Null(await _vm.AttachPastedImageAsync(vm.Focus!, [1]));
    }

    [Fact]
    public async Task A_picture_pasted_into_a_task_deleted_meanwhile_says_so()
    {
        using var vm = new SidebarViewModel(_store, _time, _shell, new SidebarOptions("http://x", p => p, "{}", TimeZoneInfo.Utc)
        {
            Attach = (_, _, _) => throw new KeyNotFoundException("That task is gone."),
        });
        await Seed("Ship");
        await vm.RefreshAsync();

        Assert.Null(await vm.AttachPastedImageAsync(vm.Focus!, [1]));
        Assert.Contains("gone", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enter_finishes_the_note_so_the_next_one_is_new()
    {
        var task = await Seed("Ship");
        await _vm.RefreshAsync();
        var card = _vm.Focus!;

        card.NoteDraft = "first";
        await _vm.AddNoteCommand.ExecuteAsync(card);
        card.NoteDraft = "second";
        _time.Advance(TimeSpan.FromSeconds(2));
        await _vm.WhenNotesSavedAsync();

        Assert.Equal(["first", "second"], await _store.ReadAsync(b => b.Get(task.Id).Notes.Select(n => n.Text).ToList()));
    }

    [Fact]
    public async Task Overlapping_saves_of_one_draft_never_create_two_notes()
    {
        var task = await Seed("Ship");
        await _vm.RefreshAsync();
        var card = _vm.Focus!;

        card.NoteDraft = "called";
        _time.Advance(TimeSpan.FromSeconds(2));
        var first = _vm.WhenNotesSavedAsync();
        card.NoteDraft = "called vendor";
        var enter = _vm.AddNoteCommand.ExecuteAsync(card);
        await Task.WhenAll(first, enter);

        var note = Assert.Single(await _store.ReadAsync(b => b.Get(task.Id).Notes.ToList()));
        Assert.Equal("called vendor", note.Text);
    }

    [Fact]
    public async Task Unsaved_drafts_are_saved_when_the_app_closes()
    {
        var task = await Seed("Ship");
        await _vm.RefreshAsync();
        Assert.False(_vm.HasUnsavedNotes);
        _vm.Focus!.NoteDraft = "typed right before closing";
        Assert.True(_vm.HasUnsavedNotes);

        await _vm.FlushNotesAsync();

        Assert.False(_vm.HasUnsavedNotes);

        Assert.Equal(["typed right before closing"], await _store.ReadAsync(b => b.Get(task.Id).Notes.Select(n => n.Text).ToList()));
    }

    [Fact]
    public async Task Cards_move_up_and_down_and_the_top_one_becomes_the_focus()
    {
        await Seed("Alpha");
        await Seed("Bravo");
        await Seed("Charlie");
        await _vm.RefreshAsync();
        string[] Visible() => [_vm.Focus!.Title, .. _vm.Now.Select(c => c.Title)];

        await _vm.MoveUpCommand.ExecuteAsync(_vm.Now.Single(c => c.Title == "Charlie"));
        Assert.Equal(["Alpha", "Charlie", "Bravo"], Visible());

        await _vm.MoveUpCommand.ExecuteAsync(_vm.Now.Single(c => c.Title == "Charlie"));
        Assert.Equal(["Charlie", "Alpha", "Bravo"], Visible());

        await _vm.MoveDownCommand.ExecuteAsync(_vm.Focus);
        Assert.Equal(["Alpha", "Charlie", "Bravo"], Visible());
        Assert.Equal(["Alpha", "Charlie", "Bravo"], await _store.ReadAsync(b => Agenda.Build(b, T0).Now.Select(e => e.Item.Title).ToList()));
    }

    [Fact]
    public async Task A_dragged_card_lands_before_or_after_the_card_it_is_dropped_on()
    {
        await Seed("Alpha");
        await Seed("Bravo");
        await Seed("Charlie");
        await _vm.RefreshAsync();
        var charlie = _vm.Now.Single(c => c.Title == "Charlie");

        await _vm.MoveCardAsync(charlie, _vm.Focus!, after: false);
        Assert.Equal("Charlie", _vm.Focus!.Title);

        await _vm.MoveCardAsync(_vm.Focus!, _vm.Now.Single(c => c.Title == "Bravo"), after: true);
        Assert.Equal(["Alpha", "Bravo", "Charlie"], [_vm.Focus!.Title, .. _vm.Now.Select(c => c.Title)]);
    }

    [Fact]
    public async Task A_task_with_a_due_reminder_can_be_moved_and_stays_where_it_is_put()
    {
        // This test used to check that moves stayed below tasks whose reminder is due ("Moves_stay_below_…"). This change
        // drops that rule on purpose (people couldn't move a reminder's task down): once Do now is arranged, a reminder
        // already due there stays where it's put; only one going off later brings its task back to the top.
        var urgent = await Seed("Reminder due");
        await Seed("Plain");
        await _store.UpdateAsync(b => b.AddReminder(urgent.Id, T0.AddMinutes(-1), "now", Actor.User, T0.AddMinutes(-2)));
        await _vm.RefreshAsync();
        Assert.Equal("Reminder due", _vm.Focus!.Title);
        var plain = _vm.Now.Single(c => c.Title == "Plain");

        Assert.True(_vm.CanMove(plain, -1));
        await _vm.MoveCardAsync(plain, _vm.Focus!, after: false);

        Assert.Equal("Plain", _vm.Focus!.Title);
        Assert.Equal("Reminder due", Assert.Single(_vm.Now).Title);
    }

    [Fact]
    public async Task Waiting_cards_cant_be_reordered()
    {
        await Seed("Now thing");
        var later = await Seed("Later thing");
        await _store.UpdateAsync(b =>
        {
            b.ScheduleNextAction(later.Id, T0.AddHours(2), Actor.User, T0);
            return true;
        });
        await _vm.RefreshAsync();

        Assert.False(_vm.Waiting.Single().CanReorder);
        Assert.True(_vm.Focus!.CanReorder);
        await _vm.MoveUpCommand.ExecuteAsync(_vm.Waiting.Single());
        Assert.Equal("Now thing", _vm.Focus!.Title);
    }

    [Fact]
    public async Task Nothing_is_saved_while_still_typing()
    {
        var task = await Seed("Ship");
        await _vm.RefreshAsync();

        _vm.Focus!.NoteDraft = "dep";
        _time.Advance(TimeSpan.FromMilliseconds(500));
        _vm.Focus!.NoteDraft = "deploy";
        _time.Advance(TimeSpan.FromMilliseconds(500));
        await _vm.WhenNotesSavedAsync();

        Assert.Empty(await _store.ReadAsync(b => b.Get(task.Id).Notes.ToList()));
    }

    [Fact]
    public async Task Cards_show_tags_and_colored_labels()
    {
        await _store.UpdateAsync(b => b.AddTask(new NewTask("Ship") { Tags = ["release"], Labels = ["Deep work"] }, Actor.User, T0));

        await _vm.RefreshAsync();

        Assert.Equal(["#release"], _vm.Focus!.Tags);
        var label = Assert.Single(_vm.Focus!.Labels);
        Assert.Equal("Deep work", label.Name);
        Assert.Matches("^#[0-9a-f]{6}$", label.Color);
    }

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
        _vm.QuickText = "Call mom !! @2h #family";

        await _vm.CaptureCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, _vm.QuickText);
        var item = await _store.ReadAsync(b => b.Items.Single());
        Assert.Equal("Call mom", item.Title);
        Assert.Equal(Priority.Critical, item.Priority);
        Assert.Equal(["family"], item.Tags);
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
    public async Task Done_today_lists_what_got_finished_with_a_cheer_and_can_reopen_it()
    {
        var parent = await Seed("Move house");
        var step = await _store.UpdateAsync(b => b.AddTask(new NewTask("Pack the kitchen") { ParentId = parent.Id }, Actor.User, T0));
        await _vm.RefreshAsync();
        Assert.Empty(_vm.DoneToday);
        Assert.Null(_vm.DoneTodayCheer);

        await _store.UpdateAsync(b => b.Complete(step.Id, Actor.User, T0));
        await _vm.RefreshAsync();

        var done = Assert.Single(_vm.DoneToday);
        Assert.Equal(("Pack the kitchen", "Move house"), (done.Title, done.Context));
        Assert.Equal("🎉 First one done. Nice start!", _vm.DoneTodayCheer);

        await _vm.ReopenCommand.ExecuteAsync(done);

        Assert.Empty(_vm.DoneToday);
        Assert.False(await _store.ReadAsync(b => b.Get(step.Id).IsDone));
        Assert.Contains("Reopened", _vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pictures_pasted_into_quick_add_go_into_the_new_task_and_a_picture_alone_names_it()
    {
        // Reported: a picture couldn't be pasted when adding a task (only into a note of an existing one).
        var attached = new List<(Guid Task, string Name)>();
        using var vm = new SidebarViewModel(_store, _time, _shell, new SidebarOptions("http://x", p => p, "{}", TimeZoneInfo.Utc)
        {
            Attach = (task, name, bytes) =>
            {
                attached.Add((task, name));
                return Task.FromResult(name);
            },
        });
        await vm.RefreshAsync();

        vm.QuickText = "Fix the broken chart";
        vm.AddCapturePicture([1, 2, 3]);
        vm.AddCapturePicture([4, 5]);
        Assert.Equal(2, vm.CapturePictures.Count);
        Assert.NotEqual(vm.CapturePictures[0].Name, vm.CapturePictures[1].Name);
        await vm.CaptureCommand.ExecuteAsync(null);

        var task = await _store.ReadAsync(b => b.Items.Single(i => i.Title == "Fix the broken chart"));
        Assert.Equal([task.Id, task.Id], attached.Select(a => a.Task));
        Assert.Equal($"![[{attached[0].Name}]]\n![[{attached[1].Name}]]", task.Details);
        Assert.Empty(vm.CapturePictures);

        vm.AddCapturePicture([9]);
        vm.RemoveCapturePictureCommand.Execute(vm.CapturePictures[0]);
        Assert.Empty(vm.CapturePictures);

        vm.AddCapturePicture([7]);
        await vm.CaptureCommand.ExecuteAsync(null);
        Assert.Equal(1, await _store.ReadAsync(b => b.Items.Count(i => i.Title == "Pasted image 20260105143000")));

        // A file copied in Explorer keeps its name (two with the same name stay apart).
        vm.AddCapturePicture([1], "Floor plan.jpg");
        vm.AddCapturePicture([2], "Floor plan.jpg");
        Assert.Equal(["Floor plan.jpg", "Floor plan 2.jpg"], vm.CapturePictures.Select(p => p.Name));
    }

    [Theory]
    [InlineData(true, null, null, true)]
    [InlineData(false, null, null, false)]
    [InlineData(true, "Q3 plan\r\nShip it", "<p>Q3 plan</p>", false)] // copied from an office app: the text wins
    [InlineData(true, "hello", null, false)]
    [InlineData(true, "", "<meta charset=\"utf-8\"><img src=\"https://teams.example/1.png\">", true)] // a browser's or Teams' "Copy image"
    [InlineData(true, "https://example.com/cat.png", "<img src=\"https://example.com/cat.png\">", true)]
    [InlineData(true, "  \r\n", null, true)]
    [InlineData(true, @"C:\Users\me\Pictures\Screenshot 2026-10-14.png", null, true)]
    [InlineData(true, "Screenshot 2026-10-14 101500.png", null, true)]
    [InlineData(true, null, "<p>Some words</p>", false)]
    public void A_paste_is_a_picture_unless_real_text_comes_with_it(bool hasImage, string? text, string? html, bool picture)
    {
        // Reported: pictures copied with a link or an <img> tag (browsers, Teams, screenshot tools) didn't paste.
        Assert.Equal(picture, PasteRules.IsPicture(hasImage, text, html));
    }

    [Fact]
    public async Task Cards_show_the_tasks_icon_and_its_color_or_its_first_labels()
    {
        var task = await Seed("Launch");
        await _store.UpdateAsync(b => b.Update(task.Id, new TaskChanges { Icon = "🚀", Color = "#3b82f6" }, Actor.User, T0));
        await _vm.RefreshAsync();
        Assert.Equal(("🚀", "#3b82f6"), (_vm.Focus!.Icon, _vm.Focus.Accent));

        await _store.UpdateAsync(b =>
        {
            b.Update(task.Id, new TaskChanges { ClearIcon = true, ClearColor = true }, Actor.User, T0);
            b.SetLabels(task.Id, ["Urgent"], Actor.User, T0);
        });
        await _vm.RefreshAsync();
        Assert.Equal((null, await _store.ReadAsync(b => b.FindLabel("Urgent")!.Color)), (_vm.Focus!.Icon, _vm.Focus.Accent));
    }

    [Fact]
    public async Task A_repeating_task_says_so_and_done_moves_it_on_and_counts_today()
    {
        var task = await _store.UpdateAsync(b => b.AddTask(new NewTask("Water plants") { Repeat = "every day" }, Actor.User, T0));
        await _vm.RefreshAsync();
        Assert.Contains(_vm.Focus!.Chips, c => c.Text == "🔁 every day");

        await _vm.CompleteCommand.ExecuteAsync(_vm.Focus);

        Assert.False(await _store.ReadAsync(b => b.Get(task.Id).IsDone));
        Assert.Equal("Water plants", Assert.Single(_vm.Waiting).Title);
        Assert.Equal("Water plants", Assert.Single(_vm.DoneToday).Title);
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

        await _vm.SnoozeCommand.ExecuteAsync(new SnoozeRequest(_vm.Focus!, Choice: "1h"));

        var item = await _store.ReadAsync(b => b.Items.Single());
        Assert.Equal(T0.AddHours(1), item.NextActionAt);
        Assert.Single(item.Reminders);
        Assert.Single(_vm.Waiting);
        Assert.Equal("Snoozed until today 15:30", _vm.StatusMessage);
    }

    [Fact]
    public void Snooze_choices_are_the_shared_ones_and_say_when()
    {
        var choices = _vm.SnoozeChoices();

        Assert.Equal(["15m", "1h", "3h", "evening", "tomorrow", "2d", "monday", "week", "month"], choices.Select(c => c.Id));
        Assert.Equal(new DateTimeOffset(2026, 1, 6, 9, 0, 0, TimeSpan.Zero), choices.Single(c => c.Id == "tomorrow").At);
        Assert.Equal("today 18:00", _vm.DescribeWhen(choices.Single(c => c.Id == "evening").At));
    }

    [Fact]
    public async Task Pick_a_time_reads_words_and_asks_again_when_it_cannot()
    {
        await Seed("Plan trip");
        await _vm.RefreshAsync();
        _shell.PromptAnswers.Enqueue("when pigs fly");
        _shell.PromptAnswers.Enqueue("fri 14:00");

        await _vm.PickSnoozeTimeCommand.ExecuteAsync(_vm.Focus);

        var item = await _store.ReadAsync(b => b.Items.Single());
        Assert.Equal(new DateTimeOffset(2026, 1, 9, 14, 0, 0, TimeSpan.Zero), item.NextActionAt);
        Assert.Equal("Snoozed until Fri 14:00", _vm.StatusMessage);
    }

    [Fact]
    public async Task Pick_a_time_cancelled_changes_nothing()
    {
        await Seed("Plan trip");
        await _vm.RefreshAsync();

        await _vm.PickSnoozeTimeCommand.ExecuteAsync(_vm.Focus);

        Assert.Null((await _store.ReadAsync(b => b.Items.Single())).NextActionAt);
    }

    [Fact]
    public async Task Snooze_after_another_task_waits_until_it_is_done()
    {
        var keys = await Seed("Get the keys");
        var move = await Seed("Move in");
        await _vm.RefreshAsync();
        var card = _vm.Now.Concat([_vm.Focus!]).Single(c => c.Id == move.Id);

        Assert.DoesNotContain(await _vm.WaitCandidatesAsync(card), c => c.Id == move.Id);
        Assert.Contains(await _vm.WaitCandidatesAsync(card), c => c.Id == keys.Id);
        await _vm.SnoozeCommand.ExecuteAsync(new SnoozeRequest(card, AfterId: keys.Id));

        Assert.Equal(keys.Id, (await _store.ReadAsync(b => b.Get(move.Id))).AfterId);
        Assert.Equal("Waiting for “Get the keys”", _vm.StatusMessage);
        var waiting = Assert.Single(_vm.Waiting);
        Assert.Equal("Move in", waiting.Title);
        // What already waits for a task isn't offered as something it could wait for (they'd wait for each other).
        Assert.DoesNotContain(await _vm.WaitCandidatesAsync(_vm.Focus!), c => c.Id == move.Id);

        await _vm.CompleteCommand.ExecuteAsync(_vm.Focus);
        Assert.Empty(_vm.Waiting);
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
    public async Task Energy_shows_on_cards_and_in_the_summary_and_is_set_from_the_menu()
    {
        var item = await Seed("Design doc");
        await _store.UpdateAsync(b => b.Plan(item.Id, T0, 120, Actor.User, T0));
        await _vm.RefreshAsync();
        Assert.Equal("⚡ 2 of 10 planned today.", _vm.EnergyText);

        await _vm.SetEnergyCommand.ExecuteAsync(new CardEnergyRequest(_vm.Focus!, 3));
        await _vm.SetDailyEnergyCommand.ExecuteAsync(4);
        Assert.Equal(3, _vm.Focus!.Energy);
        Assert.Contains(_vm.Focus.Chips, c => c.Text == "⚡⚡⚡" && c.Tone == "energy");
        Assert.Equal("⚡ 6 of 4 planned today: 2 too much.", _vm.EnergyText);
        Assert.True(_vm.EnergyOver);

        // The Energy extra switched off: no energy anywhere in the sidebar.
        _vm.ShowEnergy = false;
        await _vm.RefreshAsync();
        Assert.Null(_vm.EnergyText);
        Assert.DoesNotContain(_vm.Focus!.Chips, c => c.Tone == "energy");
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
    public async Task With_an_app_window_the_dashboard_and_a_task_open_there_instead_of_the_browser()
    {
        var opened = new List<string>();
        using var vm = new SidebarViewModel(_store, _time, _shell, new SidebarOptions("http://127.0.0.1:5317", path => $"launch:{path}", "{}", TimeZoneInfo.Utc, "secret-token") { OpenApp = opened.Add });
        var task = await Seed("Ship");
        await vm.RefreshAsync();

        vm.OpenDashboardCommand.Execute(null);
        vm.EditInBrowserCommand.Execute(vm.Focus);

        Assert.Equal(["/", $"/#/task/{task.Id}"], opened);
        Assert.Empty(_shell.OpenedUrls);
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
        _vm.ConnectAiCommand.Execute(null);
        _vm.OpenSyncCommand.Execute(null);
        _vm.SetUpBrowserExtensionCommand.Execute(null);

        Assert.Equal(
            ["launch:/", $"launch:/report.html?id={item.Id}", $"launch:/?item={item.Id}", "launch:/?connect=1", "launch:/?sync=1", "launch:/?extension=1"],
            _shell.OpenedUrls);
        _vm.CopyApiTokenCommand.Execute(null);
        _vm.CopyLinkCommand.Execute(_vm.Focus);

        Assert.Equal(["{\"mcpServers\":{}}", "secret-token", $"todotracker://task/{item.Id}"], _shell.Clipboard);
    }

    [Fact]
    public void A_copied_link_pastes_as_a_named_link_in_rich_editors()
    {
        var html = HtmlClipboard.Format(HtmlClipboard.Anchor("todotracker://task/1", "Ship <it> & café"));
        int Offset(string name) => int.Parse(html.Split(name + ":")[1][..10], System.Globalization.CultureInfo.InvariantCulture);
        var bytes = System.Text.Encoding.UTF8.GetBytes(html);
        string Slice(int from, int to) => System.Text.Encoding.UTF8.GetString(bytes, from, to - from);

        Assert.Equal("<a href=\"todotracker://task/1\">Ship &lt;it&gt; &amp; café</a>", Slice(Offset("StartFragment"), Offset("EndFragment")));
        Assert.StartsWith("<html>", Slice(Offset("StartHTML"), Offset("EndHTML")));
        Assert.Equal(bytes.Length, Offset("EndHTML"));
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

    [Fact]
    public async Task The_strip_names_the_task_at_hand_the_timed_one_first()
    {
        await Seed("Write report", Priority.Critical);
        var other = await Seed("Call bank");
        await _vm.RefreshAsync();
        Assert.Equal("Write report", _vm.ActiveTitle);

        await _store.UpdateAsync(b => b.StartTimer(other.Id, Actor.User, T0));
        await _vm.RefreshAsync();

        Assert.Equal("Call bank", _vm.ActiveTitle);
    }

    [Fact]
    public void Peeking_shows_the_whole_sidebar_while_the_strip_keeps_its_screen_space()
    {
        _vm.IsCompact = true;
        Assert.True(_vm.ShowsStrip);

        _vm.IsPeeking = true;
        Assert.False(_vm.ShowsStrip);
        Assert.True(_vm.IsCompact);

        // Expanding for good ends the peek.
        _vm.IsCompact = false;
        Assert.False(_vm.IsPeeking);
        Assert.False(_vm.ShowsStrip);
    }
}