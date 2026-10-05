using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using TodoTracker.Desktop;

namespace TodoTracker.Windows;

/// <summary>A physical monitor, in device pixels (as reported by Win32).</summary>
internal sealed record DisplayMonitor(string Id, string DeviceName, string? FriendlyName, bool IsPrimary, Int32Rect Bounds, Int32Rect WorkArea, double Scale)
    : MonitorKey(Id, DeviceName, FriendlyName, IsPrimary, Bounds.Width, Bounds.Height)
{
    /// <summary>Work area in physical pixels: one coordinate space shared by all monitors, whatever their DPI.</summary>
    public PlacementBounds WorkAreaPixels => new(WorkArea.X, WorkArea.Y, WorkArea.Width, WorkArea.Height);
}

/// <summary>
/// Registers the window as a Win32 AppBar on the left or right edge of a chosen monitor, so the shell shrinks the
/// work area and maximized windows never cover the sidebar. Handles per-monitor DPI, display/DPI changes, shell
/// repositioning, and full-screen apps (drops Topmost while a full-screen app is active).
/// </summary>
internal sealed unsafe partial class DesktopSidebarHost : IDisposable
{
    private const int AbeLeft = 0;
    private const int AbeRight = 2;
    private const int AbmNew = 0;
    private const int AbmRemove = 1;
    private const int AbmQueryPos = 2;
    private const int AbmSetPos = 3;
    private const int AbmActivate = 6;
    private const int AbmWindowPosChanged = 9;
    private const int WmActivate = 0x0006;
    private const int WmWindowPosChanged = 0x0047;
    private const int WmDisplayChange = 0x007E;
    private const int WmDpiChanged = 0x02E0;
    private const int AbnPosChanged = 1;
    private const int AbnFullscreenApp = 2;
    private const uint MonitorInfoPrimary = 1;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const string AppBarMessageName = "TodoTracker.Windows.AppBarCallback";
    private const int WmSysCommand = 0x0112;
    private const int ScMaximize = 0xF030;

    private readonly Window _window;
    private HwndSource? _source;
    private IntPtr _handle;
    private uint _callbackMessage;
    private uint _taskbarCreatedMessage;
    private bool _registered;
    private bool _positioning;
    private string? _dockedOn;
    private DispatcherTimer? _displayDebounce;

    public DesktopSidebarHost(Window window)
    {
        _window = window;
    }

    public bool IsDocked => _registered;

    /// <summary>Width in device-independent pixels reserved at the edge.</summary>
    public double WidthInDips { get; set; } = WindowPlacement.DefaultDockWidth;

    public DockEdge Edge { get; set; } = DockEdge.Right;

    /// <summary>Stable id (or legacy device name) of the monitor to dock on; null or disconnected means the primary.</summary>
    public string? MonitorId { get; set; }

    /// <summary>Raised (debounced) after monitors are added, removed, rearranged, or change resolution/scale.</summary>
    public event EventHandler? DisplaysChanged;

    /// <summary>Raised when docking is lost to something outside our control (e.g. the shell refused the AppBar).</summary>
    public event EventHandler? DockFailed;

    /// <summary>Raised when Explorer restarts; every AppBar registration is gone and must be re-created.</summary>
    public event EventHandler? ShellRestarted;

    /// <summary>When true, maximize requests are ignored (a floating sidebar must not cover the whole screen).</summary>
    public bool BlockMaximize { get; set; }

    /// <summary>Hooks window messages; call once the window handle exists, before docking or floating.</summary>
    public void Attach()
    {
        _handle = new WindowInteropHelper(_window).Handle;
        if (_handle == IntPtr.Zero || _source is not null)
        {
            return;
        }

        _callbackMessage = RegisterWindowMessage(AppBarMessageName);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);

