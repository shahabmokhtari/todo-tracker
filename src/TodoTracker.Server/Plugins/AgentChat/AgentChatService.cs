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

    /// <summary>Where chats are kept (null: chats last until the app closes).</summary>
    public ChatHistoryStore? History { get; init; }

    /// <summary>Models reached with an API key (null: agents only).</summary>
    public ApiModelStore? Models { get; init; }

    /// <summary>The task tools an API model gets, named after it (so its changes are credited to it).</summary>
    public Func<ApiModel, IChatTools>? ToolsFor { get; init; }

    /// <summary>For API models (redirects off: a key must never follow one elsewhere).</summary>
    public HttpClient? ApiHttp { get; init; }

    public ApiTurnLimits ApiLimits { get; init; } = new();

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(120);

    public TimeSpan TurnTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>An unanswered permission request is refused after this.</summary>
    public TimeSpan PermissionTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a stopped answer may take to end before the agent is stopped.</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>One line in the chat. Kinds: user, agent, tool, permission, note. Choices are for permission requests.</summary>
public sealed record ChatEntry(string Id, string Kind, string Text, string? Status = null, IReadOnlyList<string>? Choices = null);

/// <summary>
/// What the chat shows. Status: <c>idle</c> (agent not started), <c>starting</c>, <c>ready</c>, <c>busy</c> (answering),
/// <c>error</c> (it stopped; the next message starts it again). <paramref name="Version"/> grows with every state handed
/// out, so a screen can drop a state that arrives after a newer one; <paramref name="Epoch"/> changes when the app
/// restarts (versions start over then). <paramref name="ChatId"/> is the open chat; <paramref name="ChatsVersion"/>
/// changes when the list of chats does (screens fetch the list then, rather than with every update).
/// </summary>
public sealed record ChatState(string Status, string? Agent, IReadOnlyList<AgentOption> Agents, IReadOnlyList<ChatEntry> Entries, string? Problem, long Version = 0, string Epoch = "", string ChatId = "", long ChatsVersion = 0);

/// <summary>
/// Chats about your tasks, with Copilot or Claude (over ACP: the agent starts on the first message and stays warm) or a
/// model reached with an API key. Every chat is kept and can be reopened; a chat stays with the agent or model it was
/// started with. All of them get Todo Tracker's tools. Reading tasks needs no permission; changing them asks once (or
/// for the rest of the chat); anything else (shell commands, files) always asks, and an unanswered request is refused.
/// </summary>
public sealed partial class AgentChatService : IAsyncDisposable
{
    public const string ApiPrefix = "api:";

    private const string Context =
        "[Context from Todo Tracker, not from the user] You are the assistant inside Todo Tracker, the user's ADHD-friendly task list. " +
        "Use the todo-tracker tools to look at and change their tasks (never edit files or run commands for that). " +
        "Capture what they ask for in their words, make big things small, keep replies short: what you did and the one next step.";

    private const string ApiSystemPrompt =
        "You are the assistant inside Todo Tracker, the user's ADHD-friendly task list. Use the tools to look at and change their tasks. " +
        "Capture what they ask for in their words, make big things small, keep replies short: what you did and the one next step.";

    private const int StartAttempts = 3;
    private const int PrimerEntries = 20;
    private const int PrimerChars = 6000;
    private static readonly (HashSet<string> All, HashSet<string> ReadOnly) TodoTools = FindTools();

    private readonly AgentCatalog _catalog;
    private readonly AgentChatOptions _options;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, TaskCompletionSource<string>> _questions = [];

    // What each tool call was announced as (session/update), by toolCallId: permission requests may carry less.
    private readonly Dictionary<string, (string Title, string? Kind)> _announced = [];
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly ApiTurnRunner _runner;
    private readonly HttpClient _apiHttp;
    private IReadOnlyList<AgentOption> _acpAgents;
    private IReadOnlyList<AgentOption> _agents;
    private ChatRecord _chat;

    // Grows whenever another chat is shown: anything from an earlier chat's answer that arrives late is dropped.
    private long _generation;
    private AcpConnection? _agent;
    private string? _runningAgent;
    private string? _sessionId;

