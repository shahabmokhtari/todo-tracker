using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using TodoTracker.Core;
using TodoTracker.Core.Vault;
using TodoTracker.Desktop;
using TodoTracker.Server;

namespace TodoTracker.Tray;

/// <summary>
/// The tray app without its UI: runs Todo Tracker's server (the tasks folder, sync, the web app, MCP) and the view
/// model the tray menu reads. A second copy finds the first one running and only opens its window.
/// </summary>
internal sealed class TrayHost : IAsyncDisposable
{
    private readonly WebApplication _server;

    private TrayHost(WebApplication server, SidebarViewModel viewModel, string baseUrl)
    {
        _server = server;
        ViewModel = viewModel;
        BaseUrl = baseUrl;
    }

    public SidebarViewModel ViewModel { get; }

    public string BaseUrl { get; }

    /// <summary>A sign-in link to a page of the app (for the browser).</summary>
    public string LaunchUrl(string path) => TodoTrackerHost.CreateLaunchUrl(_server.Services, path);

    public static TodoTrackerServerOptions Options(TrayArgs args) => new()
    {
        DataDirectory = args.DataDirectory ?? TodoTrackerServerOptions.DefaultDataDirectory(),
        Port = args.Port ?? TodoTrackerServerOptions.DefaultPort,
    };

    /// <summary>Starts the server; null when another copy already runs it (its window was opened instead).</summary>
    public static async Task<TrayHost?> StartAsync(TodoTrackerServerOptions options, IDesktopShell shell)
    {
        ArgumentNullException.ThrowIfNull(options);
        WebApplication server;
        try
        {
            server = TodoTrackerHost.Build(TodoTrackerHost.CreateBuilder(options));
            await server.StartAsync().ConfigureAwait(false);
        }
        catch (StoreLockedException)
        {
            await OpenRunningAsync(options, shell).ConfigureAwait(false);
            return null;
        }

        var services = server.Services;
        var connection = TodoTrackerHost.GetConnection(services);
        var viewModel = new SidebarViewModel(
            services.GetRequiredService<IBoardStore>(),
            TimeProvider.System,
            shell,
            new SidebarOptions(connection.BaseUrl, path => TodoTrackerHost.CreateLaunchUrl(services, path), JsonSerializer.Serialize(connection.McpConfig), TimeZoneInfo.Local, connection.Token)
            {
                Attach = async (taskId, name, bytes) =>
                {
                    using var content = new MemoryStream(bytes);
                    var vault = services.GetRequiredService<VaultBoardStore>();
                    return Path.GetFileName((await vault.AddAttachmentAsync(taskId, name, content, Actor.User).ConfigureAwait(false)).Path);
                },
            });
        await viewModel.RefreshAsync().ConfigureAwait(false);
        return new TrayHost(server, viewModel, connection.BaseUrl);
    }

    /// <summary>Another copy runs the tasks folder: open its full window (with its token, from the data folder).</summary>
    private static async Task OpenRunningAsync(TodoTrackerServerOptions options, IDesktopShell shell)
    {
        var tokenFile = Path.Combine(options.DataDirectory, "api-token");
        if (!File.Exists(tokenFile))
        {
            return;
        }

        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(options.BaseUrl), Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await File.ReadAllTextAsync(tokenFile).ConfigureAwait(false)).Trim());
            using var response = await http.PostAsync(new Uri("/api/launch", UriKind.Relative), new StringContent("{\"return\":\"/\"}", System.Text.Encoding.UTF8, "application/json")).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("url", out var url) && url.GetString() is { } link)
            {
                shell.OpenUrl(link);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            // It's running but not answering: nothing more to do from here.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ViewModel.FlushNotesAsync().ConfigureAwait(false);
        ViewModel.Dispose();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await _server.StopAsync(stop.Token).ConfigureAwait(false);
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