        // Plugging monitors in or out sends a burst of WM_DISPLAYCHANGE while the shell settles: react once.
        _displayDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Normal, (_, _) =>
        {
            _displayDebounce!.Stop();
            DisplaysChanged?.Invoke(this, EventArgs.Empty);
        }, _window.Dispatcher);
        _displayDebounce.Stop();
    }

    /// <summary>Current window bounds in physical pixels.</summary>
    public PlacementBounds WindowBoundsPixels()
    {
        GetWindowRect(_handle, out var r);
        return new PlacementBounds(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>Moves/resizes in physical pixels (correct across monitors with different DPI).</summary>
    public void MoveWindowPixels(PlacementBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        SetWindowPos(_handle, IntPtr.Zero, (int)Math.Round(bounds.Left), (int)Math.Round(bounds.Top), (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height), SwpNoZOrder | SwpNoActivate);
    }

    public static IReadOnlyList<DisplayMonitor> Monitors()
    {
        var list = new List<DisplayMonitor>();
        var handle = GCHandle.Alloc(list);
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, &OnMonitor, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        // Primary first: it is the fallback target everywhere. Stable ids/friendly names come from the CCD API.
        var names = DisplayConfig.Query();
        return list
            .Select(m => names.TryGetValue(m.DeviceName, out var n) ? m with { Id = n.DevicePath, FriendlyName = n.FriendlyName, Aliases = n.ClonePaths } : m)
            .OrderByDescending(m => m.IsPrimary)
            .ToList();
    }

    /// <summary>The monitor to dock on: the saved one if connected, else the primary.</summary>
    public DisplayMonitor? TargetMonitor()
    {
        var monitors = Monitors();
        return MonitorIdentity.Resolve(MonitorId, monitors) ?? (monitors.Count > 0 ? monitors[0] : null);
    }

    public void Dock()
    {
        if (_positioning)
        {
            return;
        }

        _positioning = true;
        try
        {
            Attach();
            if (_handle == IntPtr.Zero || TargetMonitor() is not { } monitor)
            {
                return;
            }

            var width = (int)Math.Round(WidthInDips * monitor.Scale);
            if (_registered && !string.Equals(_dockedOn, monitor.Id, StringComparison.OrdinalIgnoreCase))
            {
                // Moving to another monitor (chosen, unplugged, or plugged back in): re-register so the shell
                // gives the old monitor's edge back and recomputes every work area.
                Undock();
            }

            var data = new AppBarData
            {
                cbSize = Marshal.SizeOf<AppBarData>(),
                hWnd = _handle,
                uCallbackMessage = _callbackMessage,
                uEdge = Edge == DockEdge.Left ? AbeLeft : AbeRight,
                // AppBars are positioned against the full monitor; the shell subtracts other bars (taskbar) in QueryPos.
                rc = Edge == DockEdge.Left
                    ? new NativeRect(monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.X + width, monitor.Bounds.Y + monitor.Bounds.Height)
                    : new NativeRect(monitor.Bounds.X + monitor.Bounds.Width - width, monitor.Bounds.Y, monitor.Bounds.X + monitor.Bounds.Width, monitor.Bounds.Y + monitor.Bounds.Height),
            };

            if (!_registered)
            {
                if (SHAppBarMessage(AbmNew, ref data) == 0)
                {
                    // ABM_NEW fails if the shell still has us registered (e.g. a stale entry): clear it and retry once.
                    SHAppBarMessage(AbmRemove, ref data);
                    if (SHAppBarMessage(AbmNew, ref data) == 0)
                    {
                        DockFailed?.Invoke(this, EventArgs.Empty);
                        return;
                    }
                }

                _registered = true;
            }

            _dockedOn = monitor.Id;
            SHAppBarMessage(AbmQueryPos, ref data);
            if (Edge == DockEdge.Left)
            {
                data.rc.Right = data.rc.Left + width;
            }
            else
            {
                data.rc.Left = data.rc.Right - width;
            }

            SHAppBarMessage(AbmSetPos, ref data);

            // Move in device pixels first (correct across monitors with different DPI), then mirror into WPF's DIPs.
            SetWindowPos(_handle, IntPtr.Zero, data.rc.Left, data.rc.Top, data.rc.Right - data.rc.Left, data.rc.Bottom - data.rc.Top, SwpNoZOrder | SwpNoActivate);
            _window.Left = data.rc.Left / monitor.Scale;
            _window.Top = data.rc.Top / monitor.Scale;
            _window.Width = (data.rc.Right - data.rc.Left) / monitor.Scale;
            _window.Height = (data.rc.Bottom - data.rc.Top) / monitor.Scale;
        }
        finally
        {
            _positioning = false;
        }
    }

    /// <summary>Gives the screen space back. Safe to call from any thread (e.g. crash handlers) and repeatedly.</summary>
    public void Undock()
    {
        if (!_registered)
        {
            return;
        }

        _registered = false;
        var data = new AppBarData { cbSize = Marshal.SizeOf<AppBarData>(), hWnd = _handle };
        SHAppBarMessage(AbmRemove, ref data);
    }

    public void Dispose()
    {
        Undock();
        _displayDebounce?.Stop();
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmSysCommand && BlockMaximize && (wParam.ToInt32() & 0xFFF0) == ScMaximize)
        {
            handled = true;
            return IntPtr.Zero;
        }

        if (_taskbarCreatedMessage != 0 && msg == _taskbarCreatedMessage)
        {
            // Explorer restarted (registration lost) - or, on Windows 10+, the primary display's DPI changed
            // (registration kept). Remove unconditionally so the re-dock starts clean in both cases.
            if (_registered)
            {
                _registered = false;
                var stale = new AppBarData { cbSize = Marshal.SizeOf<AppBarData>(), hWnd = _handle };
                SHAppBarMessage(AbmRemove, ref stale);
            }

            _window.Dispatcher.BeginInvoke(() => ShellRestarted?.Invoke(this, EventArgs.Empty));
            return IntPtr.Zero;
        }

        if (msg == WmDisplayChange)
        {
            // Docked or floating: monitors were added/removed/rearranged or changed resolution.
            _displayDebounce?.Stop();
            _displayDebounce?.Start();
            return IntPtr.Zero;
        }

        if (!_registered)
        {
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case WmActivate:
                NotifyShell(AbmActivate);
                return IntPtr.Zero;
            case WmWindowPosChanged:
                NotifyShell(AbmWindowPosChanged);
                return IntPtr.Zero;
            // Not WM_SETTINGCHANGE: our own ABM_SETPOS broadcasts it (work area changed), which would loop.
            case WmDpiChanged when !_positioning:
                // Scale changed: recompute the reserved pixels.
                _window.Dispatcher.BeginInvoke(Dock);
                return IntPtr.Zero;
        }

        if (_callbackMessage == 0 || msg != _callbackMessage)
        {
            return IntPtr.Zero;
        }

        var notification = wParam.ToInt32();
        if (notification == AbnPosChanged && !_positioning)
        {
            _window.Dispatcher.BeginInvoke(Dock);
        }
        else if (notification == AbnFullscreenApp)
        {
            var fullScreenOpening = lParam != IntPtr.Zero;
            _window.Dispatcher.BeginInvoke(() => _window.Topmost = !fullScreenOpening);
        }

        return IntPtr.Zero;
    }

    private void NotifyShell(int message)
    {
        var data = new AppBarData { cbSize = Marshal.SizeOf<AppBarData>(), hWnd = _handle };
        SHAppBarMessage(message, ref data);
    }

    [UnmanagedCallersOnly]
    private static int OnMonitor(IntPtr monitor, IntPtr hdc, NativeRect* rect, IntPtr state)
    {
        var list = (List<DisplayMonitor>)GCHandle.FromIntPtr(state).Target!;
        var info = new MonitorInfoEx { Size = sizeof(MonitorInfoEx) };
        if (GetMonitorInfo(monitor, ref info))
        {
            var scale = GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
            var deviceName = new string(info.Device, 0, 32).TrimEnd('\0');
            list.Add(new DisplayMonitor(
                deviceName,
                deviceName,
                null,
                (info.Flags & MonitorInfoPrimary) != 0,
                info.Monitor.ToInt32Rect(),
                info.Work.ToInt32Rect(),
                scale));
        }

        return 1;
    }

    [LibraryImport("shell32.dll")]
    private static partial nuint SHAppBarMessage(int dwMessage, ref AppBarData pData);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessage(string lpString);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, delegate* unmanaged<IntPtr, IntPtr, NativeRect*, IntPtr, int> callback, IntPtr state);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx info);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public int uEdge;
        public NativeRect rc;
        public nint lParam;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
        public fixed char Device[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect(int left, int top, int right, int bottom)
    {
        public int Left = left;
        public int Top = top;
        public int Right = right;
        public int Bottom = bottom;

        public readonly Int32Rect ToInt32Rect() => new(Left, Top, Right - Left, Bottom - Top);
    }
}
