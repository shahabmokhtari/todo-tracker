using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OneThing.Server.Plugins;
using OneThing.Server.Plugins.Connectors;

namespace OneThing.Server.GitHub;

/// <summary>GitHub Issues: groups in step with private GitHub repositories, their own or one they share (off by default; uses the GitHub CLI).</summary>
public sealed class GitHubConnectorPlugin : ITodoPlugin
{
    public static PluginInfo Definition { get; } = new(
        "connector-github",
        "GitHub Issues",
        "Keeps your groups in step with private GitHub repositories: tasks are issues, both ways. Each group gets its own repository (made with the GitHub CLI), or several share one, told apart by a group:<name> label.",
        DefaultEnabled: false,
        Live: true);

    public PluginInfo Info => Definition;

    public void ConfigureServices(IServiceCollection services, OneThingServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpClient(GitHubIssues.HttpClientName, c =>
        {
            c.BaseAddress = new Uri("https://api.github.com/");
            c.Timeout = TimeSpan.FromSeconds(60);
        });
        services.TryAddSingleton<IGitHubCli, GitHubCli>();
        services.AddSingleton<GitHubIssues>();
        services.AddHostedService<GitHubLoop>();
    }

    public void MapEndpoints(RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.AddEndpointFilter(ConnectorEndpoints.OutsideErrors);
        group.MapGet("/", (GitHubIssues g, CancellationToken ct) => g.ViewAsync(ct));
        group.MapPut("/every-group", (GitHubEveryGroupRequest request, GitHubIssues g, CancellationToken ct) => g.SetEveryGroupAsync(request.On, request.SharedRepo, ct));
        group.MapPost("/groups/{id:guid}/connect", (Guid id, GitHubConnectRequest? request, GitHubIssues g, CancellationToken ct) => g.ConnectAsync(id, request ?? new GitHubConnectRequest(null, null), ct));
        group.MapPost("/groups/{id:guid}/sync", (Guid id, GitHubIssues g, CancellationToken ct) => g.SyncAsync(id, ct));
        group.MapDelete("/groups/{id:guid}", (Guid id, GitHubIssues g, CancellationToken ct) => g.DisconnectAsync(id, ct));
        // Where the tasks live: only the person at this computer (the app's own session), like approving hooks.
        group.MapPut("/main-place", async (GitHubEveryGroupRequest request, HttpContext http, GitHubIssues g, CancellationToken ct) =>
            Security.IsAppSession(http)
                ? Results.Ok(await g.SetMainPlaceAsync(request.On, ct).ConfigureAwait(false))
                : Results.Problem("Only you can move where your tasks live: in Settings of the One Thing app.", statusCode: StatusCodes.Status403Forbidden));
    }
}
