using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace TodoTracker.Server.Plugins.AgentChat;

/// <summary>The running app's MCP endpoint (for agents that only take MCP servers over HTTP, like Copilot).</summary>
public sealed record McpHttpServer(string Url, string Token);

public sealed class AgentChatOptions
{
    /// <summary>The agent's working folder (its own, never the tasks folder: its file and shell tools start there).</summary>
    public required string WorkDirectory { get; init; }

    /// <summary>Where the chosen agent is remembered.</summary>
    public string? SettingsPath { get; init; }

    /// <summary>The Todo Tracker MCP server over stdio (<c>tt mcp</c>); null when tt isn't available.</summary>
    public AgentLaunch? Mcp { get; init; }

    /// <summary>The app's own MCP endpoint, preferred when the agent supports HTTP MCP servers.</summary>
    public McpHttpServer? McpHttp { get; init; }

    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(120);

    public TimeSpan TurnTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>An unanswered permission request is refused after this.</summary>
    public TimeSpan PermissionTimeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>One line in the chat. Kinds: user, agent, tool, permission, note. Choices are for permission requests.</summary>
public sealed record ChatEntry(string Id, string Kind, string Text, string? Status = null, IReadOnlyList<string>? Choices = null);

/// <summary>
/// What the chat shows. Status: <c>idle</c> (agent not started), <c>starting</c>, <c>ready</c>, <c>busy</c> (answering),
/// <c>error</c> (it stopped; the next message starts it again).
/// </summary>
public sealed record ChatState(string Status, string? Agent, IReadOnlyList<AgentOption> Agents, IReadOnlyList<ChatEntry> Entries, string? Problem);

/// <summary>
/// Chat with Copilot or Claude about your tasks. The agent starts on the first message and then stays warm; every
/// session gets Todo Tracker's MCP tools. Reading tasks needs no permission; changing them asks once (or for the rest
/// of the chat); anything else (shell commands, files) always asks, and an unanswered request is refused.
/// </summary>
public sealed partial class AgentChatService : IAsyncDisposable
{
    private const string Context =
        "[Context from Todo Tracker, not from the user] You are the assistant inside Todo Tracker, the user's ADHD-friendly task list. " +
        "Use the todo-tracker tools to look at and change their tasks (never edit files or run commands for that). " +
        "Capture what they ask for in their words, make big things small, keep replies short: what you did and the one next step.";

    private const int StartAttempts = 3;
    private static readonly (HashSet<string> All, HashSet<string> ReadOnly) TodoTools = FindTools();

    private readonly AgentCatalog _catalog;
    private readonly AgentChatOptions _options;
    private readonly Lock _lock = new();
    private readonly List<ChatEntry> _entries = [];
    private readonly Dictionary<string, TaskCompletionSource<string>> _questions = [];
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private AcpConnection? _agent;
    private string? _sessionId;
    private bool _contextSent;
    private bool _changesAllowed;
    private bool _agentTakesHttp;
    private bool _useSignInFallback;
    private string _status = "idle";
    private string? _problem;
    private string? _choice;
    private string? _messageId;
    private int _nextEntry;

    public AgentChatService(AgentCatalog catalog, AgentChatOptions options)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _choice = AgentCatalog.DefaultChoice(catalog.Detect(), LoadChoice());
    }

    /// <summary>Raised (on a background thread) whenever something the chat shows changed.</summary>
    public event EventHandler<ChatState>? Changed;

    public ChatState State
    {
        get
        {
            lock (_lock)
            {
                return new ChatState(_status, _choice, _catalog.Detect(), [.. _entries], _problem);
            }
        }
    }