    // A session loaded to carry on a chat replays it: those updates are already in the chat, so they're not shown.
    private string? _replaying;
    private long _lastReplayed;
    private string? _primer;
    private bool _agentLoadsSessions;
    private bool _contextSent;
    private bool _changesAllowed;
    private bool _agentTakesHttp;
    private bool _useSignInFallback;
    private string _status = "idle";
    private string? _problem;
    private string? _choice;
    private string? _messageId;
    private int _nextEntry;
    private readonly string _epoch = Guid.NewGuid().ToString("N");
    private long _version;
    private long _chatsVersion;
    private int _resetting;
    private Task _turn = Task.CompletedTask;
    private CancellationTokenSource? _apiTurn;
    private bool _apiStopped;
    private (string Model, IChatTools Tools)? _apiTools;
    private long _lastTextNotify;

    public AgentChatService(AgentCatalog catalog, AgentChatOptions options)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _apiHttp = options.ApiHttp ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _runner = new ApiTurnRunner(new ApiChatClient(_apiHttp), options.ApiLimits);
        _acpAgents = catalog.Detect();
        _agents = WithModels(_acpAgents);
        _choice = AgentCatalog.DefaultChoice(_agents, LoadChoice());
        _chat = ChatRecord.New(_choice ?? string.Empty, _options.Time.GetUtcNow());
        if (options.History is { } history)
        {
            history.Changed += OnChatsChanged;
        }

