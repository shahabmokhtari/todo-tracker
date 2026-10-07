using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TodoTracker.Core;
using TodoTracker.Core.Connectors;

namespace TodoTracker.Server.Plugins.Connectors;

/// <summary>Syncs the connectors that are switched on, every few minutes.</summary>
public sealed partial class ConnectorLoop(ConnectorService connectors, TodoTrackerServerOptions options, ILogger<ConnectorLoop> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(10);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector sync failed")]
    private static partial void SyncFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.EnableBackgroundLoop)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await connectors.SyncAllAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ConnectorRunner.IsProblem(ex, stoppingToken))
                {
                    // Never stops the app: each connector reports its own problems; this is a last resort.
                    SyncFailed(logger, ex);
                }

                await Task.Delay(Every, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }
}

/// <summary>What every connector plugin adds: the shared service, its loop, its endpoints and panel.</summary>
public abstract class ConnectorPlugin : ITodoPlugin
{
    public abstract PluginInfo Info { get; }

    // Set up in Settings › Connected apps (always there, even before it's switched on).
    public string? WebModule => null;

    public void ConfigureServices(IServiceCollection services, TodoTrackerServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ConnectorService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ConnectorLoop>());
        AddKind(services, options);
    }

    protected abstract void AddKind(IServiceCollection services, TodoTrackerServerOptions options);
}

/// <summary>Notion (off by default).</summary>
public sealed class NotionConnectorPlugin : ConnectorPlugin
{
    public static PluginInfo Definition { get; } = new("connector-notion", "Notion", "Keeps a group in step with a Notion database, both ways (uses a Notion integration's token).", DefaultEnabled: false, Live: true);

    public override PluginInfo Info => Definition;

    protected override void AddKind(IServiceCollection services, TodoTrackerServerOptions options)
    {
        services.AddHttpClient(NotionConnector.HttpClientName, c =>
        {
            c.BaseAddress = new Uri("https://api.notion.com/");
            c.Timeout = TimeSpan.FromSeconds(60);
        });
        services.AddSingleton<NotionConnector>();
        services.AddSingleton<IConnectorKind>(sp => sp.GetRequiredService<NotionConnector>());
    }
}

/// <summary>Microsoft To Do (off by default).</summary>
public sealed class MicrosoftToDoConnectorPlugin : ConnectorPlugin
{
    public static PluginInfo Definition { get; } = new("connector-mstodo", "Microsoft To Do", "Keeps a group in step with a Microsoft To Do list, both ways (signs in to your Microsoft account).", DefaultEnabled: false, Live: true);

    public override PluginInfo Info => Definition;

    protected override void AddKind(IServiceCollection services, TodoTrackerServerOptions options)
    {
        services.AddHttpClient(MicrosoftToDoConnector.HttpClientName, c =>
        {
            c.BaseAddress = new Uri("https://graph.microsoft.com/");
            c.Timeout = TimeSpan.FromSeconds(60);
        });
        services.TryAddSingleton<Func<ConnectorFiles, IMicrosoftSignIn>>(_ => files => new MsalSignIn(files));
        services.AddSingleton<MicrosoftToDoConnector>();
        services.AddSingleton<IConnectorKind>(sp => sp.GetRequiredService<MicrosoftToDoConnector>());
    }
}

/// <summary>"Copy for Loop": a group's tasks as a checklist to paste into Microsoft Loop (Loop has no API to write to).</summary>
public sealed class LoopPlugin : ITodoPlugin
{
    public PluginInfo Info { get; } = new("loop", "Copy for Loop", "Copies a group's tasks as a checklist to paste into Microsoft Loop (or anything that takes markdown).");

    public string? WebModule => "/js/plugins/loop.js";

    public void ConfigureServices(IServiceCollection services, TodoTrackerServerOptions options)
    {
    }
}

/// <summary>The connectors panel's API (only when a connector plugin is on).</summary>
public static class ConnectorEndpoints
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Services.GetService<ConnectorService>() is null)
        {
            return;
        }

        var group = app.MapGroup("/api/plugins/connectors").AddEndpointFilter(ApiEndpoints.MapDomainErrors).AddEndpointFilter(OutsideErrors);
        group.MapGet("/", (ConnectorService c) => c.View);
        group.MapPut("/{id}", (string id, ConnectorConfigRequest request, ConnectorService c, CancellationToken ct) => c.ConfigureAsync(id, request, ct));
        group.MapPost("/{id}/preview", (string id, ConnectorService c, CancellationToken ct) => c.PreviewAsync(id, ct));
        group.MapPost("/{id}/sync", (string id, ConnectorService c) => c.SyncAsync(id));
        group.MapDelete("/{id}", (string id, ConnectorService c, CancellationToken ct) => c.DisconnectAsync(id, ct));
        group.MapPut("/notion/token", (ConnectorTokenRequest request, ConnectorService c, CancellationToken ct) => c.Kind<NotionConnector>().UseTokenAsync(request.Token ?? string.Empty, ct));
        group.MapGet("/notion/databases", (ConnectorService c, CancellationToken ct) => c.Kind<NotionConnector>().DatabasesAsync(ct));
        group.MapPost("/mstodo/signin", (ConnectorService c, CancellationToken ct) =>
        {
            var todo = c.Kind<MicrosoftToDoConnector>();
            return todo.SignIn.StartAsync(todo.Files.Settings.ClientId ?? throw new InvalidOperationException("Add the app registration's client id first."), ct);
        });
        group.MapGet("/mstodo/signin", (ConnectorService c) => c.Kind<MicrosoftToDoConnector>().SignIn.View);
        group.MapGet("/mstodo/lists", (ConnectorService c, CancellationToken ct) => c.Kind<MicrosoftToDoConnector>().ListsAsync(ct));
    }

    /// <summary>What Notion or Microsoft said: a refused token is the person's to fix (400); anything else is theirs (502).</summary>
    private static async ValueTask<object?> OutsideErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            var refused = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            return Results.Problem(ex.Message, statusCode: refused ? StatusCodes.Status400BadRequest : StatusCodes.Status502BadGateway);
        }
    }
}
