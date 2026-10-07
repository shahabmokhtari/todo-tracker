using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TodoTracker.Core;
using TodoTracker.Core.Connectors;

namespace TodoTracker.Server.Plugins.Connectors;

/// <summary>A kind of connector (Notion, Microsoft To Do): how to reach its list, and what's missing before it can.</summary>
public interface IConnectorKind
{
    string Id { get; }

    string Name { get; }

    ConnectorFiles Files { get; }

    /// <summary>What to do before it can sync, or null when it's ready.</summary>
    string? Missing();

    Task<IConnectorRemote> ConnectAsync(CancellationToken cancellationToken);
}

public sealed class NotionConnector(IHttpClientFactory http, TodoTrackerServerOptions options) : IConnectorKind
{
    public const string HttpClientName = "notion";

    public string Id => "notion";

    public string Name => "Notion";

    public ConnectorFiles Files { get; } = new(options.DataDirectory, "notion");

    public string? Missing() =>
        Files.Secret is null ? "Paste your Notion integration's token."
        : Files.Settings.Target is null ? "Pick the Notion database."
        : Files.Settings.GroupId is null ? "Pick the group to keep in step." : null;

    public async Task<IConnectorRemote> ConnectAsync(CancellationToken cancellationToken) =>
        await NotionRemote.ConnectAsync(Api(Files.Secret ?? throw new InvalidOperationException(Missing())), Files.Settings.Target ?? throw new InvalidOperationException(Missing()), cancellationToken).ConfigureAwait(false);

    /// <summary>Checks a token by listing the databases it can see; saves it when it works.</summary>
    public async Task<IReadOnlyList<NotionDatabase>> UseTokenAsync(string token, CancellationToken cancellationToken)
    {
        var clean = token?.Trim() ?? string.Empty;
        if (clean.Length is < 10 or > 200)
        {
            throw new ArgumentException("That doesn't look like a Notion integration token (it starts with secret_ or ntn_).", nameof(token));
        }

        using var api = Api(clean);
        var databases = await api.DatabasesAsync(cancellationToken).ConfigureAwait(false);
        Files.SaveSecret(clean);
        return databases;
    }

    public async Task<IReadOnlyList<NotionDatabase>> DatabasesAsync(CancellationToken cancellationToken)
    {
        using var api = Api(Files.Secret ?? throw new InvalidOperationException("Paste your Notion integration's token first."));
        return await api.DatabasesAsync(cancellationToken).ConfigureAwait(false);
    }

    private NotionApi Api(string token) => new(http.CreateClient(HttpClientName), token);
}

public sealed class MicrosoftToDoConnector : IConnectorKind
{
    public const string HttpClientName = "graph";
    private readonly IHttpClientFactory _http;

    public MicrosoftToDoConnector(IHttpClientFactory http, TodoTrackerServerOptions options, Func<ConnectorFiles, IMicrosoftSignIn> signIn)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(signIn);
        _http = http;
        Files = new ConnectorFiles(options.DataDirectory, "mstodo");
        SignIn = signIn(Files);
    }

    public string Id => "mstodo";

    public string Name => "Microsoft To Do";

    public ConnectorFiles Files { get; }

    public IMicrosoftSignIn SignIn { get; }

    public string? Missing() =>
        Files.Settings.ClientId is null ? "Add the app registration's client id (see the setup steps)."
        : !SignIn.SignedIn ? "Sign in to Microsoft."
        : Files.Settings.Target is null ? "Pick the To Do list."
        : Files.Settings.GroupId is null ? "Pick the group to keep in step." : null;

    public Task<IConnectorRemote> ConnectAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IConnectorRemote>(new MicrosoftToDoRemote(_http.CreateClient(HttpClientName), SignIn.TokenAsync, Files.Settings.Target ?? throw new InvalidOperationException(Missing())));

    public Task<IReadOnlyList<ToDoList>> ListsAsync(CancellationToken cancellationToken) =>
        MicrosoftToDoRemote.ListsAsync(_http.CreateClient(HttpClientName), SignIn.TokenAsync, cancellationToken);
}

