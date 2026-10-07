using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using TodoTracker.Desktop;

namespace TodoTracker.Windows;

/// <summary>
/// The whole app (board, outline, reports…) in its own window next to the sidebar. There is one, reused: closing it
/// only hides it, so it comes back instantly. It remembers where it was. Without the WebView2 runtime the browser is
/// used instead.
/// </summary>
internal sealed class AppWindowHost : IDisposable
{
    private readonly AppWindowStateStore _store;
    private AppWindow? _window;

    public AppWindowHost(AppWindowStateStore store, Uri origin, Func<string, string> launchUrl, string userDataFolder)
    {
        _store = store;
        Origin = origin;
        LaunchUrl = launchUrl;
        UserDataFolder = userDataFolder;
        State = store.Load();
    }

    public event EventHandler? Changed;

    public Uri Origin { get; }

    /// <summary>A single-use sign-in link to a path of the app (the API token never appears in a URL).</summary>
    public Func<string, string> LaunchUrl { get; }

    public string UserDataFolder { get; }

    public AppWindowState State { get; private set; }

    public static bool IsAvailable
    {
        get
        {
            try
            {
                return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
            }
            catch (WebView2RuntimeNotFoundException)
            {
                return false;
            }
        }
    }

    public void SetFullScreenBreaks(bool on) => Update(State with { FullScreenBreaks = on });

    internal void Update(AppWindowState state)
    {
        State = state;
        try
        {
            _store.Save(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Remembering the window is a nicety; never fail over it.
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows a path of the app ("/", "/#/task/&lt;id&gt;", "/?ask=1").</summary>
    public void Open(string path)
    {
        if (!IsAvailable)
        {
            OpenInBrowser(path);
            return;
        }

        _window ??= new AppWindow(this);
        _window.ShowPath(path);
    }

    public void OpenInBrowser(string path) => Process.Start(new ProcessStartInfo(LaunchUrl(path)) { UseShellExecute = true });

    public void Dispose()
    {
        _window?.CloseForGood();
        _window = null;
    }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The WebView is disposed in CloseForGood; WPF owns the window's lifetime.")]
internal sealed partial class AppWindow : Window
{
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private readonly AppWindowHost _host;
    private readonly WebView2 _web;
    private bool _ready;
    private bool _loaded;
    private bool _closingForGood;
    private string _pending = "/";
    private PlacementBounds? _normalBounds;

    public AppWindow(AppWindowHost host)
    {
        _host = host;
        Title = "Todo Tracker";
        Width = 1280;
        Height = 860;
        MinWidth = AppWindowState.MinWidth;
        MinHeight = AppWindowState.MinHeight;
        WindowStartupLocation = host.State.Bounds is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.Manual;
        // Same backdrop as the app's theme, so there is no white flash while the page loads.
        var dark = IsDarkTheme();
        Background = dark ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(11, 16, 32)) : System.Windows.Media.Brushes.White;
        _web = new WebView2 { DefaultBackgroundColor = dark ? System.Drawing.Color.FromArgb(11, 16, 32) : System.Drawing.Color.White };
        Content = _web;
        SourceInitialized += (_, _) => RestorePlacement();
        LocationChanged += (_, _) => RememberNormalBounds();
        SizeChanged += (_, _) => RememberNormalBounds();
        Closing += OnClosing;
        _ = InitializeAsync();
    }

    public void ShowPath(string path)
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = _host.State.Maximized ? WindowState.Maximized : WindowState.Normal;
        }

        Activate();
        if (!_ready)
        {
            _pending = path;
            return;
        }

        if (_loaded && path == "/")
        {
            return; // already showing the app: just bring it to the front
        }

        if (_loaded && path.StartsWith("/#", StringComparison.Ordinal))
        {
            // A view or task inside the app already open: no reload.
            _ = _web.CoreWebView2.ExecuteScriptAsync($"location.hash = {JsonSerializer.Serialize(path[1..])}");
            return;
        }

        Navigate(path);
    }

    public void CloseForGood()
    {
        _closingForGood = true;
        SavePlacement();
        Close();
        _web.Dispose();
    }

    private static bool IsDarkTheme()
    {
        if (Application.Current?.ThemeMode == ThemeMode.Dark)
        {
            return true;
        }

        if (Application.Current?.ThemeMode == ThemeMode.Light)
        {
            return false;
        }

        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    private async Task InitializeAsync()
    {
        try
        {
            Directory.CreateDirectory(_host.UserDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, _host.UserDataFolder).ConfigureAwait(true);
            await _web.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or IOException or UnauthorizedAccessException)
        {
            // No usable WebView2: the browser it is.
            var path = _pending;
            CloseForGood();
            _host.OpenInBrowser(path);
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
        core.NewWindowRequested += OnNewWindowRequested;
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += (_, e) => _loaded = e.IsSuccess && IsOurs(core.Source);
        core.DocumentTitleChanged += (_, _) => Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "Todo Tracker" : core.DocumentTitle;
        _ready = true;
        Navigate(_pending);
    }

    private void Navigate(string path)
    {
        _loaded = false;
        _web.CoreWebView2.Navigate(_host.LaunchUrl(path));
    }

    private bool IsOurs(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && Uri.Compare(uri, _host.Origin, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>Links elsewhere open in the browser; the app's own pages that ask for a new window (the printable
    /// report) open there too, signed in with a single-use link.</summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (IsOurs(e.Uri))
        {
            _host.OpenInBrowser(new Uri(e.Uri).PathAndQuery);
        }
        else
        {
            OpenOutside(e.Uri);
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsOurs(e.Uri) || e.Uri.StartsWith("about:", StringComparison.Ordinal) || e.Uri.StartsWith("data:", StringComparison.Ordinal))
        {
            return;
        }

        // The window only ever shows the app; anything else goes to the browser (or the app that handles it).
        e.Cancel = true;
        OpenOutside(e.Uri);
    }

    private static void OpenOutside(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto" or "obsidian")
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SavePlacement();
        if (_closingForGood)
        {
            return;
        }

        // Hidden, not closed: next time it is back at once, on the same page.
        e.Cancel = true;
        Hide();
    }

    private void RememberNormalBounds()
    {
        if (WindowState == WindowState.Normal && IsLoaded && new WindowInteropHelper(this).Handle is var handle && handle != IntPtr.Zero && GetWindowRect(handle, out var r))
        {
            _normalBounds = new PlacementBounds(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }
    }

    private void SavePlacement()
    {
        RememberNormalBounds();
        _host.Update(_host.State with
        {
            Bounds = _normalBounds ?? _host.State.Bounds,
            Maximized = WindowState == WindowState.Maximized,
        });
    }

    private void RestorePlacement()
    {
        if (_host.State.Bounds is { } saved)
        {
            // Physical pixels across monitors; back on screen if its monitor is gone.
            var bounds = PlacementMath.EnsureVisible(saved, DesktopSidebarHost.Monitors().Select(m => m.WorkAreaPixels).ToList());
            SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, (int)Math.Round(bounds.Left), (int)Math.Round(bounds.Top), (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height), SwpNoZOrder | SwpNoActivate);
            _normalBounds = bounds;
        }

        if (_host.State.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
