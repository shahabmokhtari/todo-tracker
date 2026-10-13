using TodoTracker.Desktop;

namespace TodoTracker.Desktop.Tests;

/// <summary>
/// When a full-screen window (Remote Desktop, a video, a game) takes over the docked sidebar's monitor, the sidebar
/// steps behind it instead of covering part of it; Remote Desktop can be fitted next to the sidebar instead.
/// </summary>
public sealed class FullScreenRuleTests
{
    private static readonly PlacementBounds Left = new(0, 0, 2560, 1440);
    private static readonly PlacementBounds Right = new(2560, -9, 2560, 1440);

    private static TopWindow Window(long handle, PlacementBounds bounds, string className = "Notepad", string process = "notepad") =>
        new(handle, className, process, bounds);

    private static TopWindow Rdp(long handle, PlacementBounds bounds, string process = "msrdc") =>
        new(handle, FullScreenRule.RemoteDesktopClass, process, bounds);

    [Fact]
    public void Nothing_full_screen_leaves_the_sidebar_clear()
    {
        var maximized = Window(1, new PlacementBounds(-8, -8, 2216, 1408));

        Assert.Equal(Coverage.Clear, FullScreenRule.Evaluate([maximized], Left).Kind);
        Assert.Equal(CoverResult.Clear, FullScreenRule.Evaluate([], Left));
    }

    [Fact]
    public void A_full_screen_window_on_the_sidebars_monitor_covers_it()
    {
        var video = Window(7, Left, "Chrome_WidgetWin_1", "chrome");

        var result = FullScreenRule.Evaluate([video], Left);

        Assert.Equal(new CoverResult(Coverage.Covered, 7), result);
    }

    [Fact]
    public void Remote_desktop_clients_are_recognised_by_their_window_class_and_process()
    {
        Assert.Equal(new CoverResult(Coverage.RemoteDesktop, 3), FullScreenRule.Evaluate([Rdp(3, Left)], Left));
        Assert.Equal(Coverage.RemoteDesktop, FullScreenRule.Evaluate([Rdp(3, Left, "mstsc")], Left).Kind);
        Assert.Equal(Coverage.RemoteDesktop, FullScreenRule.Evaluate([Rdp(3, Left, "MSRDC")], Left).Kind);

        // The class alone isn't enough: only the real clients get resized.
        Assert.Equal(Coverage.Covered, FullScreenRule.Evaluate([Rdp(3, Left, "someapp")], Left).Kind);
    }

    [Fact]
    public void Which_programs_count_as_remote_desktop_clients_can_be_changed()
    {
        // System tests use a stand-in client, so a real session on the test machine is never resized.
        var clients = FullScreenRule.RemoteDesktopClients(" tt-fake-rdp , other ");

        Assert.Equal(Coverage.RemoteDesktop, FullScreenRule.Evaluate([Rdp(3, Left, "TT-FAKE-RDP")], Left, clients).Kind);
        Assert.Equal(Coverage.Covered, FullScreenRule.Evaluate([Rdp(3, Left, "msrdc")], Left, clients).Kind);
        Assert.Equal(Coverage.RemoteDesktop, FullScreenRule.Evaluate([Rdp(3, Left, "msrdc")], Left, FullScreenRule.RemoteDesktopClients(null)).Kind);
        Assert.Equal(Coverage.RemoteDesktop, FullScreenRule.Evaluate([Rdp(3, Left, "mstsc")], Left, FullScreenRule.RemoteDesktopClients("  ")).Kind);
    }

    [Fact]
    public void A_full_screen_window_on_another_monitor_does_not_matter_even_when_it_has_focus()
    {
        // The user's setup: a full-screen session on each monitor, the one in front on the other monitor.
        var front = Rdp(1, Right);
        var behind = Rdp(2, Left);

        Assert.Equal(new CoverResult(Coverage.RemoteDesktop, 2), FullScreenRule.Evaluate([front, behind], Left));
        Assert.Equal(new CoverResult(Coverage.RemoteDesktop, 1), FullScreenRule.Evaluate([front, behind], Right));
    }

    [Fact]
    public void A_big_window_in_front_of_the_full_screen_one_brings_the_sidebar_back()
    {
        // Outlook maximized over a full-screen session: the user is working in Outlook, next to the sidebar.
        var outlook = Window(5, new PlacementBounds(-8, -8, 2216, 1408), "rctrl_renwnd32", "outlook");

        Assert.Equal(Coverage.Clear, FullScreenRule.Evaluate([outlook, Rdp(2, Left)], Left).Kind);
    }

    [Fact]
    public void Small_windows_tool_windows_and_always_on_top_widgets_in_front_do_not_count()
    {
        var dialog = Window(4, new PlacementBounds(900, 500, 600, 300));
        var connectionBar = new TopWindow(8, "BBarWindowClass", "msrdc", new PlacementBounds(1790, 0, 665, 32), IsToolWindow: true);
        var bigTool = new TopWindow(9, "Overlay", "x", new PlacementBounds(0, 0, 2000, 1400), IsToolWindow: true);
        var widget = new TopWindow(10, "Widget", "x", new PlacementBounds(0, 0, 2000, 1400), IsTopmost: true);

        Assert.Equal(Coverage.RemoteDesktop, FullScreenRule.Evaluate([dialog, connectionBar, bigTool, widget, Rdp(2, Left)], Left).Kind);
    }

    [Fact]
    public void Our_own_windows_and_the_shell_are_skipped()
    {
        var sidebar = new TopWindow(1, "HwndWrapper[TodoTracker;;1]", "TodoTracker", new PlacementBounds(2200, 0, 360, 1440), IsOwn: true, IsTopmost: true);
        var ourBreakScreen = new TopWindow(2, "HwndWrapper[TodoTracker;;2]", "TodoTracker", Left, IsOwn: true, IsTopmost: true);
        var taskView = new TopWindow(3, "XamlExplorerHostIslandWindow", "explorer", Left);
        var desktop = new TopWindow(4, "Progman", "explorer", Left);
        var workerW = new TopWindow(5, "WorkerW", "explorer", Left);

        Assert.Equal(Coverage.Clear, FullScreenRule.Evaluate([sidebar, ourBreakScreen, taskView, desktop, workerW], Left).Kind);
        Assert.Equal(Coverage.RemoteDesktop, FullScreenRule.Evaluate([sidebar, ourBreakScreen, taskView, Rdp(6, Left), desktop], Left).Kind);
    }

    [Fact]
    public void A_full_screen_window_on_top_of_everything_covers_it_too()
    {
        // Games and slide shows are often always-on-top themselves.
        var slideShow = new TopWindow(3, "screenClass", "powerpnt", Left, IsTopmost: true);

        Assert.Equal(new CoverResult(Coverage.Covered, 3), FullScreenRule.Evaluate([slideShow], Left));
    }

    [Fact]
    public void Fills_allows_a_pixel_of_slack_and_windows_bigger_than_the_monitor()
    {
        Assert.True(FullScreenRule.Fills(new PlacementBounds(1, 0, 2558, 1440), Left));
        Assert.True(FullScreenRule.Fills(new PlacementBounds(-8, -8, 2576, 1456), Left));
        Assert.False(FullScreenRule.Fills(new PlacementBounds(0, 0, 2200, 1440), Left));
        Assert.False(FullScreenRule.Fills(new PlacementBounds(0, 0, 2560, 1392), Left));
    }
}
