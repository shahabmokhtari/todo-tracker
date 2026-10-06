using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TodoTracker.Core;

namespace TodoTracker.Server.Tests;

/// <summary>Spins up the real server pipeline on an in-memory TestServer with a temp data directory.</summary>
public sealed class ServerFixture : IAsyncDisposable
{
    public const string Token = "test-token-0123456789abcdef";
    public static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    private ServerFixture(WebApplication app, string dataDirectory, FakeTimeProvider time, RecordingNotifier notifier, FakeHttpHandler teams)
    {
        App = app;
        DataDirectory = dataDirectory;
        Time = time;
        Notifier = notifier;
        Teams = teams;
    }

    public WebApplication App { get; }

    public string DataDirectory { get; }

    /// <summary>With an explicit data folder, the vault defaults to <c>&lt;data&gt;/vault</c>.</summary>
    public string VaultDirectory => Path.Combine(DataDirectory, "vault");

    public FakeTimeProvider Time { get; }

    public RecordingNotifier Notifier { get; }

    public FakeHttpHandler Teams { get; }

    public IBoardStore Store => App.Services.GetRequiredService<IBoardStore>();

    public static async Task<ServerFixture> StartAsync(Action<TodoTrackerServerOptions>? configure = null, Action<IServiceCollection>? services = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tt-server-" + Guid.NewGuid().ToString("N"));
        var options = new TodoTrackerServerOptions
        {
            DataDirectory = dir,
            ApiToken = Token,
            TimeZone = TimeZoneInfo.Utc,
            TickInterval = TimeSpan.FromHours(1),
            EnableBackgroundLoop = false,
            WatchVault = false,
            EnableHistory = false,
        };
        configure?.Invoke(options);
        var time = new FakeTimeProvider(T0);
        var notifier = new RecordingNotifier();
        var teams = new FakeHttpHandler();
        var builder = TodoTrackerHost.CreateBuilder(options);
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddSingleton<IReminderNotifier>(notifier);
        builder.Services.AddHttpClient(TeamsWebhookNotifier.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => teams);
        services?.Invoke(builder.Services);
        var app = TodoTrackerHost.Build(builder);
        await app.StartAsync();
        return new ServerFixture(app, dir, time, notifier, teams);
    }

    public HttpClient Client(bool authenticated = true, string? actor = null)
    {
        var client = App.GetTestClient();
        if (authenticated)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        }

        if (actor is not null)
        {
            client.DefaultRequestHeaders.Add("X-TodoTracker-Actor", actor);
        }

        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        try
        {
            // Git object files are read-only on Windows.
            foreach (var file in Directory.EnumerateFiles(DataDirectory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public sealed class RecordingNotifier : IReminderNotifier
{
    public List<ReminderNotification> Received { get; } = [];

    public Task NotifyAsync(ReminderNotification notification, CancellationToken cancellationToken)
    {
        Received.Add(notification);
        return Task.CompletedTask;
    }
}

public sealed class FakeHttpHandler : HttpMessageHandler
{
    public List<(Uri Uri, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request.RequestUri!, await request.Content!.ReadAsStringAsync(cancellationToken)));
        return new HttpResponseMessage(System.Net.HttpStatusCode.Accepted);
    }
}

public static class McpHelpers
{
    public static async Task<List<string>> ToolNamesAsync(ServerFixture server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var http = server.Client();
        var transport = new ModelContextProtocol.Client.HttpClientTransport(
            new ModelContextProtocol.Client.HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = ModelContextProtocol.Client.HttpTransportMode.StreamableHttp },
            http,
            ownsHttpClient: true);
        await using var mcp = await ModelContextProtocol.Client.McpClient.CreateAsync(transport);
        return (await mcp.ListToolsAsync()).Select(t => t.Name).ToList();
    }
}

public static class JsonHelpers
{
    public static async Task<JsonNode> Json(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!;
    }

    /// <summary>POST that must succeed and returns no body (204).</summary>
    public static async Task PostOk(this HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    public static async Task<JsonNode> PostJson(this HttpClient client, string url, object? body = null) =>
        await (await client.PostAsJsonAsync(url, body ?? new { })).Json();

    public static async Task<JsonNode> GetJson(this HttpClient client, string url) =>
        await (await client.GetAsync(url)).Json();

    public static List<string> Titles(this JsonNode? array) =>
        array!.AsArray().Select(n => n!["title"]!.GetValue<string>()).ToList();

    public static string Id(this JsonNode node) => node["id"]!.GetValue<string>();
}

