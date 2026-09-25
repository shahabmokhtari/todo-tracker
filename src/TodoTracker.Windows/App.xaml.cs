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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = StartupArgs.Parse(e.Args);
        try
        {
            if (args.SmokeTest)
            {
                Shutdown(await RunSmokeTestAsync(args).ConfigureAwait(true));
                return;
            }

            await StartAsync(args).ConfigureAwait(true);
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

    protected override void OnExit(ExitEventArgs e)
    {
        _toasts?.Dispose();
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

        base.OnExit(e);
    }

    private async Task<MainWindow> StartAsync(StartupArgs args, string? token = null, bool dock = true, bool toasts = true)
    {
        var options = new TodoTrackerServerOptions
        {
            DataDirectory = args.DataDirectory ?? TodoTrackerServerOptions.DefaultDataDirectory(),
            Port = args.Port ?? TodoTrackerServerOptions.DefaultPort,
            ApiToken = token,
        };
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
        var connection = TodoTrackerHost.GetConnection(services);
        var mcpConfig = JsonSerializer.Serialize(connection.McpConfig, Indented);
        _viewModel = new SidebarViewModel(
            services.GetRequiredService<IBoardStore>(),
            TimeProvider.System,
            new WpfShell(),
            new SidebarOptions(connection.BaseUrl, connection.LaunchUrl, mcpConfig, TimeZoneInfo.Local, connection.Token));

        var window = new MainWindow(_viewModel, services.GetRequiredService<SettingsStore>(), dockOnStart: dock && !args.NoDock);
        MainWindow = window;

        var events = services.GetRequiredService<ServerEvents>();
        if (toasts)
        {
            _toasts = new ToastService(_viewModel, window);
            events.Reminder += (_, n) => window.Dispatcher.BeginInvoke(() => ToastService.ShowReminder(n));
            events.Pomodoro += (_, p) => window.Dispatcher.BeginInvoke(() => ToastService.ShowFocus(p));
        }

        window.Show();
        await _viewModel.RefreshAsync().ConfigureAwait(true);
        _viewModel.StatusMessage = serverProblem;
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
            var window = await StartAsync(args with { DataDirectory = dataDir, Port = port }, token, dock: false, toasts: false).ConfigureAwait(true);
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

            if (args.Screenshot is { } shot)
            {
                await SeedDemoAsync(http, timeout.Token).ConfigureAwait(true);
                await Task.Delay(1500, timeout.Token).ConfigureAwait(true);
                SaveScreenshot(window, shot);
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
            ctx.DrawRectangle(new System.Windows.Media.VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
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

internal sealed record StartupArgs(bool SmokeTest, string? SmokeLog, string? DataDirectory, int? Port, bool NoDock, string? Screenshot = null)
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
                "--screenshot" when i + 1 < args.Length => result with { Screenshot = args[++i] },
                _ => result,
            };
        }

        return result;
    }
}










