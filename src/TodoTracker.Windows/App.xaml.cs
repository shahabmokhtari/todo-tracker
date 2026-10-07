using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using TodoTracker.Core;
using TodoTracker.Desktop;
using TodoTracker.Server;

namespace TodoTracker.Windows;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed in OnExit; WPF owns the Application lifetime.")]
public partial class App : Application
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private WebApplication? _server;
    private SidebarViewModel? _viewModel;
    private ToastService? _toasts;
    private string _dataDirectory = TodoTrackerServerOptions.DefaultDataDirectory();
    private SingleInstance? _instance;
    private AgentChatAsk? _ask;
    private MainWindow? _window;
    private AppWindowHost? _appHost;
    private BreakOverlay? _breaks;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledUiException;
        TaskScheduler.UnobservedTaskException += (_, unobserved) => unobserved.SetObserved();
        var args = StartupArgs.Parse(e.Args);

        // One sidebar at a time (tests included): a second launch brings the running one to the front instead.
        _instance = SingleInstance.TryAcquire(wait: args.Restart ? TimeSpan.FromSeconds(30) : TimeSpan.Zero);
        if (_instance is null)
        {
            if (args.Restart)
            {
                // The old instance didn't close in time: say so rather than leaving no sidebar at all.
                MessageBox.Show("Todo Tracker is still closing. Start it again in a moment.", "Todo Tracker", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            Shutdown(args.SmokeTest ? 3 : 0);
            return;
        }

        _instance.OnShowRequested(() => Dispatcher.BeginInvoke(() => _window?.BringToFront()));
        try
        {
            if (args.SmokeTest)
            {
                Shutdown(await RunSmokeTestAsync(args).ConfigureAwait(true));
                return;
            }

            // After "switch tasks folder" the previous instance may still be shutting down: wait for it briefly.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await StartAsync(args).ConfigureAwait(true);
                    break;
                }
                catch (Exception ex) when (args.Restart && attempt < 20 && ex is StoreLockedException or IOException)
                {
                    await Task.Delay(500).ConfigureAwait(true);
                }
            }
        }
        catch (StoreLockedException)
        {
            MessageBox.Show("Todo Tracker is already running.", "Todo Tracker", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
        }
#pragma warning disable CA1031 // Last-chance handler: show the problem instead of crashing silently.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            MessageBox.Show($"Todo Tracker could not start:\n\n{ex.Message}", "Todo Tracker", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Last-chance handler: the always-on sidebar logs and keeps running instead of vanishing.</summary>
    private void OnUnhandledUiException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        try
        {
            File.AppendAllText(Path.Combine(_dataDirectory, "errors.log"), $"{DateTimeOffset.Now:o} {e.Exception}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging is best effort; never let the last-chance handler throw.
        }

        if (_viewModel is not null)
        {
            _viewModel.StatusMessage = "Something went wrong (details in errors.log). The sidebar is still running.";
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _toasts?.Dispose();
        _breaks?.Dispose();
        _appHost?.Dispose();
        _viewModel?.Ask?.Dispose();
        _ask?.Dispose();
        _viewModel?.Dispose();
        if (_server is not null)
        {
            // Run off the UI thread: host shutdown continuations would otherwise deadlock on the WPF dispatcher.
            var server = _server;
            Task.Run(async () =>
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await server.StopAsync(stop.Token).ConfigureAwait(false);
                await server.DisposeAsync().ConfigureAwait(false);
            }).Wait(TimeSpan.FromSeconds(5));
        }

        _instance?.Dispose();
        base.OnExit(e);
    }

    private async Task<MainWindow> StartAsync(StartupArgs args, string? token = null, bool interactive = true, bool toasts = true)
    {
        var options = new TodoTrackerServerOptions
        {
            DataDirectory = args.DataDirectory ?? TodoTrackerServerOptions.DefaultDataDirectory(),
            Port = args.Port ?? TodoTrackerServerOptions.DefaultPort,
            ApiToken = token,
        };
        ApplyTheme(args.Theme);
        _dataDirectory = options.DataDirectory;
        _server = TodoTrackerHost.Build(TodoTrackerHost.CreateBuilder(options));
        string? serverProblem = null;
        try
        {
            await _server.StartAsync().ConfigureAwait(true);
        }
        catch (IOException ex)
        {
            // The sidebar still works on the local board; browser, extension, and MCP need the port.
            serverProblem = $"Local server unavailable on port {options.Port}: {ex.Message}";
        }

        var services = _server.Services;
        // One theme for every window (Settings › Appearance in the app, or the sidebar's menu); --theme wins for this run.
        var settings = services.GetRequiredService<SettingsStore>();
        if (args.Theme is null)
        {
            ApplyTheme(settings.Current.Theme);
            settings.ThemeChanged += (_, theme) => Dispatcher.BeginInvoke(() => ApplyTheme(theme));
        }

        var connection = TodoTrackerHost.GetConnection(services);
        var mcpConfig = JsonSerializer.Serialize(connection.McpConfig, Indented);
        // The whole app in its own window (automated runs keep using the browser path and never save anything).
        _appHost = interactive
            ? new AppWindowHost(
                new AppWindowStateStore(Path.Combine(options.DataDirectory, "app-window.json")),
                new Uri(connection.BaseUrl),
                path => TodoTrackerHost.CreateLaunchUrl(services, path),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TodoTracker", "WebView2"))
            : null;
        var appHost = _appHost;
        _viewModel = new SidebarViewModel(
            services.GetRequiredService<IBoardStore>(),
            TimeProvider.System,
            new WpfShell(),
            new SidebarOptions(connection.BaseUrl, path => TodoTrackerHost.CreateLaunchUrl(services, path), mcpConfig, TimeZoneInfo.Local, connection.Token)
            {
                Attach = async (taskId, name, bytes) =>
                {
                    using var content = new MemoryStream(bytes);
                    var vault = services.GetRequiredService<TodoTracker.Core.Vault.VaultBoardStore>();
                    return Path.GetFileName((await vault.AddAttachmentAsync(taskId, name, content, TodoTracker.Core.Actor.User).ConfigureAwait(false)).Path);
                },
                OpenApp = appHost is null ? null : appHost.Open,
            });

        // Automated runs start floating and never touch the user's saved placement; --no-dock is a
        // session-only override (floating, nothing saved).
        var placementStore = interactive ? new WindowPlacementStore(Path.Combine(options.DataDirectory, "desktop.json")) : null;
        var placement = placementStore?.Load() ?? WindowPlacement.Default with { Mode = PlacementMode.Floating };
        if (args.NoDock)
        {
            placement = placement with { Mode = PlacementMode.Floating };
            placementStore = null;
        }

        if (services.GetService<TodoTracker.Server.Plugins.AgentChat.AgentChatService>() is { } chat)
        {
            _ask = new AgentChatAsk(chat);
            var shell = new WpfShell();
            _viewModel.Ask = new AskViewModel(_ask, run => Dispatcher.BeginInvoke(run), () =>
            {
                if (appHost is not null)
                {
                    appHost.Open("/#/ask");
                }
                else
                {
                    shell.OpenUrl(TodoTrackerHost.CreateLaunchUrl(services, "/?ask=1"));
                }
            });
        }

        var window = new MainWindow(_viewModel, services.GetRequiredService<SettingsStore>(), placementStore, placement, interactive);
        window.Vault = services.GetRequiredService<TodoTracker.Core.Vault.VaultBoardStore>();
        var plugins = services.GetRequiredService<TodoTracker.Server.Plugins.PluginHost>();
        window.Plugins = plugins;
        window.Sync = services.GetService<TodoTracker.Server.Plugins.Sync.SyncService>();
        _viewModel.ShowFocusTimer = plugins.IsRunning("focus-timer");
        window.AppHost = _appHost;
        MainWindow = window;
        _window = window;
        if (interactive)
        {
            // Full-screen breaks on every monitor (the focus timer plugin, and the user's choice in the menu).
            var viewModel = _viewModel;
            var breaks = new BreakOverlay(viewModel.Break, () => viewModel.ShowFocusTimer && (appHost?.State.FullScreenBreaks ?? true));
            _breaks = breaks;
            viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SidebarViewModel.ShowFocusTimer))
                {
                    breaks.Sync();
                }
            };
            if (appHost is not null)
            {
                appHost.NativeBreaks = () => viewModel.ShowFocusTimer && appHost.State.FullScreenBreaks;
                appHost.Changed += (_, _) => breaks.Sync();
            }

            // Quitting: the break windows go first, before shutdown closes every window.
            window.Closing += (_, _) => breaks.Dispose();
        }

        var events = services.GetRequiredService<ServerEvents>();
        if (toasts)
        {
            _toasts = new ToastService(_viewModel, window);
            events.Reminder += (_, n) => window.Dispatcher.BeginInvoke(() => ToastService.ShowReminder(n));
            events.Pomodoro += (_, p) => window.Dispatcher.BeginInvoke(() => ToastService.ShowFocus(p));
        }

        window.Show();
        await _viewModel.RefreshAsync().ConfigureAwait(true);
        if (serverProblem is not null)
        {
            _viewModel.StatusMessage = serverProblem;
        }

        // Never leave the screen edge reserved if the process dies or exits without closing the window.
        AppDomain.CurrentDomain.UnhandledException += (_, _) => window.ReleaseScreenEdge();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => window.ReleaseScreenEdge();
        if (args.Screenshot is { } shot && !args.SmokeTest)
        {
            // Developer/docs aid: render the sidebar to a PNG shortly after startup (works without an interactive desktop).
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                SaveScreenshot(window, shot);
            };
            timer.Start();
        }

        return window;
    }

    /// <summary>
    /// CI check that boots the real app (embedded server + WPF sidebar) against a temp board, creates a task through
    /// the HTTP API, and verifies the sidebar shows it and can complete it. Exit code 0 = pass.
    /// </summary>
    private async Task<int> RunSmokeTestAsync(StartupArgs args)
    {
        var log = new List<string>();
        var dataDir = args.DataDirectory ?? Path.Combine(Path.GetTempPath(), "tt-smoke-" + Guid.NewGuid().ToString("N"));
        var port = args.Port ?? FreePort();
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var window = await StartAsync(args with { DataDirectory = dataDir, Port = port }, token, interactive: false, toasts: false).ConfigureAwait(true);
            log.Add($"window loaded={window.IsLoaded}");

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            http.DefaultRequestHeaders.Authorization = new("Bearer", token);
            var health = await http.GetStringAsync(new Uri("/health", UriKind.Relative), timeout.Token).ConfigureAwait(true);
            log.Add($"health={health}");
            var page = await http.GetStringAsync(new Uri("/", UriKind.Relative), timeout.Token).ConfigureAwait(true);
            Check(page.Contains("Todo Tracker", StringComparison.Ordinal), "dashboard page served");

            using var created = await http.PostAsJsonAsync(new Uri("/api/items", UriKind.Relative), new { title = "Smoke test task", priority = "high" }, timeout.Token).ConfigureAwait(true);
            Check(created.StatusCode == HttpStatusCode.Created, $"create via API returned {(int)created.StatusCode}");

            while (_viewModel!.Focus?.Title != "Smoke test task")
            {
                timeout.Token.ThrowIfCancellationRequested();
                await Task.Delay(100, timeout.Token).ConfigureAwait(true);
            }

            log.Add("sidebar shows API-created task");
            await _viewModel.CompleteCommand.ExecuteAsync(_viewModel.Focus).ConfigureAwait(true);
            Check(_viewModel.Focus is null, "completing from sidebar clears focus");
            var dashboard = await http.GetFromJsonAsync<JsonElement>(new Uri("/api/dashboard", UriKind.Relative), timeout.Token).ConfigureAwait(true);
            Check(dashboard.GetProperty("now").GetArrayLength() == 0, "API sees sidebar completion");

            await CheckPlacementAsync(window, log, timeout.Token).ConfigureAwait(true);

            if (args.Screenshot is { } shot)
            {
                await SeedDemoAsync(http, timeout.Token).ConfigureAwait(true);
                await Task.Delay(1500, timeout.Token).ConfigureAwait(true);
                SaveScreenshot(window, shot);
                ThemeMode = ThemeMode.Dark;
                await Task.Delay(1000, timeout.Token).ConfigureAwait(true);
                SaveScreenshot(window, Path.ChangeExtension(shot, null) + "-dark.png");
            }

            _viewModel.IsCompact = true;
            Check(Math.Abs(window.Width - 56) < 1, "compact mode shrinks the window");
            log.Add("SMOKE OK");
            window.Close();
            return 0;
        }
