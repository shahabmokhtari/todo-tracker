using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TodoTracker.Core.Vault;

namespace TodoTracker.Server.Plugins.AgentChat;

public sealed record ChatMessageRequest(string? Text);

public sealed record ChatSelectRequest(string? Agent);

public sealed record ChatAnswerRequest(string? EntryId, string? Choice);

/// <summary>"Ask AI": a chat in the app with Copilot or Claude, which change your tasks through Todo Tracker's tools.</summary>
public sealed class AgentChatPlugin : ITodoPlugin
{
    /// <summary>Tests point this at a scripted agent (a .dll run with dotnet).</summary>
    public const string TestAgentVariable = "TODOTRACKER_TEST_AGENT";

    /// <summary>Where to look for agents instead of the PATH (tests use an empty folder).</summary>
    public const string AgentPathVariable = "TODOTRACKER_AGENT_PATH";

    public PluginInfo Info { get; } = new("agent-chat", "Ask AI", "Chat with GitHub Copilot or Claude Code to add, find and organize tasks.");

    public string? WebModule => "/js/plugins/agent-chat.js";

    public void ConfigureServices(IServiceCollection services, TodoTrackerServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(_ =>
        {
            var catalog = new AgentCatalog(Environment.GetEnvironmentVariable(AgentPathVariable));
            if (Environment.GetEnvironmentVariable(TestAgentVariable) is { Length: > 0 } fake)
            {
                catalog.Extra.Add((new AgentOption("test", "Test agent", true, null), work =>
                    new AgentLaunch("test", "dotnet", [fake], work, new Dictionary<string, string?>())));
            }

            return catalog;
        });
        services.AddSingleton(sp =>
        {
            var vault = sp.GetRequiredService<VaultBoardStore>();
            var tt = options.TtPath ?? AiSetups.FindTt();
            var dir = Path.Combine(options.DataDirectory, "agent-chat");
            return new AgentChatService(sp.GetRequiredService<AgentCatalog>(), new AgentChatOptions
            {
                WorkDirectory = Path.Combine(dir, "work"),
                SettingsPath = Path.Combine(dir, "settings.json"),

                // The agent gets the same tasks the app shows: this vault, this data folder.
                Mcp = tt == "tt" && !OnPath("tt") ? null : new AgentLaunch("todo-tracker", tt, ["mcp"], dir, new Dictionary<string, string?>
                {
                    ["TODOTRACKER_VAULT"] = vault.RootPath,
                    ["TODOTRACKER_DATA"] = options.DataDirectory,
                }),
                McpHttp = new McpHttpServer(options.BaseUrl + "/mcp", sp.GetRequiredService<ApiToken>().Value),
            });
        });
    }

    public void MapEndpoints(RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapGet("/", (AgentChatService chat) =>
        {
            // The panel was opened: look for newly installed agents (slow, so not on every update).
            chat.RefreshAgents();
            return chat.State;
        });
        group.MapPost("/select", async (ChatSelectRequest request, AgentChatService chat) =>
        {
            await chat.SelectAsync(request.Agent ?? string.Empty).ConfigureAwait(false);
            return chat.State;
        });
        group.MapPost("/message", (ChatMessageRequest request, AgentChatService chat) =>
        {
            // The answer streams through /stream; this only starts it (busy or no agent → 409 with the reason).
            _ = chat.SendAsync(request.Text ?? string.Empty);
            return Results.Accepted(value: chat.State);
        });
        group.MapPost("/cancel", async (AgentChatService chat) =>
        {
            await chat.CancelAsync().ConfigureAwait(false);
            return chat.State;
        });
        group.MapPost("/new", async (AgentChatService chat) =>
        {
            await chat.NewChatAsync().ConfigureAwait(false);
            return chat.State;
        });
        group.MapPost("/answer", async (ChatAnswerRequest request, AgentChatService chat) =>
        {
            await chat.AnswerAsync(request.EntryId ?? string.Empty, request.Choice ?? string.Empty).ConfigureAwait(false);
            return chat.State;
        });
        group.MapGet("/stream", (AgentChatService chat, HttpContext http, IHostApplicationLifetime lifetime) =>
        {
            // Ends when the page goes away or the app shuts down (an open stream would hold up a restart).
            var stop = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, lifetime.ApplicationStopping);
            http.Response.RegisterForDispose(stop);
            return TypedResults.ServerSentEvents(Stream(chat, stop.Token), eventType: "state");
        });
    }

    /// <summary>The chat state now and after every change; only the latest state is kept, so a slow reader never lags.</summary>
    private static async IAsyncEnumerable<ChatState> Stream(AgentChatService chat, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var updates = Channel.CreateBounded<ChatState>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        void OnChanged(object? sender, ChatState state) => updates.Writer.TryWrite(state);
        chat.Changed += OnChanged;
        try
        {
            yield return chat.State;
            while (!cancellationToken.IsCancellationRequested)
            {
                ChatState next;
                try
                {
                    next = await updates.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }

                yield return next;
            }
        }
        finally
        {
            chat.Changed -= OnChanged;
        }
    }

    private static bool OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, OperatingSystem.IsWindows() ? name + ".exe" : name)));
}