public sealed record ConnectorView(
    string Id,
    string Name,
    string? Missing,
    string? Target,
    string? TargetName,
    Guid? GroupId,
    ConnectorDirection Direction,
    bool Enabled,
    DateTimeOffset? LastRun,
    string? LastResult,
    IReadOnlyList<string> Problems,
    int Linked,
    string? ClientId,
    MicrosoftSignInView? SignIn,
    bool HasToken);

public sealed record ConnectorConfigRequest(string? Target, string? TargetName, Guid? GroupId, ConnectorDirection? Direction, bool? Enabled, string? ClientId);

/// <summary>What a sync would do, counted (shown before the first one).</summary>
public sealed record ConnectorPreview(int AddHere, int SendThere, int UpdateHere, int UpdateThere, int Archive, int Linked);

public sealed record ConnectorTokenRequest(string? Token);

/// <summary>Runs the connectors: on request and every few minutes for those switched on. One run at a time.</summary>
public sealed class ConnectorService(IEnumerable<IConnectorKind> kinds, IBoardStore store, TimeProvider time, TodoTrackerServerOptions options) : IDisposable
{
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly IReadOnlyList<IConnectorKind> _kinds = [.. kinds];

    public IReadOnlyList<ConnectorView> View => [.. _kinds.Select(ViewOf)];

    public T Kind<T>()
        where T : IConnectorKind => _kinds.OfType<T>().FirstOrDefault() ?? throw new KeyNotFoundException("That connector is switched off (Plugins).");

    public void Dispose() => _running.Dispose();

    public async Task<ConnectorView> ConfigureAsync(string id, ConnectorConfigRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var kind = Get(id);
        if (request.GroupId is { } g && !await store.ReadAsync(b => b.Groups.Any(x => x.Id == g), cancellationToken).ConfigureAwait(false))
        {
            throw new KeyNotFoundException("There is no such group.");
        }

        if (request.ClientId is { } clientId && !Guid.TryParse(clientId.Trim(), out _))
        {
            throw new ArgumentException("A client id looks like 00000000-0000-0000-0000-000000000000.", nameof(request));
        }

        kind.Files.Update(s =>
        {
            var targetChanged = request.Target is not null && request.Target != s.Target;
            if (targetChanged)
            {
                // Another list: its links start over (its items carry task ids, so nothing is copied).
                kind.Files.SaveLinks([]);
            }

            return s with
            {
                Target = request.Target ?? s.Target,
                TargetName = request.TargetName ?? (targetChanged ? null : s.TargetName),
                GroupId = request.GroupId ?? s.GroupId,
                Direction = request.Direction ?? s.Direction,
                Enabled = request.Enabled ?? (targetChanged ? false : s.Enabled),
                ClientId = request.ClientId?.Trim() ?? s.ClientId,
            };
        });
        return ViewOf(kind);
    }

