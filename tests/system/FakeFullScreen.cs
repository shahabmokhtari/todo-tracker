// A stand-in for a full-screen window, compiled by fullscreen.system-test.ps1 (Windows PowerShell's C# 5 compiler).
// Usage: tt-fake-rdp.exe <class> <x> <y> <width> <height> [resist]
//   class "rdp": registers TscShellContainerClass, the Remote Desktop clients' top-level window class.
//   resist: refuses to be resized (like a client that goes straight back to full screen).
using System;
using System.Runtime.InteropServices;

public static class FakeFullScreen
{
    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize; public int style; public WndProc lpfnWndProc; public int cbClsExtra; public int cbWndExtra;
        public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        public string lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS { public IntPtr hwnd; public IntPtr after; public int x; public int y; public int cx; public int cy; public uint flags; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(int ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int color);

    private const uint WsPopup = 0x80000000;
    private const uint WsVisible = 0x10000000;
    private const uint WmDestroy = 0x0002;
    private const uint WmWindowPosChanging = 0x0046;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;

    private static WndProc _proc;
    private static bool _resist;
    private static int _x, _y, _w, _h;

    public static int Main(string[] args)
    {
        // Physical pixels, like the sidebar.
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        var className = args[0] == "rdp" ? "TscShellContainerClass" : "TtFakeFullScreen";
        _x = int.Parse(args[1]); _y = int.Parse(args[2]); _w = int.Parse(args[3]); _h = int.Parse(args[4]);
        _resist = args.Length > 5 && args[5] == "resist";
        _proc = Proc;
        var wc = new WNDCLASSEX { lpfnWndProc = _proc, lpszClassName = className, hbrBackground = CreateSolidBrush(0x00804020) };
        wc.cbSize = Marshal.SizeOf(typeof(WNDCLASSEX));
        RegisterClassEx(ref wc);
        var hwnd = CreateWindowEx(0, className, "Fake full screen", WsPopup | WsVisible, _x, _y, _w, _h, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        // In front and active, like a session the user clicked into.
        var front = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        var me = GetCurrentThreadId();
        AttachThreadInput(front, me, true);
        keybd_event(0x12, 0, 0, IntPtr.Zero);
        keybd_event(0x12, 0, 2, IntPtr.Zero);
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        AttachThreadInput(front, me, false);
        Console.WriteLine(hwnd.ToInt64());

        MSG msg;
        while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        return 0;
    }

    private static IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmWindowPosChanging && _resist)
        {
            var pos = (WINDOWPOS)Marshal.PtrToStructure(lParam, typeof(WINDOWPOS));
            if ((pos.flags & (SwpNoSize | SwpNoMove)) != (SwpNoSize | SwpNoMove))
            {
                pos.x = _x; pos.y = _y; pos.cx = _w; pos.cy = _h;
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }
        else if (msg == WmDestroy)
        {
            PostQuitMessage(0);
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
