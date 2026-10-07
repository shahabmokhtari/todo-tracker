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

    private readonly Func<Task> _skip;
    private PomodoroState? _state;
    private DateTimeOffset? _until;
    private DateTimeOffset? _dismissed;

    public BreakScreenViewModel(Func<Task> skip)
    {
        _skip = skip;
    }

    [ObservableProperty]
    public partial bool IsShown { get; set; }

    [ObservableProperty]
    public partial bool IsLong { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = "Time for a break";

    [ObservableProperty]
    public partial string Tip { get; set; } = Tips[0];

    [ObservableProperty]
    public partial string TimeText { get; set; } = "0:00";

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

    internal void Update(PomodoroState state, DateTimeOffset now)
    {
        _state = state;
        Tick(now);
    }

    internal void Tick(DateTimeOffset now)
    {
        var due = Due(_state, now);
        if (due is not { } d || d.Until == _dismissed)
        {
            IsShown = false;
            return;
        }

        if (_until != d.Until)
        {
            _until = d.Until;
            IsLong = d.Long;
            Title = d.Long ? "Time for a longer break" : "Time for a break";
            Tip = Tips[(int)(Math.Abs(d.Until.ToUnixTimeSeconds()) % Tips.Length)];
        }

        var seconds = (int)Math.Ceiling((d.Until - now).TotalSeconds);
        TimeText = $"{seconds / 60}:{seconds % 60:00}";
        IsShown = true;
    }

    /// <summary>"I'm taking it": the screen goes away; the break timer keeps running.</summary>
    [RelayCommand]
    private void TakeBreak()
    {
        _dismissed = _until;
        IsShown = false;
    }

    /// <summary>Back to work now: the break ends.</summary>
    [RelayCommand]
    private async Task SkipBreak()
    {
        _dismissed = _until;
        IsShown = false;
        await _skip().ConfigureAwait(true);
    }
}
