using System.Windows;

namespace TodoTracker.Windows;

/// <summary>Hiding the sidebar (from the tray icon) gives its screen edge back; showing it docks it again.</summary>
public partial class MainWindow
{
    /// <summary>Hidden from the tray: nothing docks it again (display changes, Explorer restarting) until it's shown.</summary>
    private bool _hiddenByUser;

    internal bool IsSidebarShown => IsVisible;

    internal void HideSidebar()
    {
        _hiddenByUser = true;
        ReleaseScreenEdge();
        Hide();
    }

    internal void ShowSidebar()
    {
        if (_hiddenByUser || !IsVisible)
        {
            _hiddenByUser = false;
            Show();
            // The docked choice still waiting to be applied (docking failed earlier) wins over the floating fallback.
            ApplyPlacement(_preferred ?? Placement, isFallbackRetry: true, persist: false);
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }
}
