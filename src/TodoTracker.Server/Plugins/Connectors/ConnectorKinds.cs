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

    /// <summary>The plugin that switches it on and off.</summary>
    string PluginId { get; }

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

    public string PluginId => NotionConnectorPlugin.Definition.Id;

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
    private readonly TimeZoneInfo _zone;

    public MicrosoftToDoConnector(IHttpClientFactory http, TodoTrackerServerOptions options, Func<ConnectorFiles, IMicrosoftSignIn> signIn)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(signIn);
        _http = http;
        _zone = options.TimeZone;
        Files = new ConnectorFiles(options.DataDirectory, "mstodo");
        SignIn = signIn(Files);
    }

    public string Id => "mstodo";

    public string PluginId => MicrosoftToDoConnectorPlugin.Definition.Id;

    public string Name => "Microsoft To Do";

    public ConnectorFiles Files { get; }

    public IMicrosoftSignIn SignIn { get; }

    public string? Missing() =>
        Files.Settings.ClientId is null ? "Add the app registration's client id (see the setup steps)."
        : !SignIn.SignedIn ? "Sign in to Microsoft."
        : Files.Settings.Target is null ? "Pick the To Do list."
        : Files.Settings.GroupId is null ? "Pick the group to keep in step." : null;

    public Task<IConnectorRemote> ConnectAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IConnectorRemote>(new MicrosoftToDoRemote(_http.CreateClient(HttpClientName), SignIn.TokenAsync, Files.Settings.Target ?? throw new InvalidOperationException(Missing()), _zone));

    public Task<IReadOnlyList<ToDoList>> ListsAsync(CancellationToken cancellationToken) =>
        MicrosoftToDoRemote.ListsAsync(_http.CreateClient(HttpClientName), SignIn.TokenAsync, cancellationToken);
}
