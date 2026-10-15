using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;
using TodoTracker.Desktop;

namespace TodoTracker.Windows;

internal sealed class WpfShell : IDesktopShell
{
    private static Window? Owner => Application.Current.MainWindow;

    public void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public void CopyToClipboard(string text) => Clipboard.SetText(text);

    public void CopyLink(string url, string title)
    {
        // Rich (a link named after the task, for chat, mail and notes) with the bare address as the plain text.
        var data = new DataObject();
        data.SetText(url);
        data.SetData(DataFormats.Html, HtmlClipboard.Format(HtmlClipboard.Anchor(url, title)));
        Clipboard.SetDataObject(data, copy: true);
    }

    public void RunOnUi(Action action)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    public string? Prompt(string title, string message, string? initialValue = null) => InputDialog.Show(Owner, title, message, initialValue);

    public bool Confirm(string message) => MessageBox.Show(message, "Todo Tracker", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}

/// <summary>Small keyboard-friendly prompt (Enter = OK, Esc = cancel).</summary>
internal static class InputDialog
{
    public static string? Show(Window? owner, string title, string message, string? initialValue)
    {
        var input = new TextBox { Text = initialValue ?? string.Empty, MinWidth = 300, Margin = new Thickness(0, 8, 0, 12) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        var panel = new StackPanel { Margin = new Thickness(16), Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 }, input, buttons } };
        var dialog = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
            Topmost = true,
            Owner = owner is { IsLoaded: true } ? owner : null,
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        return dialog.ShowDialog() == true ? input.Text : null;
    }
}

/// <summary>System-wide Ctrl+Alt+Space to jump into quick capture from any app.</summary>
internal sealed partial class GlobalHotKey : IDisposable
{
    private const int WmHotKey = 0x0312;
    private const uint ModAlt = 0x1;
    private const uint ModControl = 0x2;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkSpace = 0x20;
    private const int Id = 0x7417;
    private readonly HwndSource _source;
    private readonly Action _onPressed;
    private readonly bool _registered;

    public GlobalHotKey(Window window, Action onPressed)
    {
        _onPressed = onPressed;
        var handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source.AddHook(WndProc);
        _registered = RegisterHotKey(handle, Id, ModControl | ModAlt | ModNoRepeat, VkSpace);
    }

    public bool IsRegistered => _registered;

    public void Dispose()
    {
        if (_registered)
        {
            UnregisterHotKey(_source.Handle, Id);
        }

        _source.RemoveHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt32() == Id)
        {
            handled = true;
            _onPressed();
        }

        return IntPtr.Zero;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);
}

/// <summary>
/// Per-user handler for todotracker:// links (HKCU\Software\Classes, no admin rights): Windows starts this app with the
/// link, and a running one is handed it (SingleInstance). Rewritten at every start, so it follows the app if it moves.
/// </summary>
internal static class DeepLinkRegistration
{
    public static void Register()
    {
        if (Environment.ProcessPath is not { } exe)
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{TodoTracker.Core.TaskLinks.Scheme}");
            key.SetValue(string.Empty, "URL:Todo Tracker task link");
            key.SetValue("URL Protocol", string.Empty);
            using (var icon = key.CreateSubKey("DefaultIcon"))
            {
                icon.SetValue(string.Empty, $"\"{exe}\",0");
            }

            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue(string.Empty, $"\"{exe}\" \"%1\"");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            // Links are a nicety; the app runs without them.
        }
    }
}

/// <summary>Per-user "start with Windows" via HKCU\...\Run (no admin rights needed).</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TodoTracker";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
