using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TodoTracker.Core;

namespace TodoTracker.Desktop;

/// <summary>
/// The always-visible sidebar: one focus card, then collapsible Do now / Waiting / Workstreams / Recent notes,
/// group tabs, quick capture, and the focus timer. All reads/writes go through <see cref="IBoardStore"/>, which is
/// shared with the embedded server so agents, the browser, and the sidebar always see the same board.
/// </summary>
public sealed partial class SidebarViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private readonly IBoardStore _store;
    private readonly TimeProvider _time;
    private readonly IDesktopShell _shell;
    private readonly SidebarOptions _options;
    private readonly ITimer _clockTimer;
    private readonly ITimer _refreshTimer;
    private Guid? _selectedGroupId;
    private Task _pending = Task.CompletedTask;

    public SidebarViewModel(IBoardStore store, TimeProvider time, IDesktopShell shell, SidebarOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _time = time;
        _shell = shell;
        _options = options;
        _store.Changed += OnStoreChanged;
        _clockTimer = time.CreateTimer(_ => _shell.RunOnUi(() => Pomodoro.Tick(_time.GetUtcNow())), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _refreshTimer = time.CreateTimer(_ => _shell.RunOnUi(QueueRefresh), null, RefreshInterval, RefreshInterval);
    }

    public ObservableCollection<GroupTabViewModel> Groups { get; } = [];

    public ObservableCollection<CardViewModel> Now { get; } = [];

    public ObservableCollection<CardViewModel> Waiting { get; } = [];

    public ObservableCollection<WorkstreamViewModel> Workstreams { get; } = [];

    public ObservableCollection<NoteViewModel> RecentNotes { get; } = [];

    public PomodoroViewModel Pomodoro { get; } = new();

    public IReadOnlyList<SnoozeOption> SnoozeOptions { get; } = SnoozeOption.Defaults;

    [ObservableProperty]
    public partial CardViewModel? Focus { get; set; }

    [ObservableProperty]
    public partial string QuickText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    [ObservableProperty]
    public partial int NowCount { get; set; }

    [ObservableProperty]
    public partial int WaitingCount { get; set; }

    [ObservableProperty]
    public partial bool HasAttention { get; set; }

    [ObservableProperty]
    public partial string? NextUpText { get; set; }

    [ObservableProperty]
    public partial bool IsCompact { get; set; }

    public string CompactSummary => NowCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public bool HasFocus => Focus is not null;

    partial void OnFocusChanged(CardViewModel? value) => OnPropertyChanged(nameof(HasFocus));

    partial void OnNowCountChanged(int value) => OnPropertyChanged(nameof(CompactSummary));

    /// <summary>Completes when any refresh triggered by timers or store events has finished (used by tests and smoke checks).</summary>
    public Task WhenIdleAsync() => _pending;

    public async Task RefreshAsync()
    {
        var now = _time.GetUtcNow();
        var snapshot = await _store.ReadAsync(b => Project(b, now, _selectedGroupId)).ConfigureAwait(true);
        if (snapshot.GroupMissing)
        {
            _selectedGroupId = null;
            snapshot = await _store.ReadAsync(b => Project(b, now, null)).ConfigureAwait(true);
        }

        CollectionSync.Sync(Groups, snapshot.Groups.Select(ReuseTab).ToList());
        if (Focus is not null && snapshot.Focus is not null && Focus.Id == snapshot.Focus.Id)
        {
            Focus.CopyFrom(snapshot.Focus);
        }
        else
        {
            Focus = snapshot.Focus;
        }

        CollectionSync.SyncCards(Now, snapshot.Now);
        CollectionSync.SyncCards(Waiting, snapshot.Waiting);
        CollectionSync.Sync(Workstreams, snapshot.Workstreams);
        CollectionSync.Sync(RecentNotes, snapshot.Notes);
        NowCount = snapshot.NowCount;
        WaitingCount = snapshot.Waiting.Count;
        HasAttention = snapshot.HasAttention;
        NextUpText = snapshot.NextUp;
        Pomodoro.Update(snapshot.Pomodoro, snapshot.PomodoroItem, now);
    }

    [RelayCommand]
    private Task SelectGroup(GroupTabViewModel? tab)
    {
        _selectedGroupId = tab?.Id;
        return RefreshAsync();
    }

    [RelayCommand]
    private Task Capture() => Run(async () =>
    {
        var text = QuickText;
        await _store.UpdateAsync(b =>
        {
            var now = _time.GetUtcNow();
            var capture = QuickCaptureParser.Parse(text, now, _options.TimeZone);
            var item = b.AddTask(new NewTask(capture.Title) { GroupId = _selectedGroupId, Priority = capture.Priority, Deadline = capture.Deadline }, Actor.User, now);
            if (capture.NextActionAt is { } at)
            {
                b.ScheduleNextAction(item.Id, at, Actor.User, now, notify: true);
            }
        }).ConfigureAwait(true);
        QuickText = string.Empty;
        return "Added";
    });

    [RelayCommand]
    private Task Complete(CardViewModel? card) => card is null ? Task.CompletedTask : Run(async () =>
    {
        await _store.UpdateAsync(b => b.Complete(card.Id, Actor.User, _time.GetUtcNow())).ConfigureAwait(true);
        return $"Done: {card.Title} 🎉";
    });

    [RelayCommand]
    private Task Snooze(SnoozeRequest? request) => request is null ? Task.CompletedTask : Run(async () =>
    {
        await _store.UpdateAsync(b =>
        {
            var now = _time.GetUtcNow();
            b.ScheduleNextAction(request.Card.Id, now.AddMinutes(request.Option.Minutes(now, _options.TimeZone)), Actor.User, now, notify: true);
        }).ConfigureAwait(true);
        return $"Snoozed: {request.Option.Label.ToLowerInvariant()}";
    });

    [RelayCommand]
    private Task BringBack(CardViewModel? card) => card is null ? Task.CompletedTask : Run(async () =>
    {
        await _store.UpdateAsync(b => b.ClearNextAction(card.Id, Actor.User, _time.GetUtcNow())).ConfigureAwait(true);
        return "Back in Do now";
    });

    [RelayCommand]
    private Task AddNote(CardViewModel? card) => card is null || string.IsNullOrWhiteSpace(card.NoteDraft) ? Task.CompletedTask : Run(async () =>
    {
        var text = card.NoteDraft;
        await _store.UpdateAsync(b => b.AddNote(card.Id, text, Actor.User, _time.GetUtcNow())).ConfigureAwait(true);
        card.NoteDraft = string.Empty;
        card.IsNoteOpen = false;
        return "Note saved";
    });

    [RelayCommand]
    private Task DismissReminder(CardViewModel? card) => card?.ReminderId is not { } reminderId ? Task.CompletedTask : Run(async () =>
    {
        await _store.UpdateAsync(b => b.DismissReminder(card.Id, reminderId, Actor.User, _time.GetUtcNow())).ConfigureAwait(true);
        return null;
    });

    [RelayCommand]
    private Task AddSubtask(CardViewModel? card)
    {
        if (card is null || _shell.Prompt("Add subtask", $"New subtask under \"{card.Title}\":") is not { } title || string.IsNullOrWhiteSpace(title))
        {
            return Task.CompletedTask;
        }

        return Run(async () =>
        {
            await _store.UpdateAsync(b => b.AddTask(new NewTask(title) { ParentId = card.Id }, Actor.User, _time.GetUtcNow())).ConfigureAwait(true);
            return "Subtask added";
        });
    }

    [RelayCommand]
    private Task AddGroup()
    {
        if (_shell.Prompt("New group", "Group name (e.g. Personal, Side project):") is not { } name || string.IsNullOrWhiteSpace(name))
        {
            return Task.CompletedTask;
        }

        return Run(async () =>
        {
            _selectedGroupId = await _store.UpdateAsync(b => b.AddGroup(name, null, Actor.User, _time.GetUtcNow()).Id).ConfigureAwait(true);
            return $"Added group {name.Trim()}";
        });
    }

    [RelayCommand]
    private Task StartFocus(CardViewModel? card) => Run(async () =>
    {
        await _store.UpdateAsync(b => b.StartFocus(card?.Id ?? Focus?.Id, Actor.User, _time.GetUtcNow())).ConfigureAwait(true);
        return null;
    });

    [RelayCommand]
    private Task PausePomodoro() => Pomo(t => t.Pause(_time.GetUtcNow()));

    [RelayCommand]
    private Task ResumePomodoro() => Pomo(t => t.Resume(_time.GetUtcNow()));

    [RelayCommand]
    private Task SkipPomodoro() => Pomo(t => t.Skip(_time.GetUtcNow()));

    [RelayCommand]
    private Task ResetPomodoro() => Pomo(t => t.Reset());

    [RelayCommand]
    private void OpenDashboard() => _shell.OpenUrl(_options.LaunchUrl);

    [RelayCommand]
    private void OpenReport(CardViewModel? card) => _shell.OpenUrl(Launch(card is null ? "/report.html" : $"/report.html?id={card.Id}"));

    [RelayCommand]
    private void OpenWorkstreamReport(WorkstreamViewModel? workstream) =>
        _shell.OpenUrl(Launch(workstream is null ? "/report.html" : $"/report.html?id={workstream.Id}"));

    [RelayCommand]
    private void EditInBrowser(CardViewModel? card)
    {
        if (card is not null)
        {
            _shell.OpenUrl(Launch($"/?item={card.Id}"));
        }
    }

    [RelayCommand]
    private void CopyMcpConfig()
    {
        _shell.CopyToClipboard(_options.McpConfigJson);
        StatusMessage = "MCP config copied. Paste it into your Copilot MCP settings.";
    }

    [RelayCommand]
    private void ToggleCompact() => IsCompact = !IsCompact;

    [RelayCommand]
    private static void ToggleNote(CardViewModel? card)
    {
        if (card is not null)
        {
            card.IsNoteOpen = !card.IsNoteOpen;
        }
    }

    private GroupTabViewModel ReuseTab(GroupTabViewModel fresh)
    {
        var keep = Groups.FirstOrDefault(g => g.Id == fresh.Id);
        if (keep is null)
        {
            return fresh;
        }

        keep.Name = fresh.Name;
        keep.Color = fresh.Color;
        keep.Count = fresh.Count;
        keep.HasAttention = fresh.HasAttention;
        keep.IsSelected = fresh.IsSelected;
        return keep;
    }

    public Task HandleToastActionAsync(ToastAction action, Guid itemId, Guid? reminderId) => action switch
    {
        ToastAction.Done => Run(async () =>
        {
            await _store.UpdateAsync(b => b.Complete(itemId, Actor.User, _time.GetUtcNow())).ConfigureAwait(true);
            return "Done 🎉";
        }),
        ToastAction.Snooze => Run(async () =>
        {
            await _store.UpdateAsync(b =>
            {
                var now = _time.GetUtcNow();
                if (reminderId is { } rid)
                {
                    b.DismissReminder(itemId, rid, Actor.User, now);
                }

                b.ScheduleNextAction(itemId, now.AddHours(1), Actor.User, now, notify: true);
            }).ConfigureAwait(true);
            return "Snoozed 1 hour";
        }),
        _ => OpenItem(itemId),
    };

    private Task OpenItem(Guid itemId)
    {
        _shell.OpenUrl(Launch($"/?item={itemId}"));
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
        _clockTimer.Dispose();
        _refreshTimer.Dispose();
    }

    /// <summary>Opens a page through the launch link so the browser gets a session cookie first.</summary>
    private string Launch(string path) => $"{_options.LaunchUrl}&return={Uri.EscapeDataString(path)}";

    private void OnStoreChanged(object? sender, EventArgs e) => _shell.RunOnUi(QueueRefresh);

    private void QueueRefresh() => _pending = _pending.IsCompleted ? RefreshAsync() : _pending.ContinueWith(_ => RefreshAsync(), TaskSchedulerExtensions.FromCurrentSynchronizationContextOrDefault()).Unwrap();

    private Task Pomo(Action<PomodoroTimer> action) => Run(async () =>
    {
        await _store.UpdateAsync(b => action(b.Pomodoro)).ConfigureAwait(true);
        return null;
    });

    private async Task Run(Func<Task<string?>> action)
    {
        try
        {
            StatusMessage = await action().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or NotSupportedException)
        {
            StatusMessage = ErrorText.Friendly(ex);
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private sealed record Snapshot(
        IReadOnlyList<GroupTabViewModel> Groups,
        CardViewModel? Focus,
        IReadOnlyList<CardViewModel> Now,
        IReadOnlyList<CardViewModel> Waiting,
        IReadOnlyList<WorkstreamViewModel> Workstreams,
        IReadOnlyList<NoteViewModel> Notes,
        int NowCount,
        bool HasAttention,
        string? NextUp,
        PomodoroTimer Pomodoro,
        string? PomodoroItem,
        bool GroupMissing);

    private Snapshot Project(TaskBoard board, DateTimeOffset now, Guid? groupId)
    {
        if (groupId is { } g && !board.Groups.Any(x => x.Id == g))
        {
            return new Snapshot([], null, [], [], [], [], 0, false, null, board.Pomodoro, null, GroupMissing: true);
        }

        var d = Agenda.Build(board, now, recentNoteCount: 6, groupId: groupId);
        var total = d.GroupCounts.Values.Sum(c => c.Now);
        var tabs = new List<GroupTabViewModel>
        {
            new() { Id = null, Name = "All", Count = total, HasAttention = d.GroupCounts.Values.Any(c => c.Attention > 0), IsSelected = groupId is null },
        };
        tabs.AddRange(board.Groups.Select(gr => new GroupTabViewModel
        {
            Id = gr.Id,
            Name = gr.Name,
            Color = gr.Color ?? "#6366f1",
            Count = d.GroupCounts[gr.Id].Now,
            HasAttention = d.GroupCounts[gr.Id].Attention > 0,
            IsSelected = gr.Id == groupId,
        }));

        // Reuse tab instances when nothing changed so the tab strip does not flicker.
        var reusedTabs = tabs.Select(t => Groups.FirstOrDefault(e => e.Id == t.Id && e.Name == t.Name && e.Count == t.Count && e.HasAttention == t.HasAttention && e.Color == t.Color) is { } keep
            ? Selected(keep, t.IsSelected)
            : t).ToList();

        var cards = d.Now.Select(e => ToCard(e, now)).ToList();
        var nextUp = d.Focus is null && d.Waiting.Count > 0 ? $"Next: {d.Waiting[0].Item.Title} {RelativeTime.Format(d.Waiting[0].WakeAt!.Value, now)}" : null;
        return new Snapshot(
            reusedTabs,
            cards.FirstOrDefault(),
            cards.Skip(1).ToList(),
            d.Waiting.Select(e => ToCard(e, now)).ToList(),
            d.Overview.Select(o => new WorkstreamViewModel(
                o.Item.Id,
                o.Item.Title,
                PriorityPalette.ColorOf(o.TopPriority),
                o.TotalLeaves == 0 ? 0 : (int)Math.Round(100.0 * o.DoneLeaves / o.TotalLeaves),
                WorkstreamSummary(o, now))).ToList(),
            d.RecentNotes.Select(n => new NoteViewModel(n.Item.Id, n.Note.Text, n.Item.Title, $"{n.Note.Author.DisplayName} · {RelativeTime.Format(n.Note.At, now)}", n.Note.SourceUrl)).ToList(),
            d.Now.Count,
            d.Now.Any(e => e.NeedsAttention),
            nextUp,
            board.Pomodoro,
            board.Pomodoro.ItemId is { } pid ? board.Find(pid)?.Title : null,
            GroupMissing: false);
    }

    private static GroupTabViewModel Selected(GroupTabViewModel tab, bool selected)
    {
        tab.IsSelected = selected;
        return tab;
    }

    private static CardViewModel ToCard(AgendaEntry e, DateTimeOffset now)
    {
        var item = e.Item;
        var meta = new List<string>();
        if (item.Parent is { Sequential: true } parent)
        {
            meta.Add($"Step {parent.Children.ToList().IndexOf(item) + 1} of {parent.Children.Count}");
        }

        if (e.State == ItemState.Waiting && e.WakeAt is { } wake)
        {
            meta.Add($"back {RelativeTime.Format(wake, now)}");
        }

        if (item.Deadline is { } deadline)
        {
            meta.Add(e.IsOverdue ? $"overdue {RelativeTime.Format(deadline, now)}" : $"due {RelativeTime.Format(deadline, now)}");
        }

        if (item.Notes.Count > 0)
        {
            meta.Add(item.Notes.Count == 1 ? "1 note" : $"{item.Notes.Count} notes");
        }

        return new CardViewModel
        {
            Id = item.Id,
            Title = item.Title,
            Breadcrumb = string.Join(" › ", e.Breadcrumb),
            Meta = string.Join(" · ", meta),
            PriorityColor = PriorityPalette.ColorOf(e.EffectivePriority),
            PriorityLabel = e.EffectivePriority.ToString(),
            NeedsAttention = e.NeedsAttention,
            ReminderId = e.DueReminder?.Id,
            ReminderMessage = e.DueReminder?.Message,
            IsWaiting = e.State == ItemState.Waiting && !e.NeedsAttention,
            CanComplete = e.State == ItemState.Actionable,
            LastNote = item.Notes.Count > 0 ? item.Notes[^1].Text : null,
        };
    }

    private static string WorkstreamSummary(OverviewEntry o, DateTimeOffset now)
    {
        var bits = new List<string> { $"{o.DoneLeaves}/{o.TotalLeaves} done" };
        if (o.ActionableCount > 0)
        {
            bits.Add($"{o.ActionableCount} now");
        }

        if (o.WaitingCount > 0)
        {
            bits.Add($"{o.WaitingCount} waiting");
        }

        if (o.NextWakeAt is { } w)
        {
            bits.Add($"next {RelativeTime.Format(w, now)}");
        }

        return string.Join(" · ", bits);
    }
}

internal static class TaskSchedulerExtensions
{
    public static TaskScheduler FromCurrentSynchronizationContextOrDefault() =>
        SynchronizationContext.Current is null ? TaskScheduler.Default : TaskScheduler.FromCurrentSynchronizationContext();
}