        if (options.Models is { } models)
        {
            models.Changed += OnModelsChanged;
        }
    }

    /// <summary>Raised (on a background thread) whenever something the chat shows changed.</summary>
    public event EventHandler<ChatState>? Changed;

    public ChatState State
    {
        get
        {
            lock (_lock)
            {
                return Snapshot();
            }
        }
    }

    /// <summary>The kept chats, newest first.</summary>
    public IReadOnlyList<ChatSummary> Chats => _options.History?.List() ?? [];

    public IReadOnlyList<ChatSummary> Search(string query) => _options.History?.Search(query) ?? [];

    /// <summary>Looks for installed agents again (searching the PATH is slow: only when the chat is opened).</summary>
    public void RefreshAgents()
    {
        var agents = _catalog.Detect();
        lock (_lock)
        {
            _acpAgents = agents;
            _agents = WithModels(agents);
            _choice = _chat.Entries.Count > 0 ? _choice : AgentCatalog.DefaultChoice(_agents, _choice);
        }

        Notify();
    }

    /// <summary>
    /// Chooses who to chat with (and remembers it). A chat stays with the agent or model it was started with, so
    /// choosing another one for a chat that has begun starts a new chat.
    /// </summary>
    public async Task SelectAsync(string agentId)
    {
        var acp = _catalog.Detect();
        var agents = WithModels(acp);
        var agent = agents.FirstOrDefault(a => a.Id == agentId) ?? throw new ArgumentException($"Unknown agent \"{agentId}\".", nameof(agentId));
        if (!agent.Installed)
        {
            throw new InvalidOperationException(agent.Hint ?? $"{agent.Name} isn't installed.");
        }

        await ResetAsync(async () =>
        {
            // An agent picked again starts afresh (that's how to restart a stuck one); an API model leaves it warm.
            if (!IsApi(agentId))
            {
                await StopAgentAsync().ConfigureAwait(false);
                _useSignInFallback = false;
            }

            lock (_lock)
            {
                _acpAgents = acp;
                _agents = agents;
                _choice = agentId;
                if (_chat.Entries.Count > 0 && _chat.Agent != agentId)
                {
                    ShowChatLocked(ChatRecord.New(agentId, _options.Time.GetUtcNow()));
                }
                else
                {
                    _chat.Agent = agentId;
                    ForgetSessionLocked();
                }

                _status = IdleStatus();
                _problem = null;
            }
        }).ConfigureAwait(false);
        SaveChoice(agentId);
        Notify();
    }

    /// <summary>Sends a message; the returned task completes when the answer is finished.</summary>
    /// <exception cref="InvalidOperationException">Another answer is still coming, or there's no one to chat with.</exception>
    public Task SendAsync(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ChatRecord chat;
        long generation;
        lock (_lock)
        {
            if (_status is "busy" or "starting" || _resetting > 0)
            {
                throw new InvalidOperationException("Wait for the answer (or stop it) first.");
            }

            if (_choice is null)
            {
                var hint = _agents.Select(a => a.Hint).FirstOrDefault(h => h is not null);
                throw new InvalidOperationException(hint ?? "Install GitHub Copilot CLI or Claude Code, or add an API model, to chat.");
            }

            if (_chat.Entries.Count == 0)
            {
                _chat.Agent = _choice;
            }

            if (_agents.FirstOrDefault(a => a.Id == _chat.Agent) is not { Installed: true })
            {
                throw new InvalidOperationException("This chat was with an agent or model that isn't available now. Start a new chat to carry on.");
            }

            _status = "busy";
            _problem = null;
            _chat.Entries.Add(new ChatEntry(NextId(), "user", text.Trim()));
            _chat.UpdatedAt = _options.Time.GetUtcNow();
            _messageId = null;
            _turn = done.Task;
            chat = _chat;
            generation = _generation;
        }

        Save(chat);
        Notify();
        return IsApi(chat.Agent) ? RunApiTurnAsync(text.Trim(), chat, generation, done) : RunTurnAsync(text.Trim(), chat, generation, done);
    }

    /// <summary>Stops the answer being written (and refuses any question waiting for you).</summary>
    public async Task CancelAsync()
    {
        AnswerAll("cancelled");
        CancellationTokenSource? api;
        lock (_lock)
        {
            api = _apiTurn;
            _apiStopped = api is not null;
        }

        if (api is not null)
        {
            await api.CancelAsync().ConfigureAwait(false);
        }

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

    /// <summary>Starts over: a new chat with the same (warm) agent or model. The one before is kept.</summary>
    public async Task NewChatAsync()
    {
        await ResetAsync(() =>
        {
            lock (_lock)
            {
                ShowChatLocked(ChatRecord.New(_choice ?? string.Empty, _options.Time.GetUtcNow()));
                _problem = null;
                _status = IdleStatus();
            }

            return Task.CompletedTask;
        }).ConfigureAwait(false);
        Notify();
    }

    /// <summary>Opens a kept chat; the next message carries it on (with the agent or model it was with).</summary>
    /// <exception cref="KeyNotFoundException">There's no such chat.</exception>
    public async Task OpenChatAsync(string chatId)
    {
        var chat = _options.History?.Load(chatId) ?? throw new KeyNotFoundException("That chat isn't there any more.");
        await ResetAsync(() =>
        {
            lock (_lock)
            {
                ShowChatLocked(chat);
                _choice = chat.Agent;
                _problem = null;
                _status = IdleStatus();
            }

            return Task.CompletedTask;
        }).ConfigureAwait(false);
        Notify();
    }

    public void RenameChat(string chatId, string title)
    {
        var history = _options.History ?? throw new KeyNotFoundException("That chat isn't there any more.");
        history.Rename(chatId, title);
        lock (_lock)
        {
            if (_chat.Id == chatId && history.List().FirstOrDefault(c => c.Id == chatId) is { } renamed)
            {
                _chat.Title = renamed.Title;
                _chat.Renamed = true;
            }
        }

        Notify();
    }

    /// <summary>Deletes a chat (it can be brought back for 30 days). Deleting the open chat starts a new one.</summary>
    public async Task<bool> DeleteChatAsync(string chatId)
    {
        bool open;
        lock (_lock)
        {
            open = _chat.Id == chatId;
        }

        if (open)
        {
            await NewChatAsync().ConfigureAwait(false);
        }

        return _options.History?.Delete(chatId) ?? false;
    }

    public bool RestoreChat(string chatId) => _options.History?.Restore(chatId) ?? false;

    /// <summary>
    /// Stops the answer being written, then runs <paramref name="reset"/> while no session can start. No message is
    /// taken in between (else it could start a turn the reset then cuts off, or run alongside it).
    /// </summary>
    private async Task ResetAsync(Func<Task> reset)
    {
        lock (_lock)
        {
            _resetting++;
        }

        try
        {
            await StopTurnAsync().ConfigureAwait(false);
            await _startGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await reset().ConfigureAwait(false);
            }
            finally
            {
                _startGate.Release();
            }
        }
        finally
        {
            lock (_lock)
            {
                _resetting--;
            }
        }
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
        if (_options.History is { } history)
        {
            history.Changed -= OnChatsChanged;
        }

        if (_options.Models is { } models)
        {
            models.Changed -= OnModelsChanged;
        }

        AnswerAll("cancelled");
        _apiTurn?.Cancel();
        await StopAgentAsync().ConfigureAwait(false);
        if (_apiTools is { } tools)
        {
            await tools.Tools.DisposeAsync().ConfigureAwait(false);
        }

        if (_options.ApiHttp is null)
        {
            _apiHttp.Dispose();
        }

        _startGate.Dispose();
    }

    private static bool IsApi(string? agentId) => agentId?.StartsWith(ApiPrefix, StringComparison.Ordinal) == true;

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
        // Only kinds an MCP tool call can have: never commands, deletes, moves or fetches.
        static bool Plain(string? k) => k is null or "other" or "read" or "edit";
        if (!Plain(kind) || (announced is { } an && !Plain(an.Kind)))
        {
            return null;
        }

        static string? Named(string text) => OurToolTitle().Match(text.Trim()) is { Success: true } m && TodoTools.All.Contains(m.Groups[1].Value) ? m.Groups[1].Value : null;

        // The request and how the call was announced must agree on the tool.
        var named = Named(title);
        if (announced is not { } a)
        {
            return named;
        }

        return Named(a.Title) is { } announcedName && (named == announcedName || (named is null && title.Trim() == announcedName)) ? announcedName : null;
    }

    [GeneratedRegex("^(?:todo-tracker-|todo-tracker: ?|todo-tracker/|mcp__todo-tracker__)([a-z]+(?:_[a-z]+)+)$")]
    private static partial Regex OurToolTitle();

    [GeneratedRegex("^e([0-9]+)$")]
    private static partial Regex EntryNumber();

    /// <summary>A change to tasks, as asked: the tool and, briefly, what it would do.</summary>
    private static string ChangeQuestion(string tool, string? arguments)
    {
        var what = $"Change your tasks: {tool.Replace('_', ' ')}";
        if (string.IsNullOrWhiteSpace(arguments) || arguments.Trim() is "{}" or "null")
        {
            return what;
        }

        var compact = arguments.ReplaceLineEndings(" ").Trim();
        return $"{what} {(compact.Length > 200 ? compact[..199] + "…" : compact)}";
    }

    /// <summary>The earlier messages of a chat, for an agent that starts a fresh session to carry it on.</summary>
    private static string? Primer(List<ChatEntry> entries)
    {
        var said = entries.Take(entries.Count - 1).Where(e => e.Kind is "user" or "agent").TakeLast(PrimerEntries)
            .Select(e => $"{(e.Kind == "user" ? "User" : "You")}: {e.Text}").ToList();
        if (said.Count == 0)
        {
            return null;
        }

        var text = string.Join("\n", said);
        if (text.Length > PrimerChars)
        {
            text = "…" + text[^PrimerChars..];
        }

        return "[Earlier in this chat, for context; not a new request]\n" + text;
    }

    private string NextId() => $"e{++_nextEntry}";

    private string IdleStatus() => IsApi(_choice) ? "ready" : _agent is null ? "idle" : "ready";

    private IReadOnlyList<AgentOption> WithModels(IReadOnlyList<AgentOption> acp) =>
        _options.Models is { } models
            ? [.. acp, .. models.List().Select(m => new AgentOption(ApiPrefix + m.Id, m.Name, true, null, "api", m.IsLocal))]
            : acp;

    /// <summary>Shows <paramref name="chat"/>: whatever belonged to the one before is let go (caller holds the lock).</summary>
    private void ShowChatLocked(ChatRecord chat)
    {
        _generation++;
        _chat = chat;
        _nextEntry = chat.Entries.Select(e => EntryNumber().Match(e.Id)).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max();
        ForgetSessionLocked();
    }

    /// <summary>
    /// The agent stays warm, but nothing of a chat's session carries into another: the next message loads (or starts)
    /// the right one, and permission for changes is asked again.
    /// </summary>
    private void ForgetSessionLocked()
    {
        _sessionId = null;
        _replaying = null;
        _primer = null;
        _contextSent = false;
        _messageId = null;
        _changesAllowed = false;
        _announced.Clear();
    }

    private void Save(ChatRecord chat)
    {
        if (_options.History is not { } history)
        {
            return;
        }

        ChatRecord copy;
        lock (_lock)
        {
            copy = chat.Snapshot();
        }

        try
        {
            history.Save(copy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_lock)
            {
                _problem = $"Couldn't keep this chat: {ex.Message}";
            }
        }
    }

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
            await turn.WaitAsync(_options.StopTimeout).ConfigureAwait(false);
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

    private async Task RunTurnAsync(string text, ChatRecord chat, long generation, TaskCompletionSource done)
    {
        try
        {
            var (agent, session, sendContext, primer) = await EnsureSessionAsync().ConfigureAwait(false);
            var prompt = new JsonArray();
            if (sendContext)
            {
                prompt.Add(new JsonObject { ["type"] = "text", ["text"] = Context });
            }

            if (primer is not null)
            {
                prompt.Add(new JsonObject { ["type"] = "text", ["text"] = primer });
            }

            prompt.Add(new JsonObject { ["type"] = "text", ["text"] = text });
            lock (_lock)
            {
                // A new question: what the session says from here on is new.
                _replaying = null;
            }

            var result = await agent.RequestAsync("session/prompt", new JsonObject { ["sessionId"] = session, ["prompt"] = prompt }, _options.TurnTimeout).ConfigureAwait(false);
            var stop = Str(result, "stopReason");
            lock (_lock)
            {
                if (generation == _generation)
                {
                    _status = "ready";
                    if (StopNote(stop) is { } note)
                    {
                        chat.Entries.Add(new ChatEntry(NextId(), "note", note));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever went wrong, the chat must not stay "busy" or "starting": say so, and start fresh next time.
            lock (_lock)
            {
                if (generation == _generation)
                {
                    _status = "error";
                    _problem = ex.Message;
                    chat.Entries.Add(new ChatEntry(NextId(), "note", ex is TimeoutException ? "The agent didn't answer in time." : ex.Message, "error"));
                }
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
            Save(chat);
            done.TrySetResult();
        }

        Notify();
    }

    private static string? StopNote(string? stop) => stop switch
    {
        "cancelled" => "Stopped.",
        "max_tokens" or "max_turn_requests" => "The answer was cut short. Ask it to continue.",
        "refusal" => "The agent declined to answer that.",
        _ => null,
    };

    private async Task RunApiTurnAsync(string text, ChatRecord chat, long generation, TaskCompletionSource done)
    {
        using var stop = new CancellationTokenSource(_options.TurnTimeout);
        List<ApiMessage> messages;
        lock (_lock)
        {
            _apiTurn = stop;
            _apiStopped = false;
            chat.Messages.Add(new ApiMessage("user", text));
            messages = [.. chat.Messages];
        }

        try
        {
            var id = chat.Agent[ApiPrefix.Length..];
            var models = _options.Models ?? throw new InvalidOperationException("API models aren't available here.");
            var model = models.Get(id) ?? throw new InvalidOperationException("That model was removed. Start a new chat and pick another.");
            var tools = ToolsFor(model);
            var reason = await _runner.RunAsync(model, models.Key(id), ApiSystemPrompt, messages, tools, new ApiEvents(this, chat, generation), stop.Token).ConfigureAwait(false);
            lock (_lock)
            {
                if (generation == _generation && StopNote(reason) is { } note)
                {
                    chat.Entries.Add(new ChatEntry(NextId(), "note", note));
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ApiChatException or HttpRequestException or InvalidOperationException or IOException or System.Security.Cryptography.CryptographicException or ModelContextProtocol.McpException)
        {
            lock (_lock)
            {
                if (generation == _generation)
                {
                    var note = ex switch
                    {
                        OperationCanceledException when _apiStopped => "Stopped.",
                        OperationCanceledException => "The model didn't finish in time.",
                        HttpRequestException => $"Couldn't reach the model's service: {ex.Message}",
                        System.Security.Cryptography.CryptographicException => "Couldn't read this model's API key; add the model again.",
                        _ => ex.Message,
                    };
                    chat.Entries.Add(new ChatEntry(NextId(), "note", note, ex is OperationCanceledException && _apiStopped ? null : "error"));
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                // Every tool call is answered (the runner sees to it), so the conversation can always go on.
                chat.Messages.Clear();
                chat.Messages.AddRange(messages);
                _apiTurn = null;
                if (generation == _generation)
                {
                    _status = "ready";
                }
            }

            AnswerAll("cancelled");
            Save(chat);
            done.TrySetResult();
        }

        Notify();
    }

    private IChatTools ToolsFor(ApiModel model)
    {
        var factory = _options.ToolsFor ?? throw new InvalidOperationException("Task tools aren't available for API models here.");
        lock (_lock)
        {
            if (_apiTools is { } current && current.Model == model.Name)
            {
                return current.Tools;
            }

            var previous = _apiTools?.Tools;
            var tools = factory(model);
            _apiTools = (model.Name, tools);
            _ = previous?.DisposeAsync().AsTask();
            return tools;
        }
    }

    private async Task<(AcpConnection Agent, string Session, bool SendContext, string? Primer)> EnsureSessionAsync()
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
                        _chat.SignInFallback = true;
                        _chat.Entries.Add(new ChatEntry(NextId(), "note", "Using your own Copilot settings to sign in."));
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
                var sendContext = !_contextSent;
                _contextSent = true;
                var primer = _primer;
                _primer = null;
                return (_agent!, _sessionId!, sendContext, primer);
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task StartSessionAsync()
    {
        // The open chat may be with another agent than the one running (a chat stays with its agent).
        if (_agent is null || _agent.Exited.IsCompleted || _runningAgent != _choice)
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
            _runningAgent = _choice;
            var init = await agent.RequestAsync(
                "initialize",
                new { protocolVersion = 1, clientCapabilities = new { fs = new { readTextFile = false, writeTextFile = false }, terminal = false }, clientInfo = new { name = "todo-tracker", title = "Todo Tracker", version = McpInfo.Version } },
                _options.StartTimeout).ConfigureAwait(false);
            var caps = init.ValueKind == JsonValueKind.Object && init.TryGetProperty("agentCapabilities", out var c) ? c : default;
            _agentTakesHttp = caps.ValueKind == JsonValueKind.Object && caps.TryGetProperty("mcpCapabilities", out var mcpCaps)
                && mcpCaps.TryGetProperty("http", out var http) && http.ValueKind == JsonValueKind.True;
            _agentLoadsSessions = caps.ValueKind == JsonValueKind.Object && caps.TryGetProperty("loadSession", out var load) && load.ValueKind == JsonValueKind.True;
            lock (_lock)
            {
                _sessionId = null;
            }
        }

        string? stored;
        bool earlier;
        lock (_lock)
        {
            if (_sessionId is not null)
            {
                return;
            }

            stored = _chat.AcpSessionId;
            earlier = _chat.Entries.Count(e => e.Kind is "user" or "agent") > 1;
        }

        if (stored is not null && _agentLoadsSessions)
        {
            // Carry on where the chat was: the agent replays it (already shown here, so not shown again).
            lock (_lock)
            {
                _replaying = stored;
            }

            try
            {
                await _agent!.RequestAsync("session/load", new JsonObject { ["sessionId"] = stored, ["cwd"] = _options.WorkDirectory, ["mcpServers"] = McpServers() }, _options.StartTimeout).ConfigureAwait(false);

                // Some of the replay can still be on its way: wait until the session is quiet before asking anything,
                // so nothing replayed is taken for the new answer.
                var deadline = Environment.TickCount64 + 2000;
                Interlocked.Exchange(ref _lastReplayed, Environment.TickCount64);
                while (Environment.TickCount64 - Interlocked.Read(ref _lastReplayed) < 150 && Environment.TickCount64 < deadline)
                {
                    await Task.Delay(25).ConfigureAwait(false);
                }

                lock (_lock)
                {
                    _sessionId = stored;
                    _contextSent = true;
                    _announced.Clear();
                }

                return;
            }
            catch (AcpException) when (_agent is { Exited.IsCompleted: false })
            {
                // It can't (the session is gone): a fresh session, told what was said.
                lock (_lock)
                {
                    _replaying = null;
                }
            }
        }

        var created = await _agent!.RequestAsync("session/new", new JsonObject { ["cwd"] = _options.WorkDirectory, ["mcpServers"] = McpServers() }, _options.StartTimeout).ConfigureAwait(false);
        var session = Str(created, "sessionId") ?? throw new AcpException("The agent didn't start a session.");
        lock (_lock)
        {
            _sessionId = session;
            _contextSent = false;
            _announced.Clear();
            _chat.AcpSessionId = session;
            if (earlier && Primer(_chat.Entries) is { } primer)
            {
                _primer = primer;
                _chat.Entries.Add(new ChatEntry(NextId(), "note", "Carrying on in a new session: the earlier messages were passed along."));
            }
        }
    }

    private JsonArray McpServers()
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

        return servers;
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
            // While starting, a crash is retried (EnsureSessionAsync) and reported there. An API chat doesn't need it.
            if (_status == "starting" || IsApi(_choice))
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
            // Only the current conversation (a stopped turn of an older one may still be talking), and not its replay.
            var session = Str(parameters, "sessionId");
            if (session is not null && session == _replaying)
            {
                Interlocked.Exchange(ref _lastReplayed, Environment.TickCount64);
                return;
            }

            if (session != _sessionId)
            {
                return;
            }

            var entries = _chat.Entries;
            switch (kind)
            {
                case "agent_message_chunk":
                    var text = update.TryGetProperty("content", out var content) ? Str(content, "text") ?? string.Empty : string.Empty;
                    var messageId = Str(update, "messageId");
                    var last = entries.Count > 0 ? entries[^1] : null;
                    if (last is { Kind: "agent" } && (messageId is null || messageId == _messageId))
                    {
                        entries[^1] = last with { Text = last.Text + text };
                    }
                    else if (text.Length > 0)
                    {
                        entries.Add(new ChatEntry(NextId(), "agent", text));
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

                    entries.Add(new ChatEntry(id is null ? NextId() : "tool:" + id, "tool", title, Str(update, "status") ?? "pending"));
                    break;
                case "tool_call_update":
                    if (Str(update, "toolCallId") is { } updated && entries.FindIndex(e => e.Id == "tool:" + updated) is var index and >= 0)
                    {
                        var entry = entries[index];
                        entries[index] = entry with
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
        long generation;
        lock (_lock)
        {
            // A request from a session that isn't the open chat's (it was left): nobody is there to answer it.
            if (Str(parameters, "sessionId") is { } session && session != _sessionId)
            {
                return new { outcome = new { outcome = "cancelled" } };
            }

            generation = _generation;
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
            var arguments = toolCall.ValueKind == JsonValueKind.Object && toolCall.TryGetProperty("rawInput", out var raw) ? raw.GetRawText() : null;
            choice = await AskAsync(ours ? ChangeQuestion(tool!, arguments) : title, ours ? ["allow", "allow-chat", "reject"] : ["allow", "reject"], generation, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Asks the person (a question in the chat) and waits: their choice, "expired", or "cancelled".</summary>
    private async Task<string> AskAsync(string text, IReadOnlyList<string> choices, long generation, CancellationToken cancellationToken)
    {
        var question = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        string entryId;
        lock (_lock)
        {
            if (generation != _generation)
            {
                return "cancelled";
            }

            entryId = NextId();
            _chat.Entries.Add(new ChatEntry(entryId, "permission", text, "waiting", choices));
            _questions[entryId] = question;
        }

        Notify();
        string choice;
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
            if (generation == _generation)
            {
                Settle(entryId, choice);
            }
        }

        Notify();
        return choice;
    }

    /// <summary>Records the answer on the question's entry (caller holds the lock).</summary>
    private void Settle(string entryId, string choice)
    {
        if (choice == "allow-chat")
        {
            _changesAllowed = true;
        }

        var entries = _chat.Entries;
        if (entries.FindIndex(e => e.Id == entryId) is var index and >= 0 && entries[index].Status == "waiting")
        {
            entries[index] = entries[index] with
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
        _runningAgent = null;
        lock (_lock)
        {
            _sessionId = null;
            _replaying = null;
        }

        if (agent is not null)
        {
            agent.Notification -= OnNotification;
            agent.RequestHandler = null;
            await agent.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnChatsChanged(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            _chatsVersion++;
        }

        Notify();
    }

    private void OnModelsChanged(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            _agents = WithModels(_acpAgents);
            if (_chat.Entries.Count == 0)
            {
                _choice = AgentCatalog.DefaultChoice(_agents, _choice);
                _chat.Agent = _choice ?? string.Empty;
            }
        }

        Notify();
    }

    /// <summary>Every change gets a new version (one per announcement, so a version always means the same state).</summary>
    private void Notify()
    {
        ChatState state;
        lock (_lock)
        {
            _version++;
            state = Snapshot();
        }

        Changed?.Invoke(this, state);
    }

    /// <summary>Streamed text is shown at most every 50 ms (the end of the answer always is).</summary>
    private void NotifyText()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastTextNotify) < 50)
        {
            return;
        }

        Interlocked.Exchange(ref _lastTextNotify, now);
        Notify();
    }

    private ChatState Snapshot() => new(_status, _chat.Entries.Count > 0 ? _chat.Agent : _choice, _agents, [.. _chat.Entries], _problem, _version, _epoch, _chat.Id, _chatsVersion);

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

    /// <summary>An API model's answer as it happens, written into the chat it belongs to (and nowhere else).</summary>
    private sealed class ApiEvents(AgentChatService chat, ChatRecord record, long generation) : IApiTurnEvents
    {
        // Models reuse call ids across answers: each call gets its own entry.
        private readonly Dictionary<string, string> _entryOf = [];
        private bool _writing;

        public void Text(string chunk)
        {
            lock (chat._lock)
            {
                if (generation != chat._generation || chunk.Length == 0)
                {
                    return;
                }

                var entries = record.Entries;
                if (_writing && entries.Count > 0 && entries[^1].Kind == "agent")
                {
                    entries[^1] = entries[^1] with { Text = entries[^1].Text + chunk };
                }
                else
                {
                    entries.Add(new ChatEntry(chat.NextId(), "agent", chunk));
                    _writing = true;
                }
            }

            chat.NotifyText();
        }

        public void ToolStarted(string callId, string name, string arguments)
        {
            lock (chat._lock)
            {
                if (generation != chat._generation)
                {
                    return;
                }

                _writing = false;
                var entryId = "tool:" + chat.NextId();
                _entryOf[callId] = entryId;
                record.Entries.Add(new ChatEntry(entryId, "tool", name.Replace('_', ' '), "in_progress"));
            }

            chat.Notify();
        }

        public void ToolFinished(string callId, string status)
        {
            lock (chat._lock)
            {
                if (generation != chat._generation || !_entryOf.TryGetValue(callId, out var entryId) || record.Entries.FindIndex(e => e.Id == entryId) is not (var index and >= 0))
                {
                    return;
                }

                record.Entries[index] = record.Entries[index] with { Status = status };
            }

            chat.Notify();
        }

        public async Task<bool> AllowAsync(string name, string arguments, CancellationToken cancellationToken)
        {
            if (chat._changesAllowed)
            {
                return true;
            }

            lock (chat._lock)
            {
                _writing = false;
            }

            var choice = await chat.AskAsync(ChangeQuestion(name, arguments), ["allow", "allow-chat", "reject"], generation, cancellationToken).ConfigureAwait(false);
            if (choice == "cancelled")
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return choice is "allow" or "allow-chat";
        }
    }
}
