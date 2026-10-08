using System.Windows;

namespace TodoTracker.Windows;

/// <summary>Hiding the sidebar (from the tray icon) gives its screen edge back; showing it docks it again.</summary>
public partial class MainWindow
{
    internal bool IsSidebarShown => IsVisible;

    internal void HideSidebar()
    {
        ReleaseScreenEdge();
        Hide();
    }

    internal void ShowSidebar()
    {
        if (!IsVisible)
        {
            Show();
            ApplyPlacement(Placement, isFallbackRetry: false, persist: false);
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }
}
