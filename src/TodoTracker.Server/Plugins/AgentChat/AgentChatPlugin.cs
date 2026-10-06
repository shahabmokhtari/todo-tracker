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

public sealed record ChatRenameRequest(string? Title);

/// <summary>An API model to add: preset (openai, azure, openrouter, ollama, lmstudio, anthropic, …), name, address, model, key.</summary>
public sealed record ApiModelRequest(string? Preset, string? Name, string? BaseUrl, string? Model, string? Key);

/// <summary>An API model as screens see it: never its key, only whether it has one.</summary>
public sealed record ApiModelView(string Id, string Preset, string Name, string BaseUrl, string Model, bool HasKey, bool Local);

/// <summary>"Ask AI": a chat in the app with Copilot or Claude, which change your tasks through Todo Tracker's tools.</summary>
public sealed class AgentChatPlugin : ITodoPlugin
{
    /// <summary>Tests point this at a scripted agent (a .dll run with dotnet).</summary>
    public const string TestAgentVariable = "TODOTRACKER_TEST_AGENT";

    /// <summary>Where to look for agents instead of the PATH (tests use an empty folder).</summary>
    public const string AgentPathVariable = "TODOTRACKER_AGENT_PATH";

    public PluginInfo Info { get; } = new("agent-chat", "Ask AI", "Chat with GitHub Copilot, Claude Code or a model with an API key to add, find and organize tasks. Chats are kept.");

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
        services.AddSingleton(sp => new ApiModelStore(Path.Combine(options.DataDirectory, "agent-chat")));
        services.AddSingleton(sp =>
        {
            var vault = sp.GetRequiredService<VaultBoardStore>();
            var tt = options.TtPath ?? AiSetups.FindTt();
            var dir = Path.Combine(options.DataDirectory, "agent-chat");
            var token = sp.GetRequiredService<ApiToken>().Value;
            return new AgentChatService(sp.GetRequiredService<AgentCatalog>(), new AgentChatOptions
            {
                WorkDirectory = Path.Combine(dir, "work"),
                SettingsPath = Path.Combine(dir, "settings.json"),
                History = new ChatHistoryStore(Path.Combine(dir, "chats"), sp.GetRequiredService<TimeProvider>()),
                Models = sp.GetRequiredService<ApiModelStore>(),
                Time = sp.GetRequiredService<TimeProvider>(),

                // API models use the app's own MCP tools, named after the model (so its changes are credited to it).
                ToolsFor = model => new McpChatTools(
                    () => new HttpClient { BaseAddress = new Uri(options.BaseUrl), DefaultRequestHeaders = { Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token) } },
                    model.Name),

                // The agent gets the same tasks the app shows: this vault, this data folder.
                Mcp = tt == "tt" && !OnPath("tt") ? null : new AgentLaunch("todo-tracker", tt, ["mcp"], dir, new Dictionary<string, string?>
                {
                    ["TODOTRACKER_VAULT"] = vault.RootPath,
                    ["TODOTRACKER_DATA"] = options.DataDirectory,
                }),
                McpHttp = new McpHttpServer(options.BaseUrl + "/mcp", token),
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

        // Chats: the list (newest first), search, open, rename, delete (with undo).
        group.MapGet("/chats", (string? q, AgentChatService chat) => string.IsNullOrWhiteSpace(q) ? chat.Chats : chat.Search(q));
        group.MapPost("/chats/{id}/open", async (string id, AgentChatService chat) =>
        {
            await chat.OpenChatAsync(id).ConfigureAwait(false);
            return chat.State;
        });
        group.MapPut("/chats/{id}", (string id, ChatRenameRequest request, AgentChatService chat) =>
        {
            chat.RenameChat(id, request.Title ?? string.Empty);
            return chat.State;
        });
        group.MapDelete("/chats/{id}", async (string id, AgentChatService chat) =>
            await chat.DeleteChatAsync(id).ConfigureAwait(false) ? Results.Ok(chat.State) : Results.NotFound());
        group.MapPost("/chats/{id}/restore", (string id, AgentChatService chat) =>
            chat.RestoreChat(id) ? Results.Ok(chat.State) : Results.Problem("That chat can't be brought back any more.", statusCode: StatusCodes.Status404NotFound));

        // API models (keys go in, never come out).
        group.MapGet("/models", (ApiModelStore models) => models.List().Select(m => View(m, models)));
        group.MapPost("/models", (ApiModelRequest request, ApiModelStore models) =>
        {
            var model = ApiModel.Create(request.Preset ?? string.Empty, request.Name ?? string.Empty, request.BaseUrl ?? string.Empty, request.Model ?? string.Empty);
            if (!model.IsLocal && string.IsNullOrWhiteSpace(request.Key))
            {
                throw new ArgumentException("Paste the API key for this service.", nameof(request));
            }

            models.Add(model, request.Key);
            return View(model, models);
        });
        group.MapDelete("/models/{id}", (string id, ApiModelStore models) => models.Remove(id) ? Results.NoContent() : Results.NotFound());
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

    private static ApiModelView View(ApiModel m, ApiModelStore models) => new(m.Id, m.Preset, m.Name, m.BaseUrl, m.Model, models.HasKey(m.Id), m.IsLocal);

    private static bool OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, OperatingSystem.IsWindows() ? name + ".exe" : name)));
}
