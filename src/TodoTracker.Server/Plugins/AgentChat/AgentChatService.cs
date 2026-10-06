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
/// <c>error</c> (it stopped; the next message starts it again). <paramref name="Version"/> grows with every state handed
/// out, so a screen can drop a state that arrives after a newer one.
/// </summary>
public sealed record ChatState(string Status, string? Agent, IReadOnlyList<AgentOption> Agents, IReadOnlyList<ChatEntry> Entries, string? Problem, long Version = 0);

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

    // What each tool call was announced as (session/update), by toolCallId: permission requests may carry less.
    private readonly Dictionary<string, (string Title, string? Kind)> _announced = [];
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private IReadOnlyList<AgentOption> _agents;
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
    private long _version;
    private Task _turn = Task.CompletedTask;

    public AgentChatService(AgentCatalog catalog, AgentChatOptions options)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _agents = catalog.Detect();
        _choice = AgentCatalog.DefaultChoice(_agents, LoadChoice());
    }

    /// <summary>Raised (on a background thread) whenever something the chat shows changed.</summary>
    public event EventHandler<ChatState>? Changed;

    public ChatState State
    {
        get
        {
            lock (_lock)
            {
                return new ChatState(_status, _choice, _agents, [.. _entries], _problem, ++_version);
            }
        }
    }

    /// <summary>Looks for installed agents again (searching the PATH is slow: only when the chat is opened).</summary>
    public void RefreshAgents()
    {
        var agents = _catalog.Detect();
        lock (_lock)
        {
            _agents = agents;
            _choice = AgentCatalog.DefaultChoice(agents, _choice);
        }

        Notify();
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

        await StopTurnAsync().ConfigureAwait(false);
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAgentAsync().ConfigureAwait(false);
            lock (_lock)
            {
                _agents = agents;
                _useSignInFallback = false;
                _choice = agentId;
                _status = "idle";
                _problem = null;
            }
        }
        finally
        {
            _startGate.Release();
        }

        SaveChoice(agentId);
        Notify();
    }

    /// <summary>Sends a message; the returned task completes when the answer is finished.</summary>
    /// <exception cref="InvalidOperationException">Another answer is still coming, or no agent is installed.</exception>
    public Task SendAsync(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (_status is "busy" or "starting")
            {
                throw new InvalidOperationException("Wait for the answer (or stop it) first.");
            }

            if (_choice is null)
            {
                var hint = _agents.Select(a => a.Hint).FirstOrDefault(h => h is not null);
                throw new InvalidOperationException(hint ?? "Install GitHub Copilot CLI or Claude Code to chat.");
            }

            _status = "busy";
            _problem = null;
            _entries.Add(new ChatEntry(NextId(), "user", text.Trim()));
            _messageId = null;
            _turn = done.Task;
        }

        Notify();
        return RunTurnAsync(text.Trim(), done);
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
        await StopTurnAsync().ConfigureAwait(false);
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                _entries.Clear();
                _announced.Clear();
                _sessionId = null;
                _changesAllowed = false;
                if (_status != "error")
                {
                    _status = _agent is null ? "idle" : "ready";
                }
            }
        }
        finally
        {
            _startGate.Release();
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
            if (question is not null)
            {
                // Shown at once: the state handed back now already has the answer.
                Settle(entryId, choice);
            }
        }

        if (question is null)
        {
            throw new InvalidOperationException("That question was already answered.");
        }

        question.TrySetResult(choice);
        Notify();
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

    private static string? OptionOfKind(JsonElement options, string kind)
    {
        foreach (var option in options.EnumerateArray())
        {
            if (Str(option, "kind") == kind && Str(option, "optionId") is { } id)
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>A string property, or null when it's missing or not a string (agents don't always send what the spec says).</summary>
    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool LooksLikeSignIn(string message) =>
        message.Contains("auth", StringComparison.OrdinalIgnoreCase) || message.Contains("login", StringComparison.OrdinalIgnoreCase)
        || message.Contains("log in", StringComparison.OrdinalIgnoreCase) || message.Contains("sign in", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The Todo Tracker tool a permission request is for, or null when it's anything else. Decided by the tool's name as
    /// the agent names our server's tools (Copilot: <c>todo-tracker-create_task</c> when announced, then the bare name in
    /// the request; Claude: <c>mcp__todo-tracker__create_task</c>), never by words found elsewhere (a command can
    /// mention a tool), and never for commands, deletes or moves.
    /// </summary>
    private static string? OurTool(string title, string? kind, (string Title, string? Kind)? announced)
    {
        static bool Risky(string? k) => k is "execute" or "delete" or "move";
        if (Risky(kind) || Risky(announced?.Kind))
        {
            return null;
        }

        static string? Named(string text) => OurToolTitle().Match(text.Trim()) is { Success: true } m && TodoTools.All.Contains(m.Groups[1].Value) ? m.Groups[1].Value : null;
        return Named(title) ?? (announced is { } a && Named(a.Title) is { } name && (title.Trim() == name || title.Trim() == a.Title.Trim()) ? name : null);
    }

    [GeneratedRegex("^(?:todo-tracker-|todo-tracker: ?|todo-tracker/|mcp__todo-tracker__)([a-z]+(?:_[a-z]+)+)$")]
    private static partial Regex OurToolTitle();

    private string NextId() => $"e{++_nextEntry}";

    /// <summary>Stops the answer being written and waits for it to end (so a new chat never mixes with the old one).</summary>
    private async Task StopTurnAsync()
    {
        await CancelAsync().ConfigureAwait(false);
        Task turn;
        lock (_lock)
        {
            turn = _turn;
        }

        try
        {
            await turn.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The agent ignores the cancel: stopping it ends the turn.
            await _startGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopAgentAsync().ConfigureAwait(false);
            }
            finally
            {
                _startGate.Release();
            }

            await turn.ConfigureAwait(false);
        }
    }

    private async Task RunTurnAsync(string text, TaskCompletionSource done)
    {
        try
        {
            var (agent, session, sendContext) = await EnsureSessionAsync().ConfigureAwait(false);
            JsonArray prompt = sendContext
                ? [new JsonObject { ["type"] = "text", ["text"] = Context }, new JsonObject { ["type"] = "text", ["text"] = text }]
                : [new JsonObject { ["type"] = "text", ["text"] = text }];
            var result = await agent.RequestAsync("session/prompt", new JsonObject { ["sessionId"] = session, ["prompt"] = prompt }, _options.TurnTimeout).ConfigureAwait(false);
            var stop = Str(result, "stopReason");
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever went wrong, the chat must not stay "busy" or "starting": say so, and start fresh next time.
            lock (_lock)
            {
                _status = "error";
                _problem = ex.Message;
                _entries.Add(new ChatEntry(NextId(), "note", ex is TimeoutException ? "The agent didn't answer in time." : ex.Message, "error"));
            }

            await _startGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopAgentAsync().ConfigureAwait(false);
            }
            finally
            {
                _startGate.Release();
            }
        }
        finally
        {
            AnswerAll("cancelled");
            done.TrySetResult();
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
                catch (AcpException ex) when (!_useSignInFallback && _agent is { Exited.IsCompleted: false } && LooksLikeSignIn(ex.Message)
                    && _catalog.LaunchFor(_choice!, _options.WorkDirectory).SignInFallback is not null)
                {
                    // The agent answered (it didn't crash) that it isn't signed in with the chat's own settings: use the
                    // person's own settings, where they signed in. Say so: their own MCP servers load there too.
                    _useSignInFallback = true;
                    lock (_lock)
                    {
                        _entries.Add(new ChatEntry(NextId(), "note", "Using your own Copilot settings to sign in."));
                    }

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
                _sessionId = Str(created, "sessionId") ?? throw new AcpException("The agent didn't start a session.");
                _contextSent = false;
                lock (_lock)
                {
                    _announced.Clear();
                }
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
        var update = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("update", out var u) ? u : default;
        if (method != "session/update" || Str(update, "sessionUpdate") is not { } kind)
        {
            return;
        }

        lock (_lock)
        {
            // Only the current conversation (a stopped turn of an older one may still be talking).
            if (Str(parameters, "sessionId") != _sessionId)
            {
                return;
            }

            switch (kind)
            {
                case "agent_message_chunk":
                    var text = update.TryGetProperty("content", out var content) ? Str(content, "text") ?? string.Empty : string.Empty;
                    var messageId = Str(update, "messageId");
                    var last = _entries.Count > 0 ? _entries[^1] : null;
                    if (last is { Kind: "agent" } && (messageId is null || messageId == _messageId))
                    {
                        _entries[^1] = last with { Text = last.Text + text };
                    }
                    else if (text.Length > 0)
                    {
                        _entries.Add(new ChatEntry(NextId(), "agent", text));
                    }

                    _messageId = messageId;
                    break;
                case "tool_call":
                    var id = Str(update, "toolCallId");
                    var title = Str(update, "title") ?? "Tool";
                    if (id is not null)
                    {
                        _announced[id] = (title, Str(update, "kind"));
                    }

                    _entries.Add(new ChatEntry(id is null ? NextId() : "tool:" + id, "tool", title, Str(update, "status") ?? "pending"));
                    break;
                case "tool_call_update":
                    if (Str(update, "toolCallId") is { } updated && _entries.FindIndex(e => e.Id == "tool:" + updated) is var index and >= 0)
                    {
                        var entry = _entries[index];
                        _entries[index] = entry with
                        {
                            Status = Str(update, "status") ?? entry.Status,
                            Text = Str(update, "title") is { Length: > 0 } better ? better : entry.Text,
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

        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("The request offered no choices.");
        }

        var toolCall = parameters.TryGetProperty("toolCall", out var call) ? call : default;
        var title = Str(toolCall, "title") ?? "An action";
        (string Title, string? Kind)? announced = null;
        lock (_lock)
        {
            if (Str(toolCall, "toolCallId") is { } callId && _announced.TryGetValue(callId, out var a))
            {
                announced = a;
            }
        }

        var tool = OurTool(title, Str(toolCall, "kind"), announced);
        var ours = tool is not null;
        var readOnly = ours && TodoTools.ReadOnly.Contains(tool!);

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
                _entries.Add(new ChatEntry(entryId, "permission", ours ? $"Change your tasks: {tool!.Replace('_', ' ')}" : title, "waiting", ours ? ["allow", "allow-chat", "reject"] : ["allow", "reject"]));
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
                Settle(entryId, choice);
            }

            Notify();
        }

        if (choice == "cancelled")
        {
            return new { outcome = new { outcome = "cancelled" } };
        }

        // Only "once" options: what is allowed is decided here, per chat, never remembered by the agent. An agent that
        // only offers "always" gets "cancelled".
        var optionId = OptionOfKind(options, choice is "allow" or "allow-chat" ? "allow_once" : "reject_once");
        return optionId is null ? new { outcome = new { outcome = "cancelled" } } : new { outcome = new { outcome = "selected", optionId } };
    }

    /// <summary>Records the answer on the question's entry (caller holds the lock).</summary>
    private void Settle(string entryId, string choice)
    {
        if (choice == "allow-chat")
        {
            _changesAllowed = true;
        }

        if (_entries.FindIndex(e => e.Id == entryId) is var index and >= 0 && _entries[index].Status == "waiting")
        {
            _entries[index] = _entries[index] with
            {
                Status = choice switch { "allow" or "allow-chat" => "allowed", "reject" => "rejected", _ => choice },
                Choices = null,
            };
        }
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
