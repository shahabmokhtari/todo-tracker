using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TodoTracker.Windows;

/// <summary>
/// Registers the window as a Win32 AppBar docked to the right edge, so the shell shrinks the work area and
/// maximized windows never cover the sidebar. Handles DPI, monitor work areas, shell repositioning, and
/// full-screen apps (drops Topmost while a full-screen app is active).
/// </summary>
internal sealed partial class DesktopSidebarHost : IDisposable
{
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
    private const int WmSettingChange = 0x001A;
    private const int WmDpiChanged = 0x02E0;
    private const int AbnPosChanged = 1;
    private const int AbnFullscreenApp = 2;
    private const uint MonitorDefaultToNearest = 2;
    private const string AppBarMessageName = "TodoTracker.Windows.AppBarCallback";

    private readonly Window _window;
    private HwndSource? _source;
    private IntPtr _handle;
    private uint _callbackMessage;
    private bool _registered;
    private bool _positioning;

    public DesktopSidebarHost(Window window)
    {
        _window = window;
    }

    public bool IsDocked => _registered;

    /// <summary>Width in device-independent pixels reserved at the right edge.</summary>
    public double WidthInDips { get; set; } = 360;

    public void Dock()
    {
        if (_positioning)
        {
            return;
        }

        _positioning = true;
        try
        {
            _handle = new WindowInteropHelper(_window).Handle;
            if (_handle == IntPtr.Zero)
            {
                return;
            }

            if (_callbackMessage == 0)
            {
                _callbackMessage = RegisterWindowMessage(AppBarMessageName);
            }

            if (_source is null)
            {
                _source = HwndSource.FromHwnd(_handle);
                _source?.AddHook(WndProc);
            }

            var transforms = DpiTransforms.For(_window);
            var widthInPixels = (int)Math.Round(WidthInDips * transforms.ToDevice.M11);
            var data = AppBarData.Create(_handle, _callbackMessage, widthInPixels, transforms.ToDevice);
            if (!_registered)
            {
                if (SHAppBarMessage(AbmNew, ref data) == 0)
                {
                    return;
                }

                _registered = true;
            }

            SHAppBarMessage(AbmQueryPos, ref data);
            data.rc.Left = data.rc.Right - widthInPixels;
            SHAppBarMessage(AbmSetPos, ref data);

            var topLeft = transforms.FromDevice.Transform(new Point(data.rc.Left, data.rc.Top));
            var bottomRight = transforms.FromDevice.Transform(new Point(data.rc.Right, data.rc.Bottom));
            _window.Left = topLeft.X;
            _window.Top = topLeft.Y;
            _window.Width = bottomRight.X - topLeft.X;
            _window.Height = bottomRight.Y - topLeft.Y;
        }
        finally
        {
            _positioning = false;
        }
    }

    /// <summary>Gives the screen space back (the window becomes a normal floating window).</summary>
    public void Undock()
    {
        if (!_registered)
        {
            return;
        }

        var data = AppBarData.Create(_handle, _callbackMessage, 0, Matrix.Identity);
        SHAppBarMessage(AbmRemove, ref data);
        _registered = false;
    }

    public void Dispose()
    {
        Undock();
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
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
            case WmDisplayChange or WmDpiChanged or WmSettingChange when !_positioning:
                // Resolution, monitor layout, or scale changed: recompute the reserved pixels.
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

    [LibraryImport("shell32.dll")]
    private static partial nuint SHAppBarMessage(int dwMessage, ref AppBarData pData);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessage(string lpString);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    private sealed record DpiTransforms(Matrix ToDevice, Matrix FromDevice)
    {
        public static DpiTransforms For(Window window)
        {
            var source = PresentationSource.FromVisual(window);
            return source?.CompositionTarget is null
                ? new DpiTransforms(Matrix.Identity, Matrix.Identity)
                : new DpiTransforms(source.CompositionTarget.TransformToDevice, source.CompositionTarget.TransformFromDevice);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public int uEdge;
        public NativeRect rc;
        public nint lParam;

        public static AppBarData Create(IntPtr handle, uint callbackMessage, int widthInPixels, Matrix toDevice)
        {
            // AppBars must be positioned against the full monitor rect; the shell subtracts other bars (taskbar) in QueryPos.
            var monitor = MonitorRectFor(handle, toDevice);
            return new AppBarData
            {
                cbSize = Marshal.SizeOf<AppBarData>(),
                hWnd = handle,
                uCallbackMessage = callbackMessage,
                uEdge = AbeRight,
                rc = new NativeRect { Left = monitor.Right - widthInPixels, Top = monitor.Top, Right = monitor.Right, Bottom = monitor.Bottom },
            };
        }

        private static NativeRect MonitorRectFor(IntPtr handle, Matrix toDevice)
        {
            var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero)
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    return info.Monitor;
                }
            }

            var area = SystemParameters.WorkArea;
            var topLeft = toDevice.Transform(new Point(area.Left, area.Top));
            var bottomRight = toDevice.Transform(new Point(area.Right, area.Bottom));
            return new NativeRect { Left = (int)topLeft.X, Top = (int)topLeft.Y, Right = (int)bottomRight.X, Bottom = (int)bottomRight.Y };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
