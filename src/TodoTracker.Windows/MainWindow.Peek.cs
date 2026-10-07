using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TodoTracker.Desktop;

namespace TodoTracker.Windows;

/// <summary>
/// Collapsed to a strip, the sidebar opens while the pointer rests on it and closes when the pointer leaves: over the
/// other windows, without giving back or taking more of the screen edge (the reserved width stays the strip's).
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan PeekOpenDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PeekCloseDelay = TimeSpan.FromMilliseconds(450);
    private readonly DispatcherTimer _peekTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private DateTime _hoverSince;
    private DateTime? _outsideSince;
    private bool _peekGrewLeft;
    private bool _raisedForPeek;
    private double _peekWidthPixels;

    private void InitPeek()
    {
        MouseEnter += (_, _) =>
        {
            if (_vm.IsCompact && WindowState == WindowState.Normal && !_peekTimer.IsEnabled)
            {
                _hoverSince = DateTime.UtcNow;
                _outsideSince = null;
                _peekTimer.Start();
            }
        };
        _peekTimer.Tick += (_, _) => PeekTick();
        Deactivated += (_, _) =>
        {
            if (_vm.IsPeeking && !_appBar.ContainsCursor())
            {
                EndPeek();
            }
        };
    }

    // Polls the pointer rather than trusting MouseLeave, which also fires when the window resizes under it or a
    // menu opens from it.
    private void PeekTick()
    {
        if (!_vm.IsCompact || WindowState != WindowState.Normal)
        {
            _peekTimer.Stop();
            EndPeek();
            return;
        }

        var inside = _appBar.ContainsCursor();
        var now = DateTime.UtcNow;
        if (!_vm.IsPeeking)
        {
            if (!inside)
            {
                _peekTimer.Stop();
            }
            else if (now - _hoverSince >= PeekOpenDelay)
            {
                StartPeek();
            }

            return;
        }

        if (_appBar.WindowBoundsPixels().Width < _peekWidthPixels * 0.9)
        {
            // Docked again meanwhile (the taskbar or a display changed): it's the strip again.
            _peekTimer.Stop();
            _vm.IsPeeking = false;
            RestoreAfterPeek();
            return;
        }

        if (inside || IsBusy())
        {
            _outsideSince = null;
            return;
        }

        _outsideSince ??= now;
        if (now - _outsideSince >= PeekCloseDelay)
        {
            _peekTimer.Stop();
            EndPeek();
        }
    }

    // Typing in the sidebar, or a menu open from it: it stays open until that's done (or another window is used).
    private bool IsBusy() => Mouse.Captured is not null || (IsActive && Keyboard.FocusedElement is TextBox);

    private void StartPeek()
    {
        var strip = _appBar.WindowBoundsPixels();
        if (DesktopSidebarHost.MonitorOf(strip) is not { } monitor)
        {
            return;
        }

        var docked = Placement.Mode == PlacementMode.Docked && _appBar.IsDocked ? Placement.Edge : (DockEdge?)null;
        var area = docked is null
            ? monitor.WorkAreaPixels
            : new PlacementBounds(monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.Width, monitor.Bounds.Height);
        var width = docked is null && Placement.Floating is { } floating ? floating.Width : Placement.DockWidth * monitor.Scale;
        var peek = PlacementMath.PeekBounds(strip, width, area, docked);
        _peekGrewLeft = peek.Left < strip.Left;

        _peekWidthPixels = peek.Width;
        _vm.IsPeeking = true;
        CollapseButton.ToolTip = "Keep the sidebar open";
        System.Windows.Automation.AutomationProperties.SetName(CollapseButton, "Keep the sidebar open");
        if (!Topmost)
        {
            // Over the other windows while it's open.
            _raisedForPeek = true;
            Topmost = true;
        }

        _appBar.MoveWindowPixels(peek);
    }

    private void EndPeek()
    {
        if (!_vm.IsPeeking)
        {
            return;
        }

        _vm.IsPeeking = false;
        RestoreAfterPeek();

        // Back to the strip, on the side it opened from (it may have been dragged meanwhile).
        var current = _appBar.WindowBoundsPixels();
        var stripWidth = Math.Round(CompactWidth * (DesktopSidebarHost.MonitorOf(current)?.Scale ?? 1));
        _appBar.MoveWindowPixels(current with { Left = _peekGrewLeft ? current.Right - stripWidth : current.Left, Width = stripWidth });
    }

    /// <summary>Undoes what opening did besides the size (also when it's kept open: the placement code sizes it then).</summary>
    private void RestoreAfterPeek()
    {
        _peekTimer.Stop();
        CollapseButton.ToolTip = "Collapse to a thin strip";
        System.Windows.Automation.AutomationProperties.SetName(CollapseButton, "Collapse");
        if (_raisedForPeek)
        {
            // What the placement says now ("Always on top" may have been switched on while it was open).
            Topmost = Placement.IsTopmost;
            _raisedForPeek = false;
        }
    }
}
