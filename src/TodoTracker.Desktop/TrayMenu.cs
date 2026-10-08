namespace TodoTracker.Desktop;

/// <summary>An entry of the tray icon's menu: <see cref="Id"/> says what it does; null id: a separator.</summary>
public sealed record TrayItem(string? Id, string Label = "", bool Enabled = true)
{
    public static TrayItem Separator { get; } = new(Id: null);
}

/// <summary>
/// The notification-area (tray) icon: what its menu offers and what its tooltip says, worked out from the sidebar's
/// state so the menu always matches what's going on (focus running, a task being timed, the sidebar hidden).
/// </summary>
public static class TrayMenu
{
    public const string Open = "open";
    public const string Add = "add";
    public const string StartFocus = "focus-start";
    public const string PauseFocus = "focus-pause";
    public const string ResumeFocus = "focus-resume";
    public const string StopTimer = "timer-stop";
    public const string ToggleSidebar = "sidebar";
    public const string Settings = "settings";
    public const string Quit = "quit";

    /// <summary>The tooltip limit of the Windows notification area (characters).</summary>
    public const int MaxTooltip = 127;

    public static IReadOnlyList<TrayItem> Items(SidebarViewModel vm, bool sidebarShown)
    {
        ArgumentNullException.ThrowIfNull(vm);
        var items = new List<TrayItem>
        {
            new(Open, "Open Todo Tracker"),
            new(Add, "Add a task…"),
            TrayItem.Separator,
        };

        if (vm.ShowFocusTimer)
        {
            var pomodoro = vm.Pomodoro;
            var what = pomodoro.IsBreak ? "break" : "focus";
            items.Add(pomodoro.IsIdle
                ? new TrayItem(StartFocus, vm.Focus is { } focus ? $"Focus on “{Short(focus.Title)}”" : "Start a focus session")
                : pomodoro.IsPaused
                    ? new TrayItem(ResumeFocus, $"Resume {what} ({pomodoro.TimeText} left)")
                    : new TrayItem(PauseFocus, $"Pause {what} ({pomodoro.TimeText} left)"));
        }

        if (vm.Timer.IsRunning)
        {
            items.Add(new TrayItem(StopTimer, $"Stop timing “{Short(vm.Timer.Title)}” ({vm.Timer.TimeText})"));
        }

        items.Add(TrayItem.Separator);
        items.Add(new TrayItem(ToggleSidebar, sidebarShown ? "Hide the sidebar" : "Show the sidebar"));
        items.Add(new TrayItem(Settings, "Settings…"));
        items.Add(TrayItem.Separator);
        items.Add(new TrayItem(Quit, "Quit Todo Tracker"));
        return items;
    }

    /// <summary>What's going on, at a glance: how much is waiting for you, the focus timer, a reminder.</summary>
    public static string Tooltip(SidebarViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        var parts = new List<string> { "Todo Tracker", vm.NowCount == 1 ? "1 thing to do now" : $"{vm.NowCount} things to do now" };
        if (vm.HasAttention)
        {
            parts.Add("a reminder is due");
        }

        if (vm.ShowFocusTimer && !vm.Pomodoro.IsIdle)
        {
            parts.Add($"{vm.Pomodoro.Label} {vm.Pomodoro.TimeText}");
        }
        else if (vm.Timer.IsRunning)
        {
            parts.Add($"timing {vm.Timer.Title}");
        }

        return Cut(string.Join(" · ", parts), MaxTooltip);
    }

    private static string Short(string title) => Cut(title, 40);

    /// <summary>At most <paramref name="max"/> characters, with … when cut (never in the middle of an emoji).</summary>
    internal static string Cut(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var end = max - 1;
        if (char.IsLowSurrogate(text[end]))
        {
            end--;
        }

        return text[..end] + "…";
    }
}
