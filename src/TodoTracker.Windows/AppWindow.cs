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
/// only hides it, so it comes back instantly. It remembers where it was. Without a working WebView2 the browser is used
/// instead (and stays used until the app restarts).
/// </summary>
internal sealed class AppWindowHost : IDisposable
{
    private readonly AppWindowStateStore _store;
    private AppWindow? _window;
    private bool _broken;

    public AppWindowHost(AppWindowStateStore store, Uri origin, Func<string, string> launchUrl, string userDataFolder)
    {
        _store = store;
        Origin = origin;
        LaunchUrl = launchUrl;
        UserDataFolder = userDataFolder;
        State = store.Load();
    }

    /// <summary>The window's place or the break choice changed.</summary>
    public event EventHandler? Changed;

    public Uri Origin { get; }

    /// <summary>A single-use sign-in link to a path of the app (the API token never appears in a URL).</summary>
    public Func<string, string> LaunchUrl { get; }

    public string UserDataFolder { get; }

    public AppWindowState State { get; private set; }

    /// <summary>Whether the desktop shows breaks itself (then the app in the window doesn't show its own).</summary>
    public Func<bool> NativeBreaks { get; set; } = () => false;

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

    /// <summary>Shows a path of the app ("/", "/#/task/&lt;id&gt;", "/#/ask").</summary>
    public void Open(string path)
    {
        if (_broken || !IsAvailable)
        {
            OpenInBrowser(path);
            return;
        }

        _window ??= new AppWindow(this);
        _window.ShowPath(path);
    }

    public void OpenInBrowser(string path) => Process.Start(new ProcessStartInfo(LaunchUrl(path)) { UseShellExecute = true });

    /// <summary>The window couldn't start (or its browser died): it's gone; the next open builds a new one.</summary>
    internal void Lost(AppWindow window, bool useBrowser)
    {
        if (_window == window)
        {
            _window = null;
        }

        _broken |= useBrowser;
    }

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
    private static readonly System.Drawing.Color DarkBackdrop = System.Drawing.Color.FromArgb(11, 16, 32);
    private readonly AppWindowHost _host;
    private readonly WebView2 _web;
    private bool _ready;
    private bool _loaded;
    private bool _closingForGood;
    private string _pending = "/";
    private string? _breakScript;
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
        Background = dark ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(DarkBackdrop.R, DarkBackdrop.G, DarkBackdrop.B)) : System.Windows.Media.Brushes.White;
        _web = new WebView2 { DefaultBackgroundColor = dark ? DarkBackdrop : System.Drawing.Color.White };
        Content = _web;
        SourceInitialized += (_, _) => RestorePlacement();
        LocationChanged += (_, _) => RememberNormalBounds();
        SizeChanged += (_, _) => RememberNormalBounds();
        Closing += OnClosing;
        _host.Changed += OnHostChanged;
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

        var plan = AppNavigation.Plan(_web.CoreWebView2.Source, _loaded, path);
        switch (plan.Kind)
        {
            case "focus":
                break;
            case "hash":
                // A view, task or panel of the app already open: no reload, so nothing being typed is lost.
                _ = RunScriptAsync($"location.hash = {JsonSerializer.Serialize(plan.Value)}", path);
                break;
            default:
                Navigate(path);
                break;
        }
    }

    public void CloseForGood()
    {
        if (_closingForGood)
        {
            return;
        }

        _closingForGood = true;
        _host.Changed -= OnHostChanged;
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

#pragma warning disable CA1031 // Last resort: whatever stops WebView2 from starting, the browser takes over.
    private async Task InitializeAsync()
    {
        try
        {
            Directory.CreateDirectory(_host.UserDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, _host.UserDataFolder).ConfigureAwait(true);
            await _web.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            var core = _web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
            core.NewWindowRequested += OnNewWindowRequested;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += (_, e) => _loaded = e.IsSuccess && AppNavigation.Classify(_host.Origin, core.Source) == AppLink.App;
            core.DocumentTitleChanged += (_, _) => Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "Todo Tracker" : core.DocumentTitle;
            core.ProcessFailed += OnProcessFailed;
            await SetNativeBreaksAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            var path = _pending;
            _host.Lost(this, useBrowser: true);
            CloseForGood();
            _host.OpenInBrowser(path);
            return;
        }

        _ready = true;
        Navigate(_pending);
    }

    private async Task RunScriptAsync(string script, string fallbackPath)
    {
        try
        {
            await _web.CoreWebView2.ExecuteScriptAsync(script).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // The page isn't answering: load it again.
            Navigate(fallbackPath);
        }
    }

    /// <summary>The app's own break screen stays off in this window while the desktop shows breaks on every monitor.</summary>
    private async Task SetNativeBreaksAsync()
    {
        try
        {
            var core = _web.CoreWebView2;
            var script = $"window.__ttNativeBreaks = {(_host.NativeBreaks() ? "true" : "false")};";
            if (_breakScript is not null)
            {
                core.RemoveScriptToExecuteOnDocumentCreated(_breakScript);
            }

            _breakScript = await core.AddScriptToExecuteOnDocumentCreatedAsync(script).ConfigureAwait(true);
            if (_loaded)
            {
                await core.ExecuteScriptAsync(script).ConfigureAwait(true);
            }
        }
        catch (Exception) when (_ready)
        {
            // A page that isn't answering gets the setting when it loads again.
        }
    }
#pragma warning restore CA1031

    private void OnHostChanged(object? sender, EventArgs e)
    {
        if (_ready && !_closingForGood)
        {
            _ = SetNativeBreaksAsync();
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            // The whole WebView is gone: a new window next time.
            _host.Lost(this, useBrowser: false);
            CloseForGood();
            return;
        }

        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            Navigate("/");
        }
    }

    private void Navigate(string path)
    {
        _loaded = false;
        _web.CoreWebView2.Navigate(_host.LaunchUrl(path));
    }

    /// <summary>Requests for a new window: the app's other pages (the printable report) and links elsewhere open in the
    /// browser; nothing opens a bare popup.</summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        Follow(e.Uri, e.IsUserInitiated, inWindow: false);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (AppNavigation.Classify(_host.Origin, e.Uri) == AppLink.App)
        {
            return;
        }

        // The window only ever shows the app itself.
        e.Cancel = true;
        Follow(e.Uri, e.IsUserInitiated, inWindow: true);
    }

    private void Follow(string url, bool userInitiated, bool inWindow)
    {
        switch (AppNavigation.Classify(_host.Origin, url))
        {
            case AppLink.App when !inWindow && url != "about:blank":
            case AppLink.OwnPageInBrowser:
                _host.OpenInBrowser(AppNavigation.PathOf(url));
                break;
            case AppLink.Outside:
            case AppLink.OutsideIfAsked when userInitiated:
                Process.Start(new ProcessStartInfo(new Uri(url).AbsoluteUri) { UseShellExecute = true });
                break;
            default:
                break; // data:, javascript:, file:, or another app the person didn't ask for
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
