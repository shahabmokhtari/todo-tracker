using System.Windows.Threading;
using TodoTracker.Desktop;

namespace TodoTracker.Windows;

/// <summary>
/// A full-screen window on the docked sidebar's monitor (Remote Desktop, a video, a slide show) ignores the screen edge
/// the sidebar reserves. Instead of covering part of it, the sidebar goes behind it; a full-screen Remote Desktop
/// session the user is in can instead be fitted next to the sidebar ("Make room for full-screen Remote Desktop").
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan FitCheckDelay = TimeSpan.FromSeconds(1);
    private const int FitChecks = 3;
    private readonly RoomMaker _room = new();
    private FullScreenWatcher? _fullScreen;

    /// <summary>The monitor being watched (null: none).</summary>
    private PlacementBounds? _watched;

    private bool _quitting;

    /// <summary>The full-screen window the sidebar sits behind (0: none).</summary>
    private long _coveredBy;

    /// <summary>Every stacking change goes through here, so nothing puts the sidebar back over a full-screen window.</summary>
    private void ApplyTopmost()
    {
        if (_coveredBy != 0 && !_vm.IsPeeking)
        {
            Topmost = false;

            // Just brought back (tray, hotkey) and in use: it stays in front until the user clicks the full-screen window.
            if (!IsActive)
            {
                _appBar.PutBehind(_coveredBy);
            }
        }
        else
        {
            Topmost = Placement.IsTopmost;
        }
    }

    private void StartWatchingFullScreen()
    {
        _fullScreen = new FullScreenWatcher(Dispatcher, _appBar.Handle, OnCoverChanged);
        _appBar.FullScreenNotified += (_, _) => _fullScreen.EvaluateSoon();

        // Collapsed, expanded, other edge, DPI or taskbar change: fitted sessions follow the room.
        _appBar.Docked += (_, _) =>
        {
            if (!_quitting)
            {
                UpdateFullScreenWatch();
            }
        };
        // Brought back over a full-screen window and then left (taskbar, desktop, another monitor): behind it again.
        Deactivated += (_, _) =>
        {
            if (_coveredBy != 0)
            {
                ApplyTopmost();
            }
        };
        Closed += (_, _) => _fullScreen.Dispose();
    }

    /// <summary>Watches the monitor it's docked on, while it's docked and shown.</summary>
    private void UpdateFullScreenWatch()
    {
        if (_fullScreen is null || _quitting)
        {
            return;
        }

        var monitor = !_hiddenByUser && Placement.Mode == PlacementMode.Docked && _appBar.DockedMonitor is { } docked
            ? new PlacementBounds(docked.Bounds.X, docked.Bounds.Y, docked.Bounds.Width, docked.Bounds.Height)
            : null;
        if (monitor != _watched)
        {
            // Hidden, floating, or on another monitor: the room made next to it isn't needed there any more.
            RestoreRoom();
            _watched = monitor;
        }

        if (monitor is not null && DesktopSidebarHost.WorkAreaOf(monitor) is { } room)
        {
            // Docked again (collapsed, opened, other edge): fitted sessions follow the room next to it.
            foreach (var step in _room.Resize(room, FullScreenWatcher.BoundsOf))
            {
                DesktopSidebarHost.MoveOtherWindow(step.Window, step.Bounds);
            }
        }

        _fullScreen.Watch(monitor);
    }

    private void OnCoverChanged(CoverResult cover, bool foreground)
    {
        if (cover.Window != 0 && _room.IsPending(cover.Window))
        {
            // Being fitted next to the sidebar (the client hasn't resized yet): it stays on top meanwhile.
            _coveredBy = 0;
        }
        else if (cover.Kind == Coverage.RemoteDesktop && foreground && Placement.MakeRoomForRemoteDesktop && TryMakeRoom(cover.Window))
        {
            _coveredBy = 0;
        }
        else
        {
            _coveredBy = cover.Window;
        }

        ApplyTopmost();
    }

    /// <summary>
    /// Resizes the session the user is in to the room next to the sidebar - once per window, and only while it's in
    /// front, so a session in the background is never touched. A client that goes back to full screen is left alone.
    /// </summary>
    private bool TryMakeRoom(long window)
    {
        if (_appBar.DockedMonitor is not { } docked)
        {
            return false;
        }

        var monitor = new PlacementBounds(docked.Bounds.X, docked.Bounds.Y, docked.Bounds.Width, docked.Bounds.Height);
        if (FullScreenWatcher.BoundsOf(window) is not { } current || DesktopSidebarHost.WorkAreaOf(monitor) is not { } room
            || _room.Plan(window, current, monitor, room) is not { } target)
        {
            return false;
        }

        if (!DesktopSidebarHost.MoveOtherWindow(window, target))
        {
            // E.g. the client runs elevated: Windows doesn't let a normal app move it.
            _room.Failed(window);
            return false;
        }

        // The resize is asynchronous: a busy client gets a few seconds before it counts as refusing.
        var tries = 0;
        var check = new DispatcherTimer { Interval = FitCheckDelay };
        check.Tick += (_, _) =>
        {
            tries++;
            if (_quitting || FullScreenWatcher.BoundsOf(window) is not { } now)
            {
                check.Stop();
                return;
            }

            var result = _room.Check(window, now, lastTry: tries >= FitChecks);
            if (result != FitCheck.Waiting)
            {
                check.Stop();

                // Fitted: the sidebar stays on top next to it. Refused: it goes behind the session instead.
                _fullScreen?.Refresh();
            }
        };
        check.Start();
        return true;
    }

    /// <summary>
    /// Fitted sessions go back to full screen (on the monitor they're on now: its size may have changed since): the
    /// sidebar is hidden, floats, moves to another monitor, or quits.
    /// </summary>
    private void RestoreRoom()
    {
        foreach (var step in _room.Restore(FullScreenWatcher.BoundsOf))
        {
            DesktopSidebarHost.MoveOtherWindow(step.Window, DesktopSidebarHost.MonitorBoundsOf(step.Window) ?? step.Bounds);
        }
    }

    /// <summary>Quitting: stop watching first, so nothing is fitted again while notes are saved or after it's gone.</summary>
    private void StopFullScreen()
    {
        _quitting = true;
        _fullScreen?.Watch(null);
        _watched = null;
        RestoreRoom();
    }

    private void SetMakeRoomForRemoteDesktop(bool value)
    {
        UpdatePreferences(p => p with { MakeRoomForRemoteDesktop = value });
        if (!value)
        {
            RestoreRoom();
        }

        _fullScreen?.Refresh();
    }

    /// <summary>Changes a preference in the placement and in the docked choice waiting to be applied, then saves it.</summary>
    private void UpdatePreferences(Func<WindowPlacement, WindowPlacement> change)
    {
        Placement = change(Placement);
        _preferred = _preferred is { } preferred ? change(preferred) : null;
        Persist();
    }
}
