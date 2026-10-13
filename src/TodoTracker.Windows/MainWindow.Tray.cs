using System.Windows;
using System.Windows.Interop;

namespace TodoTracker.Windows;

/// <summary>
/// Hiding the sidebar (from its menu or the tray icon) gives its screen edge back; it keeps running in the
/// notification area, also after a restart, until it's shown again (tray icon, Ctrl+Alt+Space, starting it again).
/// </summary>
public partial class MainWindow
{
    /// <summary>Hidden by the user: nothing docks it again (display changes, Explorer restarting) until it's shown.</summary>
    private bool _hiddenByUser;

    internal bool IsSidebarShown => IsVisible;

    /// <summary>Whether Ctrl+Alt+Space brings it back (another app may own the shortcut).</summary>
    internal bool HasHotKey => _hotKey?.IsRegistered == true;

    /// <summary>Hidden from its own menu: the tray icon says where it went.</summary>
    internal event EventHandler? HiddenFromMenu;

    internal void HideSidebar()
    {
        _hiddenByUser = true;
        ReleaseScreenEdge();
        Hide();
        UpdateFullScreenWatch();
        if (!Placement.Hidden)
        {
            UpdatePreferences(p => p with { Hidden = true });
        }
    }

    /// <summary>Starts in the notification area (it was hidden when the app last ran): set up, but not shown or docked.</summary>
    internal void StartHidden()
    {
        _hiddenByUser = true;
        new WindowInteropHelper(this).EnsureHandle();
    }

    internal void ShowSidebar()
    {
        if (_hiddenByUser || !IsVisible)
        {
            _hiddenByUser = false;
            if (Placement.Hidden)
            {
                UpdatePreferences(p => p with { Hidden = false });
            }

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

    private void HideFromMenu()
    {
        HideSidebar();
        HiddenFromMenu?.Invoke(this, EventArgs.Empty);
    }
}
