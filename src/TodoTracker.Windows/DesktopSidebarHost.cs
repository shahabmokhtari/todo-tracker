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
    private const string AppBarMessageName = "TodoTracker.Windows.AppBarCallback";

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

        var callbackMessage = RegisterWindowMessage(AppBarMessageName);
        if (callbackMessage == 0)
        {
            return;
        }

        var transforms = DpiTransforms.For(_window);
        var widthInPixels = (int)Math.Round(_window.Width * transforms.ToDevice.M11);
        var data = AppBarData.Create(handle, callbackMessage, widthInPixels, transforms.ToDevice);
        if (!_registered)
        {
            if (SHAppBarMessage(AbmNew, ref data) == 0)
            {
                return;
            }

            _registered = true;
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

        var handle = new WindowInteropHelper(_window).Handle;
        var transforms = DpiTransforms.For(_window);
        var callbackMessage = RegisterWindowMessage(AppBarMessageName);
        var data = AppBarData.Create(handle, callbackMessage, 0, transforms.ToDevice);
        SHAppBarMessage(AbmRemove, ref data);
        _registered = false;
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
