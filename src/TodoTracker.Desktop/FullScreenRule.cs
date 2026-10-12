namespace TodoTracker.Desktop;

/// <summary>A visible, not minimized top-level window, as the full-screen check sees it (physical pixels).</summary>
/// <param name="IsOwn">One of this app's windows (the sidebar, a break screen, a menu).</param>
/// <param name="IsToolWindow">A tool window (palettes, a Remote Desktop connection bar): never "the" window in front.</param>
public sealed record TopWindow(
    long Handle,
    string ClassName,
    string ProcessName,
    PlacementBounds Bounds,
    bool IsOwn = false,
    bool IsToolWindow = false,
    bool IsTopmost = false);

public enum Coverage
{
    /// <summary>Nothing full screen in front on the sidebar's monitor: the docked sidebar stays on top.</summary>
    Clear,

    /// <summary>A full-screen window is in front on the sidebar's monitor: the sidebar goes behind it.</summary>
    Covered,

    /// <summary>Like <see cref="Covered"/>, by a Remote Desktop client (which can be fitted next to the sidebar).</summary>
    RemoteDesktop,
}

/// <param name="Window">The covering window; 0 when <see cref="Coverage.Clear"/>.</param>
public sealed record CoverResult(Coverage Kind, long Window)
{
    public static CoverResult Clear { get; } = new(Coverage.Clear, 0);
}

/// <summary>
/// Whether a full-screen window (Remote Desktop, a video, a slide show, a game) has taken over the docked sidebar's
/// monitor. Full-screen windows ignore the screen edge the sidebar reserves, so a sidebar left on top would cover part
/// of them.
/// </summary>
public static class FullScreenRule
{
    /// <summary>The top-level window of the Remote Desktop clients (mstsc and Windows App's msrdc).</summary>
    public const string RemoteDesktopClass = "TscShellContainerClass";

    private const double Slack = 2;

    private static readonly HashSet<string> RemoteDesktopProcesses = new(StringComparer.OrdinalIgnoreCase) { "mstsc", "msrdc" };

    /// <summary>
    /// The programs (process names) treated as Remote Desktop clients: <paramref name="configured"/> (comma-separated,
    /// from <c>TODOTRACKER_REMOTE_DESKTOP_CLIENTS</c>) or, when empty, mstsc and msrdc (Windows App).
    /// </summary>
    public static IReadOnlySet<string> RemoteDesktopClients(string? configured)
    {
        var names = (configured ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return names.Length == 0 ? RemoteDesktopProcesses : new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The desktop, taskbars, Start, task view and Alt+Tab: never a full-screen app.</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "XamlExplorerHostIslandWindow",
        "MultitaskingViewFrame",
        "TaskSwitcherWnd",
        "ForegroundStaging",
        "Windows.UI.Core.CoreWindow",
        "TopLevelWindowForOverflowXamlIsland",
    };

    /// <summary>
    /// Walks the windows from the front: the first one that fills <paramref name="monitor"/> covers the sidebar,
    /// unless a big window (half the monitor or more, like a maximized app) is in front of it - the user is working
    /// there, next to the sidebar. Small windows, tool windows and always-on-top widgets in front don't count.
    /// </summary>
    /// <param name="zOrder">Top-level windows, frontmost first.</param>
    /// <param name="remoteDesktopClients">See <see cref="RemoteDesktopClients"/>; null: mstsc and msrdc.</param>
    public static CoverResult Evaluate(IEnumerable<TopWindow> zOrder, PlacementBounds monitor, IReadOnlySet<string>? remoteDesktopClients = null)
    {
        ArgumentNullException.ThrowIfNull(zOrder);
        ArgumentNullException.ThrowIfNull(monitor);
        var clients = remoteDesktopClients ?? RemoteDesktopProcesses;
        var area = monitor.Width * monitor.Height;
        foreach (var window in zOrder)
        {
            if (window.IsOwn || window.IsToolWindow || ShellClasses.Contains(window.ClassName))
            {
                continue;
            }

            var overlap = window.Bounds.IntersectionArea(monitor);
            if (overlap <= 0)
            {
                continue;
            }

            if (Fills(window.Bounds, monitor))
            {
                return new CoverResult(IsRemoteDesktop(window, clients) ? Coverage.RemoteDesktop : Coverage.Covered, window.Handle);
            }

            if (!window.IsTopmost && overlap >= area / 2)
            {
                return CoverResult.Clear;
            }
        }

        return CoverResult.Clear;
    }

    private static bool IsRemoteDesktop(TopWindow window, IReadOnlySet<string> clients) =>
        window.ClassName == RemoteDesktopClass && clients.Contains(window.ProcessName);

    /// <summary>Whether <paramref name="window"/> covers all of <paramref name="monitor"/> (a couple of pixels of slack).</summary>
    public static bool Fills(PlacementBounds window, PlacementBounds monitor)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(monitor);
        return window.Left <= monitor.Left + Slack && window.Top <= monitor.Top + Slack
            && window.Right >= monitor.Right - Slack && window.Bottom >= monitor.Bottom - Slack;
    }