    public async Task<ConnectorPreview> PreviewAsync(string id, CancellationToken cancellationToken)
    {
        var kind = Ready(id);
        var remote = await kind.ConnectAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plan = await Runner(kind).PlanAsync(remote, Target(kind), kind.Files.Links, cancellationToken).ConfigureAwait(false);
            return new ConnectorPreview(
                plan.Ops.OfType<CreateLocal>().Count(),
                plan.Ops.OfType<CreateRemote>().Count(),
                plan.Ops.OfType<UpdateLocal>().Count(),
                plan.Ops.OfType<UpdateRemote>().Count(),
                plan.Ops.OfType<ArchiveRemote>().Count(),
                plan.Links.Count(l => l.State == LinkState.Active));
        }
        finally
        {
            (remote as IDisposable)?.Dispose();
        }
    }

    /// <summary>Syncs now (and from then on every few minutes).</summary>
    public async Task<ConnectorView> SyncAsync(string id, CancellationToken cancellationToken)
    {
        var kind = Ready(id);
        await _running.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RunAsync(kind, cancellationToken).ConfigureAwait(false);
            kind.Files.Update(s => s with { Enabled = true });
        }
        finally
        {
            _running.Release();
        }

        return ViewOf(kind);
    }

    public async Task SyncAllAsync(CancellationToken cancellationToken)
    {
        foreach (var kind in _kinds.Where(k => k.Files.Settings.Enabled && k.Missing() is null))
        {
            await _running.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await RunAsync(kind, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _running.Release();
            }
        }
    }

    public ConnectorView Disconnect(string id)
    {
        var kind = Get(id);
        var clientId = kind.Files.Settings.ClientId;
        (kind as MicrosoftToDoConnector)?.SignIn.SignOut();
        kind.Files.Clear();
        if (clientId is not null)
        {
            kind.Files.Update(s => s with { ClientId = clientId });
        }

        return ViewOf(kind);
    }

    private async Task RunAsync(IConnectorKind kind, CancellationToken cancellationToken)
    {
        try
        {
            var remote = await kind.ConnectAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var run = await Runner(kind).RunAsync(remote, Target(kind), kind.Files.Links, cancellationToken).ConfigureAwait(false);
                kind.Files.SaveLinks(run.Links);
                kind.Files.Update(s => s with { LastRun = time.GetUtcNow(), LastResult = Summary(run.Plan, run.Failed), Problems = run.Problems });
            }
            finally
            {
                (remote as IDisposable)?.Dispose();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            kind.Files.Update(s => s with { LastRun = time.GetUtcNow(), LastResult = "Couldn't sync", Problems = [ex.Message] });
        }
    }

    private static string Summary(ConnectorPlanResult plan, int failed)
    {
        var parts = new List<string>();
        void Add(int n, string what)
        {
            if (n > 0)
            {
                parts.Add($"{n} {what}");
            }
        }

        Add(plan.Ops.OfType<CreateLocal>().Count(), "added here");
        Add(plan.Ops.OfType<CreateRemote>().Count(), "sent");
        Add(plan.Ops.OfType<UpdateLocal>().Count() + plan.Ops.OfType<UpdateRemote>().Count(), "updated");
        Add(plan.Ops.OfType<ArchiveRemote>().Count(), "archived");
        Add(failed, "didn't go through");
        return parts.Count == 0 ? "Up to date" : string.Join(", ", parts);
    }

    private ConnectorRunner Runner(IConnectorKind kind) => new(kind.Id, kind.Name, store, time, options.TimeZone);

    private static ConnectorTarget Target(IConnectorKind kind) => new(kind.Files.Settings.GroupId!.Value, kind.Files.Settings.Direction);

    private IConnectorKind Get(string id) => _kinds.FirstOrDefault(k => k.Id == id) ?? throw new KeyNotFoundException($"There is no connector \"{id}\" (or it's switched off in Plugins).");

    private IConnectorKind Ready(string id)
    {
        var kind = Get(id);
        return kind.Missing() is { } missing ? throw new InvalidOperationException(missing) : kind;
    }

    private static ConnectorView ViewOf(IConnectorKind kind)
    {
        var s = kind.Files.Settings;
        return new ConnectorView(
            kind.Id,
            kind.Name,
            kind.Missing(),
            s.Target,
            s.TargetName,
            s.GroupId,
            s.Direction,
            s.Enabled,
            s.LastRun,
            s.LastResult,
            s.Problems,
            kind.Files.Links.Count(l => l.State == LinkState.Active),
            s.ClientId,
            (kind as MicrosoftToDoConnector)?.SignIn.View,
            kind.Files.Secret is not null);
    }
}

/// <summary>Syncs the connectors that are switched on, every few minutes.</summary>
public sealed class ConnectorLoop(ConnectorService connectors, TodoTrackerServerOptions options) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(10);

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
                await connectors.SyncAllAsync(stoppingToken).ConfigureAwait(false);
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

    public string? WebModule => "/js/plugins/connectors.js";

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
    public override PluginInfo Info { get; } = new("connector-notion", "Notion", "Keeps a group in step with a Notion database, both ways (uses a Notion integration's token).", DefaultEnabled: false);

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
    public override PluginInfo Info { get; } = new("connector-mstodo", "Microsoft To Do", "Keeps a group in step with a Microsoft To Do list, both ways (signs in to your Microsoft account).", DefaultEnabled: false);

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
        group.MapPost("/{id}/sync", (string id, ConnectorService c, CancellationToken ct) => c.SyncAsync(id, ct));
        group.MapDelete("/{id}", (string id, ConnectorService c) => c.Disconnect(id));
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