#pragma warning disable CA1031 // Report any failure through the exit code and log.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.Add("SMOKE FAILED: " + ex);
            return 1;
        }
        finally
        {
            if (args.SmokeLog is { } path)
            {
                await File.WriteAllLinesAsync(path, log).ConfigureAwait(true);
            }
        }

        static void Check(bool condition, string what)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Check failed: " + what);
            }
        }
    }

    private static async Task SeedDemoAsync(HttpClient http, CancellationToken cancellationToken)
    {
        async Task<JsonElement> Post(string url, object body)
        {
            using var response = await http.PostAsJsonAsync(new Uri(url, UriKind.Relative), body, cancellationToken).ConfigureAwait(true);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(true);
        }

        var x = await Post("/api/items", new { title = "Roll out feature X", priority = "high" }).ConfigureAwait(true);
        var a = await Post("/api/items", new { title = "Feature A", parentId = x.GetProperty("id").GetString() }).ConfigureAwait(true);
        var b = await Post("/api/items", new { title = "Feature B", parentId = x.GetProperty("id").GetString() }).ConfigureAwait(true);
        await Post($"/api/items/{a.GetProperty("id").GetString()}/steps", new { titles = new List<string> { "A: ring 0", "A: ring 1", "A: ring 2" }, stepDelayMinutes = 1440 }).ConfigureAwait(true);
        var bSteps = await Post($"/api/items/{b.GetProperty("id").GetString()}/steps", new { titles = new List<string> { "B: ring 0", "B: ring 1" }, stepDelayMinutes = 1440 }).ConfigureAwait(true);
        var b0 = bSteps[0].GetProperty("id").GetString();
        await Post($"/api/items/{b0}/notes", new { text = "Ring 0 deployed, metrics green" }).ConfigureAwait(true);
        await Post($"/api/items/{b0}/complete", new { }).ConfigureAwait(true);
        var review = await Post("/api/items", new { title = "Reply to design review", priority = "critical" }).ConfigureAwait(true);
        await Post($"/api/items/{review.GetProperty("id").GetString()}/reminders", new { inMinutes = 0, message = "Review closes today" }).ConfigureAwait(true);
        await Post("/api/items", new { title = "Update onboarding doc", priority = "low" }).ConfigureAwait(true);
    }

    /// <summary>"light" or "dark" forces a theme; anything else follows Windows.</summary>
    private void ApplyTheme(string? theme) =>
        ThemeMode = theme?.ToLowerInvariant() switch
        {
            "light" => ThemeMode.Light,
            "dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };

    /// <summary>
    /// Exercises every placement on the real shell: floating + always on top, docking right and left (the Windows
    /// work area must shrink by the sidebar width), and floating again (the work area must be given back).
    /// </summary>
    private static async Task CheckPlacementAsync(MainWindow window, List<string> log, CancellationToken cancellationToken)
    {
        static void Check(bool condition, string what)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Placement check failed: " + what);
            }
        }

        async Task<Int32Rect> WaitForWorkArea(Func<Int32Rect, bool> condition)
        {
            for (var i = 0; i < 40; i++)
            {
                var area = DesktopSidebarHost.Monitors()[0].WorkArea;
                if (condition(area))
                {
                    return area;
                }

                await Task.Delay(100, cancellationToken).ConfigureAwait(true);
            }

            return DesktopSidebarHost.Monitors()[0].WorkArea;
        }

        var monitor = DesktopSidebarHost.Monitors()[0];
        var original = monitor.WorkArea;
        var width = (int)Math.Round(WindowPlacement.DefaultDockWidth * monitor.Scale);

        window.ApplyPlacement(WindowPlacement.Default with { Mode = PlacementMode.Floating, AlwaysOnTop = true });
        Check(!window.IsDocked && window.Topmost && window.ResizeMode == ResizeMode.CanResize, "floating + always on top");
        window.ApplyPlacement(window.Placement with { AlwaysOnTop = false });
        Check(!window.Topmost, "floating without always on top");
        log.Add("placement: floating ok");

        window.ApplyPlacement(window.Placement with { Mode = PlacementMode.Docked, Edge = DockEdge.Right });
        var right = await WaitForWorkArea(a => a.X + a.Width <= original.X + original.Width - width + 1).ConfigureAwait(true);
        Check(window.IsDocked && window.Topmost && window.ResizeMode == ResizeMode.NoResize, "docked right state");
        Check(right.X + right.Width <= original.X + original.Width - width + 1, $"work area shrinks on the right ({original} -> {right})");
        Check(Math.Abs(((window.Left + window.ActualWidth) * monitor.Scale) - (monitor.Bounds.X + monitor.Bounds.Width)) <= 2, "window hugs the right edge");
        log.Add($"placement: docked right ok (work area {original.Width}px -> {right.Width}px)");

        window.ApplyPlacement(window.Placement with { Edge = DockEdge.Left });
        var left = await WaitForWorkArea(a => a.X >= original.X + width - 1).ConfigureAwait(true);
        Check(window.IsDocked && left.X >= original.X + width - 1, $"work area shrinks on the left ({original} -> {left})");
        Check(Math.Abs((window.Left * monitor.Scale) - monitor.Bounds.X) <= 2, "window hugs the left edge");
        log.Add("placement: docked left ok");

        window.ApplyPlacement(window.Placement with { Mode = PlacementMode.Floating });
        var restored = await WaitForWorkArea(a => a.Equals(original)).ConfigureAwait(true);
        Check(!window.IsDocked && restored.Equals(original), $"work area restored after floating ({restored} vs {original})");
        log.Add("placement: undock restores work area ok");
    }

    internal static void SaveScreenshot(Window window, string path)
    {
        window.UpdateLayout();
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            System.Windows.Media.PixelFormats.Pbgra32);
        // Render the content (not the Window, whose chrome/backdrop renders blank off-screen) on an opaque background.
        var root = (FrameworkElement)window.Content;
        var visual = new System.Windows.Media.DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            ctx.DrawRectangle(window.TryFindResource("ApplicationBackgroundBrush") as System.Windows.Media.Brush ?? window.Background ?? System.Windows.Media.Brushes.White, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            ctx.DrawRectangle(new System.Windows.Media.VisualBrush(root) { Stretch = System.Windows.Media.Stretch.None, AlignmentX = System.Windows.Media.AlignmentX.Left, AlignmentY = System.Windows.Media.AlignmentY.Top, ViewboxUnits = System.Windows.Media.BrushMappingMode.Absolute, Viewbox = new Rect(root.RenderSize) }, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }

        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

internal sealed record StartupArgs(bool SmokeTest, string? SmokeLog, string? DataDirectory, int? Port, bool NoDock, string? Screenshot = null, string? Theme = null, bool Restart = false)
{
    public static StartupArgs Parse(string[] args)
    {
        var result = new StartupArgs(false, null, null, null, false);
        for (var i = 0; i < args.Length; i++)
        {
            result = args[i] switch
            {
                "--smoke-test" => result with { SmokeTest = true },
                "--smoke-log" when i + 1 < args.Length => result with { SmokeLog = args[++i] },
                "--data" when i + 1 < args.Length => result with { DataDirectory = args[++i] },
                "--port" when i + 1 < args.Length => result with { Port = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                "--no-dock" => result with { NoDock = true },
                "--restart" => result with { Restart = true },
                "--screenshot" when i + 1 < args.Length => result with { Screenshot = args[++i] },
                "--theme" when i + 1 < args.Length => result with { Theme = args[++i] },
                _ => result,
            };
        }

        return result;
    }
}