    /// <summary>Same rectangle, give or take a couple of pixels.</summary>
    public static bool Near(PlacementBounds a, PlacementBounds b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return Math.Abs(a.Left - b.Left) <= Slack && Math.Abs(a.Top - b.Top) <= Slack
            && Math.Abs(a.Right - b.Right) <= Slack && Math.Abs(a.Bottom - b.Bottom) <= Slack;
    }
}

/// <summary>Put a fitted window back to <see cref="Bounds"/> (its monitor: full screen again).</summary>
public sealed record RestoreStep(long Window, PlacementBounds Bounds);

/// <summary>What a check after fitting a window found.</summary>
public enum FitCheck
{
    /// <summary>It's at its new size (or isn't being fitted).</summary>
    Fitted,

    /// <summary>Still full screen: the client hasn't handled the resize yet; check again.</summary>
    Waiting,

    /// <summary>Still full screen at the last check: it won't be resized; left alone from now on.</summary>
    Refused,
}

/// <summary>
/// "Make room for full-screen Remote Desktop": a full-screen Remote Desktop window on the docked sidebar's monitor is
/// resized to the room next to the sidebar (the monitor's work area) - once per window, so a client (or user) that
/// goes back to full screen isn't fought over. Fitted windows are put back to full screen when the room isn't needed
/// any more (the sidebar hidden, floating, moved, or the app quitting).
/// </summary>
public sealed class RoomMaker
{
    private readonly HashSet<long> _tried = [];
    private readonly HashSet<long> _gaveUp = [];
    private readonly HashSet<long> _pending = [];
    private readonly Dictionary<long, (PlacementBounds Monitor, PlacementBounds Room)> _fitted = [];

    /// <summary>Where to put <paramref name="window"/>; null: leave it alone (the sidebar goes behind it instead).</summary>
    /// <param name="current">Where the window is now: only a session filling just this monitor is fitted (one spanning
    /// several monitors would be squeezed onto one).</param>
    public PlacementBounds? Plan(long window, PlacementBounds current, PlacementBounds monitor, PlacementBounds room)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(room);
        if (!FullScreenRule.Near(current, monitor) || _gaveUp.Contains(window) || !_tried.Add(window))
        {
            return null;
        }

        _fitted[window] = (monitor, room);
        _pending.Add(window);
        return room;
    }

    /// <summary>
    /// A moment after fitting (the resize is asynchronous: a busy client may take a while). Still full screen: Waiting,
    /// or on the <paramref name="lastTry"/> Refused (and never tried again).
    /// </summary>
    public FitCheck Check(long window, PlacementBounds now, bool lastTry)
    {
        ArgumentNullException.ThrowIfNull(now);
        if (!_fitted.TryGetValue(window, out var fitted))
        {
            return FitCheck.Fitted;
        }

        if (!FullScreenRule.Fills(now, fitted.Monitor))
        {
            _pending.Remove(window);
            return FitCheck.Fitted;
        }

        if (!lastTry)
        {
            return FitCheck.Waiting;
        }

        Failed(window);
        return FitCheck.Refused;
    }

    /// <summary>
    /// Resized but not seen at its new size yet: it doesn't count as covering the sidebar (which would otherwise drop
    /// behind it for a moment). Once seen fitted, full screen again means the user chose it.
    /// </summary>
    public bool IsPending(long window) => _pending.Contains(window);

    /// <summary>The window couldn't be resized (or wouldn't stay resized): never tried again.</summary>
    public void Failed(long window)
    {
        _fitted.Remove(window);
        _pending.Remove(window);
        _gaveUp.Add(window);
    }

    /// <summary>
    /// The room changed (the sidebar collapsed to its strip, opened again, or moved to the other edge): fitted windows
    /// still where they were put move into the new room. Ones moved or resized since are let go.
    /// </summary>
    public IReadOnlyList<RestoreStep> Resize(PlacementBounds room, Func<long, PlacementBounds?> current)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(current);
        var steps = new List<RestoreStep>();
        foreach (var (window, fitted) in _fitted.ToList())
        {
            if (FullScreenRule.Near(fitted.Room, room))
            {
                continue;
            }

            // Pending: the earlier move is still on its way (it would land in the old room).
            if (current(window) is { } now && (_pending.Contains(window) || FullScreenRule.Near(now, fitted.Room)))
            {
                _fitted[window] = (fitted.Monitor, room);
                steps.Add(new RestoreStep(window, room));
            }
            else
            {
                _fitted.Remove(window);
                _pending.Remove(window);
            }
        }

        return steps;
    }

    /// <summary>
    /// The fitted windows still where they were put (gone, moved or resized ones are left alone), and ones whose move
    /// is still on its way (the restore is queued after it, so it wins), to put back full screen. Afterwards nothing is
    /// remembered except the windows that refused.
    /// </summary>
    /// <param name="current">A window's bounds now; null when it's gone.</param>
    public IReadOnlyList<RestoreStep> Restore(Func<long, PlacementBounds?> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var steps = _fitted
            .Where(f => current(f.Key) is { } now && (_pending.Contains(f.Key) || FullScreenRule.Near(now, f.Value.Room)))
            .Select(f => new RestoreStep(f.Key, f.Value.Monitor))
            .ToList();
        _fitted.Clear();
        _pending.Clear();
        _tried.Clear();
        return steps;
    }
}
