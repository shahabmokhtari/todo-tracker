using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TodoTracker.Core;

namespace TodoTracker.Desktop;

/// <summary>
/// The full-screen break after a focus session (the window itself is the platform's). It shows the moment the session
/// ends, before the background tick switches the timer to the break, and while the break runs. Hidden or skipped, it
/// stays hidden for that break. Mirrors breakState in the web app (wwwroot/js/breaks.js).
/// </summary>
public sealed partial class BreakScreenViewModel : ObservableObject
{
    /// <summary>Clicks and keys this soon after the screen appears are typing still going on, not an answer.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(800);

    // Same tips, in the same order, as the web app (tests/fixtures/breaks.json checks both).
    private static readonly string[] Tips =
    [
        "Stand up and stretch for a minute.",
        "Look at something far away: rest your eyes.",
        "Drink a glass of water.",
        "Take five slow breaths.",
        "Walk around the room.",
        "Roll your shoulders and unclench your jaw.",
        "Step outside or open a window.",
    ];

    /// <summary>How long "Break's over" keeps asking after a break ran out (later, the timer's own Start does).</summary>
    public static readonly TimeSpan OverFor = TimeSpan.FromMinutes(15);

    private readonly Func<Task> _skip;
    private readonly Func<Task> _startNext;
    private PomodoroState? _state;
    private DateTimeOffset? _until;
    private DateTimeOffset? _dismissed;

    // The break this app saw (shown, or hidden with "I'm taking it"): when it runs out, it asks about the next focus.
    private DateTimeOffset? _seen;
    private DateTimeOffset? _dismissedOver;

    public BreakScreenViewModel(Func<Task> skip, Func<Task>? startNext = null)
    {
        _skip = skip;
        _startNext = startNext ?? (() => Task.CompletedTask);
    }

    [ObservableProperty]
    public partial bool IsShown { get; set; }

    /// <summary>"Break's over": the break ran out; Start next focus / Not now (instead of the break's clock).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBreak))]
    public partial bool IsOver { get; set; }

    public bool IsBreak => !IsOver;

    [ObservableProperty]
    public partial bool IsLong { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = "Time for a break";

    [ObservableProperty]
    public partial string Tip { get; set; } = Tips[0];

    [ObservableProperty]
    public partial string TimeText { get; set; } = "0:00";

    /// <summary>"Next: &lt;task&gt;": what Start next focus picks up (empty: no task).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNext))]
    public partial string NextText { get; set; } = string.Empty;

    public bool HasNext => NextText.Length > 0;

    /// <summary>
    /// Whether to ask "Break's over": the break this app saw (<paramref name="seen"/>, its end) ran out by itself, not
    /// long ago, and it wasn't answered with Not now. The timer says when a break ran out (never for a skipped or paused
    /// one); until it has moved on, a running break at its end counts too. Mirrors breakOver in the web app
    /// (tests/fixtures/breaks.json checks both).
    /// </summary>
    public static bool Over(PomodoroState? state, DateTimeOffset now, DateTimeOffset? seen, DateTimeOffset? dismissedOver)
    {
        if (state is null || seen is not { } end || end == dismissedOver || now < end || now - end >= OverFor)
        {
            return false;
        }

        if (state.Phase == PomodoroPhase.Idle)
        {
            return state.BreakEndedAt == end;
        }

        if (!state.IsRunning || state.EndsAt is not { } ends)
        {
            return false; // paused
        }

        // The break itself, run out; or the session before it (both ran out before the timer moved on). Not a new session.
        return state.Phase == PomodoroPhase.Focus ? ends < end : ends == end;
    }

    /// <summary>When the break is over, or null when none is due now.</summary>
    public static (DateTimeOffset Until, bool Long)? Due(PomodoroState? state, DateTimeOffset now)
    {
        if (state is not { IsRunning: true, EndsAt: { } end })
        {
            return null;
        }

        if (state.Phase is PomodoroPhase.ShortBreak or PomodoroPhase.LongBreak)
        {
            return end > now ? (end, state.Phase == PomodoroPhase.LongBreak) : null;
        }

        if (state.Phase == PomodoroPhase.Focus && end <= now)
        {
            // The session just ended: the break the timer is about to start (long every few sessions).
            var settings = state.Settings ?? new PomodoroSettings();
            var isLong = (state.CompletedFocusCount + 1) % settings.FocusesBeforeLongBreak == 0;
            var until = end + settings.DurationOf(isLong ? PomodoroPhase.LongBreak : PomodoroPhase.ShortBreak);
            return until > now ? (until, isLong) : null;
        }

        return null;
    }

    /// <summary>The tip for a break (stable while it lasts): the web app's tipFor, on the break's end in milliseconds.</summary>
    public static string TipFor(DateTimeOffset until)
    {
        var hash = 7;
        foreach (var c in until.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            hash = unchecked((hash * 31) + c);
        }

        return Tips[(int)(Math.Abs((long)hash) % Tips.Length)];
    }

    internal void Update(PomodoroState state, DateTimeOffset now, string? next = null)
    {
        _state = state;
        NextText = string.IsNullOrWhiteSpace(next) ? string.Empty : $"Next: {next}";
        Tick(now);
    }

    internal void Tick(DateTimeOffset now)
    {
        var due = Due(_state, now);
        if (due is { } running)
        {
            _seen = running.Until;
        }

        if (due is { } d && d.Until != _dismissed)
        {
            if (_until != d.Until || IsOver)
            {
                _until = d.Until;
                IsOver = false;
                IsLong = d.Long;
                Title = d.Long ? "Time for a longer break" : "Time for a break";
                Tip = TipFor(d.Until);
            }

            var seconds = (int)Math.Ceiling((d.Until - now).TotalSeconds);
            TimeText = $"{seconds / 60}:{seconds % 60:00}";
            IsShown = true;
            return;
        }

        if (due is null && Over(_state, now, _seen, _dismissedOver))
        {
            if (!IsOver)
            {
                IsOver = true;
                Title = "Break’s over";
                Tip = "Ready for the next one? One small step is enough.";
                TimeText = string.Empty;
            }

            IsShown = true;
            return;
        }

        IsShown = false;
    }

    /// <summary>"I'm taking it" (the break timer keeps running), or "Not now" when the break is over.</summary>
    [RelayCommand]
    private void TakeBreak()
    {
        if (IsOver)
        {
            _dismissedOver = _seen;
        }
        else
        {
            _dismissed = _until;
        }

        IsShown = false;
    }

    /// <summary>Back to work now: the break ends.</summary>
    [RelayCommand]
    private async Task SkipBreak()
    {
        _dismissed = _until;
        _seen = null;
        IsShown = false;
        await _skip().ConfigureAwait(true);
    }

    /// <summary>The next focus session now (during the break, or when it's over): on the last task, else today's top one.</summary>
    [RelayCommand]
    private async Task StartNextFocus()
    {
        _dismissed = _until;
        _dismissedOver = _seen;
        _seen = null;
        IsShown = false;
        await _startNext().ConfigureAwait(true);
    }
}