    /// <summary>Switches agents (the next message starts the chosen one) and remembers the choice.</summary>
    public async Task SelectAsync(string agentId)
    {
        var agents = _catalog.Detect();
        var agent = agents.FirstOrDefault(a => a.Id == agentId) ?? throw new ArgumentException($"Unknown agent \"{agentId}\".", nameof(agentId));
        if (!agent.Installed)
        {
            throw new InvalidOperationException(agent.Hint ?? $"{agent.Name} isn't installed.");
        }

        await StopAgentAsync().ConfigureAwait(false);
        lock (_lock)
        {
            _useSignInFallback = false;
            _choice = agentId;
            _status = "idle";
            _problem = null;
        }

        SaveChoice(agentId);
        Notify();
    }

    /// <summary>Sends a message; the returned task completes when the answer is finished.</summary>
    /// <exception cref="InvalidOperationException">Another answer is still coming, or no agent is installed.</exception>
    public Task SendAsync(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        lock (_lock)
        {
            if (_status is "busy" or "starting")
            {
                throw new InvalidOperationException("Wait for the answer (or stop it) first.");
            }

            if (_choice is null)
            {
                var hint = _catalog.Detect().Select(a => a.Hint).FirstOrDefault(h => h is not null);
                throw new InvalidOperationException(hint ?? "Install GitHub Copilot CLI or Claude Code to chat.");
            }

            _status = "busy";
            _problem = null;
            _entries.Add(new ChatEntry(NextId(), "user", text.Trim()));
            _messageId = null;
        }

        Notify();
        return RunTurnAsync(text.Trim());
    }

    /// <summary>Stops the answer being written (and refuses any question waiting for you).</summary>
    public async Task CancelAsync()
    {
        AnswerAll("cancelled");
        if (_agent is { } agent && _sessionId is { } session)
        {
            try
            {
                await agent.NotifyAsync("session/cancel", new { sessionId = session }).ConfigureAwait(false);
            }
            catch (AcpException)
            {
            }
        }
    }

    /// <summary>Starts over: a new conversation with the same (warm) agent.</summary>
    public async Task NewChatAsync()
    {
        await CancelAsync().ConfigureAwait(false);
        lock (_lock)
        {
            _entries.Clear();
            _sessionId = null;
            _changesAllowed = false;
            if (_status != "error")
            {
                _status = _agent is null ? "idle" : "ready";
            }
        }

        Notify();
    }

    /// <summary>Answers a permission request: allow, allow-chat (changes to tasks for the rest of this chat), or reject.</summary>
    public Task AnswerAsync(string entryId, string choice)
    {
        if (choice is not ("allow" or "allow-chat" or "reject"))
        {
            throw new ArgumentException("Answer allow, allow-chat, or reject.", nameof(choice));
        }

        TaskCompletionSource<string>? question;
        lock (_lock)
        {
            _questions.Remove(entryId, out question);
        }

        if (question is null)
        {
            throw new InvalidOperationException("That question was already answered.");
        }

        question.TrySetResult(choice);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        AnswerAll("cancelled");
        await StopAgentAsync().ConfigureAwait(false);
        _startGate.Dispose();
    }

    private static (HashSet<string> All, HashSet<string> ReadOnly) FindTools()
    {
        var tools = typeof(TodoTools).GetMethods().Select(m => m.GetCustomAttribute<McpServerToolAttribute>()).OfType<McpServerToolAttribute>().ToList();
        return (tools.Select(t => t.Name!).ToHashSet(StringComparer.Ordinal), tools.Where(t => t.ReadOnly).Select(t => t.Name!).ToHashSet(StringComparer.Ordinal));
    }

    private static string? OptionOfKind(JsonElement options, params string[] kinds)
    {
        foreach (var kind in kinds)
        {
            foreach (var option in options.EnumerateArray())
            {
                if (option.TryGetProperty("kind", out var k) && k.GetString() == kind && option.TryGetProperty("optionId", out var id))
                {
                    return id.GetString();
                }
            }
        }

        return null;
    }

    private static bool LooksLikeSignIn(string message) =>
        message.Contains("auth", StringComparison.OrdinalIgnoreCase) || message.Contains("login", StringComparison.OrdinalIgnoreCase)
        || message.Contains("log in", StringComparison.OrdinalIgnoreCase) || message.Contains("sign in", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("[a-z]+(?:_[a-z]+)+")]
    private static partial Regex ToolName();

