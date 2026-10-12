using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using TodoTracker.Desktop;

namespace TodoTracker.Windows;

/// <summary>
/// Watches the docked sidebar's monitor for a full-screen window (Remote Desktop, a video, a slide show) in front, and
/// says when that changes. Re-checked when the foreground window changes, when the shell reports a full-screen app,
/// and every half second (a window going full screen in place doesn't change the foreground).
/// </summary>
internal sealed unsafe partial class FullScreenWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private const int WsExTopmost = 0x00000008;
    private const int WsChild = 0x40000000;
    private const int DwmwaCloaked = 14;
    private const uint GaRootOwner = 3;

    /// <summary>Which programs are Remote Desktop clients (system tests point this at a stand-in).</summary>
    private static readonly IReadOnlySet<string> Clients = FullScreenRule.RemoteDesktopClients(Environment.GetEnvironmentVariable("TODOTRACKER_REMOTE_DESKTOP_CLIENTS"));

    private readonly Dispatcher _dispatcher;
    private readonly Action<CoverResult, bool> _changed;
    private readonly DispatcherTimer _poll;
    private readonly WinEventProc _onForeground;
    private readonly int _ownProcess = Environment.ProcessId;
    private readonly IntPtr _sidebar;
    private IntPtr _hook;
    private PlacementBounds? _monitor;
    private (CoverResult Cover, bool Foreground)? _last = (CoverResult.Clear, false);
    private bool _queued;

    /// <param name="sidebar">The sidebar's own window (never "in front of" itself).</param>
    /// <param name="changed">What covers the monitor now, and whether that window is the one the user is in (foreground).</param>
    public FullScreenWatcher(Dispatcher dispatcher, IntPtr sidebar, Action<CoverResult, bool> changed)
    {
        _dispatcher = dispatcher;
        _sidebar = sidebar;
        _changed = changed;
        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Evaluate(), dispatcher);
        _poll.Stop();

        // Kept in a field: the hook calls it for as long as it's installed.
        _onForeground = (_, _, _, _, _, _, _) => EvaluateSoon();
    }

    /// <summary>Watch <paramref name="monitor"/> (physical pixels), or stop watching (null: hidden, floating).</summary>
    public void Watch(PlacementBounds? monitor)
    {
        _monitor = monitor;
        if (monitor is null)
        {
            _poll.Stop();
            if (_hook != IntPtr.Zero)
            {
                UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
            }

            Report(CoverResult.Clear, foreground: false);
            return;
        }

        if (_hook == IntPtr.Zero)
        {
            // Out of context: called on this (the UI) thread's message loop, never inside another process.
            _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, Marshal.GetFunctionPointerForDelegate(_onForeground), 0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
        }

        _poll.Start();
        Evaluate();
    }

    /// <summary>Check again shortly (several triggers in a row are folded into one check).</summary>
    public void EvaluateSoon()
    {
        if (_queued || _monitor is null)
        {
            return;
        }

        _queued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _queued = false;
            Evaluate();
        });
    }

    /// <summary>Check again shortly and report the result even when it looks unchanged (after resizing the covering window).</summary>
    public void Refresh()
    {
        _last = null;
        EvaluateSoon();
    }

    public void Dispose()
    {
        _monitor = null;
        _poll.Stop();
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    /// <summary>The window's bounds in physical pixels; null when it's gone.</summary>
    public static PlacementBounds? BoundsOf(long window) =>
        IsWindow(new IntPtr(window)) && GetWindowRect(new IntPtr(window), out var r) ? new PlacementBounds(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : null;

    /// <summary>Whether the window in front is one of this app's (a dialog or menu of the sidebar, the app window).</summary>
    public static bool ForegroundIsOurs()
    {
        var foreground = GetForegroundWindow();
        return foreground != IntPtr.Zero && GetWindowThreadProcessId(foreground, out var pid) != 0 && pid == Environment.ProcessId;
    }

    private void Evaluate()
    {
        if (_monitor is not { } monitor)
        {
            return;
        }

        var cover = FullScreenRule.Evaluate(TopWindows(), monitor, Clients);
        Report(cover, cover.Window != 0 && GetAncestor(GetForegroundWindow(), GaRootOwner) == new IntPtr(cover.Window));
    }

    private void Report(CoverResult cover, bool foreground)
    {
        if (_last == (cover, foreground))
        {
            return;
        }

        _last = (cover, foreground);
        _changed(cover, foreground);
    }

    /// <summary>Visible, not minimized, not cloaked top-level windows, frontmost first.</summary>
    private List<TopWindow> TopWindows()
    {
        var list = new List<IntPtr>();
        var handle = GCHandle.Alloc(list);
        try
        {
            EnumWindows(&OnWindow, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        var windows = new List<TopWindow>(list.Count);
        foreach (var hwnd in list)
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsCloaked(hwnd) || !GetWindowRect(hwnd, out var r))
            {
                continue;
            }

            var style = GetWindowLongW(hwnd, GwlStyle);
            if ((style & WsChild) != 0)
            {
                continue;
            }

            var ex = GetWindowLongW(hwnd, GwlExStyle);
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            var className = ClassName(hwnd);
            var topmost = (ex & WsExTopmost) != 0;
            windows.Add(new TopWindow(
                (long)hwnd,
                className,
                className == FullScreenRule.RemoteDesktopClass ? ProcessName(pid) : string.Empty,
                new PlacementBounds(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
                // The sidebar and its always-on-top screens (breaks, menus). The app window counts like any other app's:
                // maximized over a full-screen session, the user is working there, next to the sidebar.
                IsOwn: hwnd == _sidebar || (pid == _ownProcess && topmost),
                IsToolWindow: (ex & WsExToolWindow) != 0 && (ex & WsExAppWindow) == 0,
                IsTopmost: topmost));
        }

        return windows;
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return string.Empty;
        }
    }

    private static string ClassName(IntPtr hwnd)
    {
        var buffer = stackalloc char[256];
        var length = GetClassNameW(hwnd, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        int cloaked;
        return DwmGetWindowAttribute(hwnd, DwmwaCloaked, &cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    [UnmanagedCallersOnly]
    private static int OnWindow(IntPtr hwnd, IntPtr state)
    {
        ((List<IntPtr>)GCHandle.FromIntPtr(state).Target!).Add(hwnd);
        return 1;
    }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(delegate* unmanaged<IntPtr, IntPtr, int> callback, IntPtr state);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowLongW(IntPtr hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [LibraryImport("user32.dll")]
    private static partial int GetClassNameW(IntPtr hwnd, char* name, int max);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(IntPtr hwnd, int attribute, void* value, int size);

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, IntPtr callback, uint process, uint thread, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(IntPtr hook);
}
