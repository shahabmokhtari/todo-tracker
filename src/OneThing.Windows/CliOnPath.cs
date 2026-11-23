using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OneThing.Desktop;

namespace OneThing.Windows;

/// <summary>
/// "Add ot to your PATH" in the sidebar's menu: the app's <c>cli</c> folder (<c>onething.exe</c>, <c>ot.exe</c>) at
/// the end of your own PATH (no admin rights), so a new terminal finds <c>ot</c>. Only when you choose it; the rest of
/// your PATH stays as it was (its kind too, so <c>%USERPROFILE%</c> entries keep working). "Connect an AI app" never
/// needs it: it fills in the full path.
/// </summary>
internal static partial class CliOnPath
{
    private const string EnvironmentKey = "Environment";
    private const string PathName = "Path";

    /// <summary>The app's cli folder, when it has one (a published app does; a build run from the repository may not).</summary>
    public static string? Folder =>
        Path.Combine(AppContext.BaseDirectory, "cli") is var cli && File.Exists(Path.Combine(cli, "onething.exe")) ? cli : null;

    public static MenuItem MenuItem(string folder)
    {
        var onPath = UserPath.Contains(Read().Value, folder);
        var item = new MenuItem
        {
            Header = onPath ? "ot is on your PATH" : "Add ot to your PATH",
            IsEnabled = !onPath,
            ToolTip = $"Lets a new terminal run ot and onething ({folder}).",
        };
        item.Click += (_, _) => Add(folder);
        return item;
    }

    private static void Add(string folder)
    {
        try
        {
            var (value, kind) = Read();
            if (UserPath.WithFolder(value, folder) is { } changed)
            {
                using var key = Registry.CurrentUser.CreateSubKey(EnvironmentKey);
                key.SetValue(PathName, changed, kind);
                // Tell running programs (Explorer, so new terminals get it) that the environment changed.
                _ = SendMessageTimeout(new IntPtr(0xffff), 0x001A, IntPtr.Zero, EnvironmentKey, 0x0002, 5000, out _);
            }

            MessageBox.Show("Done. Open a new terminal and try: ot now", "One Thing", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            MessageBox.Show($"Couldn't add ot to your PATH ({ex.Message}). Add this folder to it yourself: {folder}", "One Thing", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Your PATH as written (not expanded) and its kind (expandable unless it's plain text already).</summary>
    private static (string? Value, RegistryValueKind Kind) Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(EnvironmentKey);
        var value = key?.GetValue(PathName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        var kind = value is not null && key!.GetValueKind(PathName) == RegistryValueKind.String ? RegistryValueKind.String : RegistryValueKind.ExpandString;
        return (value, kind);
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}