    private string NextId() => $"e{++_nextEntry}";

    private async Task RunTurnAsync(string text)
    {
        try
        {
            var (agent, session, sendContext) = await EnsureSessionAsync().ConfigureAwait(false);
            JsonArray prompt = sendContext
                ? [new JsonObject { ["type"] = "text", ["text"] = Context }, new JsonObject { ["type"] = "text", ["text"] = text }]
                : [new JsonObject { ["type"] = "text", ["text"] = text }];
            var result = await agent.RequestAsync("session/prompt", new JsonObject { ["sessionId"] = session, ["prompt"] = prompt }, _options.TurnTimeout).ConfigureAwait(false);
            var stop = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("stopReason", out var s) ? s.GetString() : null;
            lock (_lock)
            {
                _status = "ready";
                var note = stop switch
                {
                    "cancelled" => "Stopped.",
                    "max_tokens" or "max_turn_requests" => "The answer was cut short. Ask it to continue.",
                    "refusal" => "The agent declined to answer that.",
                    _ => null,
                };
                if (note is not null)
                {
                    _entries.Add(new ChatEntry(NextId(), "note", note));
                }
            }
        }
        catch (Exception ex) when (ex is AcpException or TimeoutException or InvalidOperationException or IOException)
        {
            lock (_lock)
            {
                _status = "error";
                _problem = ex.Message;
                _entries.Add(new ChatEntry(NextId(), "note", ex is TimeoutException ? "The agent didn't answer in time." : ex.Message, "error"));
            }

            await StopAgentAsync().ConfigureAwait(false);
        }
        finally
        {
            AnswerAll("cancelled");
        }

        Notify();
    }

