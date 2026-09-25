using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TodoTracker.Windows;

internal sealed class DesktopSidebarHost : IDisposable
{
    private const int AbeRight = 2;
    private const int AbmNew = 0;
    private const int AbmRemove = 1;
    private const int AbmQueryPos = 2;
    private const int AbmSetPos = 3;
    private const int AbnPosChanged = 1;
    private const int AbnFullscreenApp = 2;
    private const string AppBarMessageName = "TodoTracker.Windows.AppBarCallback";

    private readonly Window _window;
    private HwndSource? _source;
    private IntPtr _handle;
    private uint _callbackMessage;
    private bool _registered;
    private bool _hooked;

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

        _handle = handle;
        _callbackMessage = _callbackMessage == 0
            ? RegisterWindowMessage(AppBarMessageName)
            : _callbackMessage;
        if (_callbackMessage == 0)
        {
            return;
        }

        _source ??= HwndSource.FromHwnd(handle);
        var transforms = DpiTransforms.For(_window);
        var widthInPixels = (int)Math.Round(EffectiveWidth() * transforms.ToDevice.M11);
        var data = AppBarData.Create(handle, _callbackMessage, widthInPixels, transforms.ToDevice);
        if (!_registered)
        {
            if (SHAppBarMessage(AbmNew, ref data) == 0)
            {
                return;
            }

            _registered = true;
            if (!_hooked && _source is not null)
            {
                _source.AddHook(WndProc);
                _hooked = true;
            }
        }

        if (SHAppBarMessage(AbmQueryPos, ref data) == 0)
        {
            return;
        }

        data.rc.Left = data.rc.Right - widthInPixels;
        if (SHAppBarMessage(AbmSetPos, ref data) == 0)
        {
            return;
        }

        var topLeft = transforms.FromDevice.Transform(new Point(data.rc.Left, data.rc.Top));
        var bottomRight = transforms.FromDevice.Transform(new Point(data.rc.Right, data.rc.Bottom));
        _window.Left = topLeft.X;
        _window.Top = topLeft.Y;
        _window.Width = bottomRight.X - topLeft.X;
        _window.Height = bottomRight.Y - topLeft.Y;
    }

    public void Dispose()
    {
        if (!_registered)
        {
            return;
        }

        if (_hooked && _source is not null)
        {
            _source.RemoveHook(WndProc);
            _hooked = false;
        }

        var handle = _handle != IntPtr.Zero
            ? _handle
            : new WindowInteropHelper(_window).Handle;
        var data = AppBarData.Create(handle, _callbackMessage, 0, Matrix.Identity);
        SHAppBarMessage(AbmRemove, ref data);
        _registered = false;
    }

    private double EffectiveWidth()
    {
        if (!double.IsNaN(_window.ActualWidth) && _window.ActualWidth > 0)
        {
            return _window.ActualWidth;
        }

        return !double.IsNaN(_window.Width) && _window.Width > 0
            ? _window.Width
            : 380;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_callbackMessage != 0 && msg == _callbackMessage && IsPositionNotification(wParam))
        {
            _window.Dispatcher.BeginInvoke(ReserveRightEdge);
        }

        return IntPtr.Zero;
    }

    private static bool IsPositionNotification(IntPtr notification)
    {
        var value = notification.ToInt32();
        return value is AbnPosChanged or AbnFullscreenApp;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern nuint SHAppBarMessage(int dwMessage, ref AppBarData pData);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    private sealed record DpiTransforms(Matrix ToDevice, Matrix FromDevice)
    {
        public static DpiTransforms For(Window window)
        {
            var source = PresentationSource.FromVisual(window);
            if (source?.CompositionTarget is null)
            {
                return new DpiTransforms(Matrix.Identity, Matrix.Identity);
            }

            return new DpiTransforms(
                source.CompositionTarget.TransformToDevice,
                source.CompositionTarget.TransformFromDevice);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public int uEdge;
        public Rect rc;
        public nint lParam;

        public static AppBarData Create(IntPtr handle, uint callbackMessage, int widthInPixels, Matrix toDevice)
        {
            var workArea = SystemParameters.WorkArea;
            var topLeft = toDevice.Transform(new Point(workArea.Left, workArea.Top));
            var bottomRight = toDevice.Transform(new Point(workArea.Right, workArea.Bottom));
            return new AppBarData
            {
                cbSize = Marshal.SizeOf<AppBarData>(),
                hWnd = handle,
                uCallbackMessage = callbackMessage,
                uEdge = AbeRight,
                rc = new Rect
                {
                    Left = (int)Math.Round(bottomRight.X - widthInPixels),
                    Top = (int)Math.Round(topLeft.Y),
                    Right = (int)Math.Round(bottomRight.X),
                    Bottom = (int)Math.Round(bottomRight.Y)
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
