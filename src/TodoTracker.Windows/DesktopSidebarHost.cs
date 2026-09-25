using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TodoTracker.Windows;

internal sealed class DesktopSidebarHost : IDisposable
{
    private const int AbeRight = 2;
    private const int AbmNew = 0;
    private const int AbmRemove = 1;
    private const int AbmQueryPos = 2;
    private const int AbmSetPos = 3;

    private readonly Window _window;
    private bool _registered;

    public DesktopSidebarHost(Window window)
    {
        _window = window;
    }

    public void ReserveRightEdge()
    {
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var data = AppBarData.Create(handle, _window.Width);
        if (!_registered)
        {
            SHAppBarMessage(AbmNew, ref data);
            _registered = true;
        }

        SHAppBarMessage(AbmQueryPos, ref data);
        data.rc.Left = data.rc.Right - (int)_window.Width;
        SHAppBarMessage(AbmSetPos, ref data);

        _window.Left = data.rc.Left;
        _window.Top = data.rc.Top;
        _window.Width = data.rc.Right - data.rc.Left;
        _window.Height = data.rc.Bottom - data.rc.Top;
    }

    public void Dispose()
    {
        if (!_registered)
        {
            return;
        }

        var handle = new WindowInteropHelper(_window).Handle;
        var data = AppBarData.Create(handle, _window.Width);
        SHAppBarMessage(AbmRemove, ref data);
        _registered = false;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern nuint SHAppBarMessage(int dwMessage, ref AppBarData pData);

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public int uEdge;
        public Rect rc;
        public nint lParam;

        public static AppBarData Create(IntPtr handle, double width)
        {
            var workArea = SystemParameters.WorkArea;
            return new AppBarData
            {
                cbSize = Marshal.SizeOf<AppBarData>(),
                hWnd = handle,
                uEdge = AbeRight,
                rc = new Rect
                {
                    Left = (int)(workArea.Right - width),
                    Top = (int)workArea.Top,
                    Right = (int)workArea.Right,
                    Bottom = (int)workArea.Bottom
                }
            };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