    private async Task<(AcpConnection Agent, string Session, bool SendContext)> EnsureSessionAsync()
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Agents sometimes crash while starting (e.g. while they load their own MCP servers): try a few times.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await StartSessionAsync().ConfigureAwait(false);
                    break;
                }
                catch (AcpException ex) when (!_useSignInFallback && LooksLikeSignIn(ex.Message) && _catalog.LaunchFor(_choice!, _options.WorkDirectory).SignInFallback is not null)
                {
                    // Not signed in with the chat's own settings: use the person's own (their usual sign-in).
                    _useSignInFallback = true;
                    await StopAgentAsync().ConfigureAwait(false);
                }
                catch (AcpException ex) when (_agent is null || _agent.Exited.IsCompleted)
                {
                    await StopAgentAsync().ConfigureAwait(false);
                    if (attempt == StartAttempts)
                    {
                        throw new AcpException($"{ex.Message} It keeps stopping while it starts; updating it may help (copilot update, or update Claude Code).", ex);
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt)).ConfigureAwait(false);
                }
            }

            lock (_lock)
            {
                _status = "busy";
            }

            var sendContext = !_contextSent;
            _contextSent = true;
            return (_agent!, _sessionId!, sendContext);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task StartSessionAsync()
    {
        {
            if (_agent is null || _agent.Exited.IsCompleted)
            {
                await StopAgentAsync().ConfigureAwait(false);
                lock (_lock)
                {
                    _status = "starting";
                }

                Notify();
                Directory.CreateDirectory(_options.WorkDirectory);
                var launch = _catalog.LaunchFor(_choice!, _options.WorkDirectory);
                var agent = AcpConnection.Start(_useSignInFallback && launch.SignInFallback is { } fallback ? fallback : launch);
                agent.Notification += OnNotification;
                agent.RequestHandler = OnRequestAsync;
                _ = agent.Exited.ContinueWith(_ => OnAgentExited(agent), TaskScheduler.Default);
                _agent = agent;
                var init = await agent.RequestAsync(
                    "initialize",
                    new { protocolVersion = 1, clientCapabilities = new { fs = new { readTextFile = false, writeTextFile = false }, terminal = false }, clientInfo = new { name = "todo-tracker", title = "Todo Tracker", version = McpInfo.Version } },
                    _options.StartTimeout).ConfigureAwait(false);
                _agentTakesHttp = init.ValueKind == JsonValueKind.Object
                    && init.TryGetProperty("agentCapabilities", out var caps) && caps.TryGetProperty("mcpCapabilities", out var mcpCaps)
                    && mcpCaps.TryGetProperty("http", out var http) && http.ValueKind == JsonValueKind.True;
                _sessionId = null;
            }

            if (_sessionId is null)
            {
                var servers = new JsonArray();
                if (_agentTakesHttp && _options.McpHttp is { } endpoint)
                {
                    // The live board through the app's own endpoint (Copilot only accepts HTTP MCP servers from clients).
                    servers.Add(new JsonObject
                    {
                        ["type"] = "http",
                        ["name"] = "todo-tracker",
                        ["url"] = endpoint.Url,
                        ["headers"] = new JsonArray(new JsonObject { ["name"] = "Authorization", ["value"] = "Bearer " + endpoint.Token }),
                    });
                }
                else if (_options.Mcp is { } mcp)
                {
                    servers.Add(new JsonObject
                    {
                        ["name"] = mcp.Id,
                        ["command"] = mcp.Command,
                        ["args"] = new JsonArray([.. mcp.Arguments.Select(a => (JsonNode)a)]),
                        ["env"] = new JsonArray([.. mcp.Environment.Where(e => e.Value is not null).Select(e => (JsonNode)new JsonObject { ["name"] = e.Key, ["value"] = e.Value })]),
                    });
                }

                var created = await _agent!.RequestAsync("session/new", new JsonObject { ["cwd"] = _options.WorkDirectory, ["mcpServers"] = servers }, _options.StartTimeout).ConfigureAwait(false);
                _sessionId = created.GetProperty("sessionId").GetString() ?? throw new AcpException("The agent didn't start a session.");
                _contextSent = false;
            }
        }
    }

    private void OnAgentExited(AcpConnection agent)
    {
        if (!ReferenceEquals(agent, _agent))
        {
            return;
        }

        AnswerAll("cancelled");
        lock (_lock)
        {
            // While starting, a crash is retried (EnsureSessionAsync) and reported there.
            if (_status == "starting")
            {
                return;
            }

            _status = "error";
            var code = agent.ExitCode is { } c ? (c < 0 ? $" (exit code 0x{unchecked((uint)c):X8})" : $" (exit code {c})") : string.Empty;
            _problem = $"The agent stopped{code}. Send a message to start it again.";
        }

        Notify();
    }

    private void OnNotification(string method, JsonElement parameters)
    {
        if (method != "session/update" || !parameters.TryGetProperty("update", out var update) || !update.TryGetProperty("sessionUpdate", out var kind))
        {
            return;
        }

        lock (_lock)
        {
            switch (kind.GetString())
            {
                case "agent_message_chunk":
                    var text = update.TryGetProperty("content", out var content) && content.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                    var messageId = update.TryGetProperty("messageId", out var m) ? m.GetString() : null;
                    var last = _entries.Count > 0 ? _entries[^1] : null;
                    if (last is { Kind: "agent" } && (messageId is null || messageId == _messageId))
                    {
                        _entries[^1] = last with { Text = last.Text + text };
                    }
                    else
                    {
                        _entries.Add(new ChatEntry(NextId(), "agent", text));
                    }

                    _messageId = messageId;
                    break;
                case "tool_call":
                    var id = update.TryGetProperty("toolCallId", out var callId) ? callId.GetString() : null;
                    var title = update.TryGetProperty("title", out var ti) ? ti.GetString() ?? "Tool" : "Tool";
                    _entries.Add(new ChatEntry(id is null ? NextId() : "tool:" + id, "tool", title, update.TryGetProperty("status", out var st) ? st.GetString() : "pending"));
                    break;
                case "tool_call_update":
                    if (update.TryGetProperty("toolCallId", out var updated) && _entries.FindIndex(e => e.Id == "tool:" + updated.GetString()) is var index and >= 0)
                    {
                        var entry = _entries[index];
                        _entries[index] = entry with
                        {
                            Status = update.TryGetProperty("status", out var status) ? status.GetString() : entry.Status,
                            Text = update.TryGetProperty("title", out var newTitle) && newTitle.GetString() is { Length: > 0 } better ? better : entry.Text,
                        };
                    }

                    break;
                default:
                    return;
            }
        }

        Notify();
    }

    private async Task<object?> OnRequestAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        if (method != "session/request_permission")
        {
            throw new NotSupportedException();
        }

        var options = parameters.GetProperty("options");
        var toolCall = parameters.TryGetProperty("toolCall", out var call) ? call : default;
        var title = toolCall.ValueKind == JsonValueKind.Object && toolCall.TryGetProperty("title", out var t) ? t.GetString() ?? "An action" : "An action";
        var described = toolCall.ValueKind == JsonValueKind.Object ? toolCall.GetRawText() : title;
        var names = ToolName().Matches(described).Select(m => m.Value).Where(TodoTools.All.Contains).ToHashSet();
        var ours = names.Count > 0;
        var readOnly = ours && names.All(TodoTools.ReadOnly.Contains);

        string choice;
        if (readOnly || (ours && _changesAllowed))
        {
            choice = "allow";
        }
        else
        {
            var question = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            string entryId;
            lock (_lock)
            {
                entryId = NextId();
                _entries.Add(new ChatEntry(entryId, "permission", ours ? $"Change your tasks: {title}" : title, "waiting", ours ? ["allow", "allow-chat", "reject"] : ["allow", "reject"]));
                _questions[entryId] = question;
            }

            Notify();
            try
            {
                choice = await question.Task.WaitAsync(_options.PermissionTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                choice = "expired";
            }
            catch (OperationCanceledException)
            {
                choice = "cancelled";
            }

            lock (_lock)
            {
                _questions.Remove(entryId);
                if (choice == "allow-chat")
                {
                    _changesAllowed = true;
                }

                if (_entries.FindIndex(e => e.Id == entryId) is var index and >= 0)
                {
                    _entries[index] = _entries[index] with
                    {
                        Status = choice switch { "allow" or "allow-chat" => "allowed", "reject" => "rejected", _ => choice },
                        Choices = null,
                    };
                }
            }

            Notify();
        }

        if (choice == "cancelled")
        {
            return new { outcome = new { outcome = "cancelled" } };
        }

        // Never the agent's own "always": what is allowed is decided here, per chat.
        var optionId = choice is "allow" or "allow-chat" ? OptionOfKind(options, "allow_once", "allow_always") : OptionOfKind(options, "reject_once", "reject_always");
        return optionId is null ? new { outcome = new { outcome = "cancelled" } } : new { outcome = new { outcome = "selected", optionId } };
    }

    private void AnswerAll(string answer)
    {
        List<TaskCompletionSource<string>> waiting;
        lock (_lock)
        {
            waiting = [.. _questions.Values];
            _questions.Clear();
        }

        foreach (var question in waiting)
        {
            question.TrySetResult(answer);
        }
    }

    private async Task StopAgentAsync()
    {
        var agent = _agent;
        _agent = null;
        _sessionId = null;
        if (agent is not null)
        {
            agent.Notification -= OnNotification;
            agent.RequestHandler = null;
            await agent.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void Notify() => Changed?.Invoke(this, State);

    private string? LoadChoice()
    {
        try
        {
            return _options.SettingsPath is { } path && File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path))?["agent"]?.GetValue<string>()
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SaveChoice(string agentId)
    {
        if (_options.SettingsPath is not { } path)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new JsonObject { ["agent"] = agentId }.ToJsonString());
    }
}
