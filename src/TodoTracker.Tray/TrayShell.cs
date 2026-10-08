using System.Diagnostics;
using Avalonia.Threading;
using TodoTracker.Desktop;

namespace TodoTracker.Tray;

/// <summary>What the shared view model needs from the desktop: links open in the browser; the rest is quiet.</summary>
internal sealed class TrayShell : IDesktopShell
{
    public void OpenUrl(string url) => Open(url);

    // No window to own the clipboard in the tray app (copying is offered in the full window).
    public void CopyToClipboard(string text)
    {
    }

    public void RunOnUi(Action action) => Dispatcher.UIThread.Post(action);

    public string? Prompt(string title, string message, string? initialValue = null) => null;

    public bool Confirm(string message) => false;

    /// <summary>The command that opens a link: xdg-open on Linux, open on macOS, the shell on Windows.</summary>
    internal static ProcessStartInfo Opener(string url, bool linux, bool mac) =>
        linux ? new ProcessStartInfo("xdg-open", [url]) { UseShellExecute = false }
        : mac ? new ProcessStartInfo("open", [url]) { UseShellExecute = false }
        : new ProcessStartInfo(url) { UseShellExecute = true };

    internal static void Open(string url)
    {
        try
        {
            using var _ = Process.Start(Opener(url, OperatingSystem.IsLinux(), OperatingSystem.IsMacOS()));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Nothing here opens links (no browser): nothing to do.
        }
    }
}
