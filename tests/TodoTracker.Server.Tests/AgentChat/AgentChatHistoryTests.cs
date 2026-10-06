using System.Text.Json.Nodes;
using TodoTracker.Server.Plugins.AgentChat;

namespace TodoTracker.Server.Tests.AgentChat;

/// <summary>
/// Chats with history: every chat is kept and can be reopened and carried on (an agent loads its session, or is told
/// what was said), a chat stays with its agent or model, and API models chat with the same tools and permissions.
/// </summary>
public sealed class AgentChatHistoryTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly string _dir = Directory.CreateTempSubdirectory("tt-chats-").FullName;
    private readonly FakeModel _model = new();
    private readonly FakeTools _tools = new();
    private readonly List<AgentChatService> _services = [];
    private AgentChatService _chat = null!;

    private string Log => Path.Combine(_dir, "agent.log");

    public ValueTask InitializeAsync()
    {
        var models = new ApiModelStore(Path.Combine(_dir, "agent-chat"));
        models.Add(FakeModel.Model, "sk-test");
        _chat = Create();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var service in _services)
        {
            await service.DisposeAsync();
        }

        _model.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A chat service like the app's, on the same data folder each time (as after a restart).</summary>
    private AgentChatService Create(string? load = null)
    {
        var catalog = new AgentCatalog(searchPath: _dir);
        var fake = AcpConnectionTests.FakeAgent();
        catalog.Extra.Add((new AgentOption("fake", "Fake agent", true, null), work => fake with
        {
            WorkingDirectory = work,
            Environment = new Dictionary<string, string?> { ["FAKE_ACP_LOG"] = Log, ["FAKE_ACP_LOAD"] = load },
        }));
        var data = Path.Combine(_dir, "agent-chat");
        var service = new AgentChatService(catalog, new AgentChatOptions
        {
            WorkDirectory = Path.Combine(data, "work"),
            SettingsPath = Path.Combine(data, "settings.json"),
            History = new ChatHistoryStore(Path.Combine(data, "chats"), TimeProvider.System),
            Models = new ApiModelStore(data),
            ToolsFor = _ => _tools,
            ApiHttp = new HttpClient(_model, disposeHandler: false),
        });
        _services.Add(service);
        return service;
    }

    private List<JsonNode> Received(string method) =>
        File.ReadAllLines(Log).Select(l => JsonNode.Parse(l)!).Where(m => m["method"]?.GetValue<string>() == method).ToList();

    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            await Task.Delay(25);
        }
    }

    private async Task<ChatEntry> WaitForQuestion()
    {
        await WaitFor(() => _chat.State.Entries.Any(e => e is { Kind: "permission", Status: "waiting" }));
        return _chat.State.Entries.Last(e => e.Kind == "permission");
    }

    [Fact]
    public async Task Every_chat_is_kept_and_comes_back_when_opened()
    {
        await _chat.SendAsync("Plan my week").WaitAsync(Timeout);
        var first = _chat.State.ChatId;
        await _chat.NewChatAsync();
        Assert.Empty(_chat.State.Entries);
        Assert.NotEqual(first, _chat.State.ChatId);

        await _chat.OpenChatAsync(first).WaitAsync(Timeout);

        Assert.Equal(first, _chat.State.ChatId);
        Assert.Equal(["Plan my week", "Hello, there."], _chat.State.Entries.Select(e => e.Text));
        var kept = Assert.Single(_chat.Chats);
        Assert.Equal("Plan my week", kept.Title);
        Assert.Equal("fake", kept.Agent);
    }

    [Fact]
    public async Task The_list_of_chats_changing_is_announced_without_sending_the_list()
    {
        var before = _chat.State.ChatsVersion;

        await _chat.SendAsync("hi").WaitAsync(Timeout);

        Assert.True(_chat.State.ChatsVersion > before);
    }

    [Fact]
    public async Task An_agent_that_can_load_sessions_carries_the_chat_on_after_a_restart_without_repeating_it()
    {
        _chat = Create(load: "1");
        await _chat.SendAsync("Remember PINEAPPLE").WaitAsync(Timeout);
        var id = _chat.State.ChatId;
        var session = Received("session/new").Single()["params"]!.ToJsonString();
        await _chat.DisposeAsync();

        _chat = Create(load: "1");
        await _chat.OpenChatAsync(id);
        await _chat.SendAsync("What did I say?").WaitAsync(Timeout);

        var load = Received("session/load").Single()["params"]!;
        Assert.StartsWith("sess-", load["sessionId"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Single(Received("session/new"));
        Assert.Equal(Path.Combine(_dir, "agent-chat", "work"), load["cwd"]!.GetValue<string>());
        Assert.NotNull(session);

        // What the agent replays (before and after it answers the load) is already in the chat: not shown twice.
        Assert.DoesNotContain(_chat.State.Entries, e => e.Text.Contains("REPLAYED", StringComparison.Ordinal));
        Assert.Equal(["user", "agent", "user", "agent"], _chat.State.Entries.Select(e => e.Kind));
        var prompt = Received("session/prompt")[^1]["params"]!["prompt"]!.ToJsonString();
        Assert.DoesNotContain("Earlier in this chat", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("fail")]
    public async Task An_agent_that_can_not_load_the_session_is_told_what_was_said(string? load)
    {
        _chat = Create(load);
        await _chat.SendAsync("Remember PINEAPPLE").WaitAsync(Timeout);
        var id = _chat.State.ChatId;
        await _chat.DisposeAsync();

        _chat = Create(load);
        await _chat.OpenChatAsync(id);
        await _chat.SendAsync("What did I say?").WaitAsync(Timeout);

        var prompt = Received("session/prompt")[^1]["params"]!["prompt"]!.AsArray();
        Assert.Contains("Earlier in this chat", prompt.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("PINEAPPLE", prompt.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("What did I say?", prompt[^1]!["text"]!.GetValue<string>());
        Assert.Contains(_chat.State.Entries, e => e.Kind == "note" && e.Text.Contains("new session", StringComparison.Ordinal));
        Assert.Equal(2, Received("session/new").Count);
    }

    [Fact]
    public async Task Choosing_someone_else_for_a_chat_that_has_begun_starts_a_new_chat()
    {
        await _chat.SendAsync("hi").WaitAsync(Timeout);
        var first = _chat.State.ChatId;

        await _chat.SelectAsync("api:" + FakeModel.Model.Id);

        Assert.NotEqual(first, _chat.State.ChatId);
        Assert.Empty(_chat.State.Entries);
        Assert.Equal("api:" + FakeModel.Model.Id, _chat.State.Agent);
        Assert.Equal("fake", _chat.Chats.Single(c => c.Id == first).Agent);
    }

    [Fact]
    public async Task API_models_are_offered_beside_the_agents()
    {
        var option = _chat.State.Agents.Single(a => a.Id == "api:" + FakeModel.Model.Id);

        Assert.Equal("api", option.Kind);
        Assert.Equal("Fake GPT", option.Name);
        Assert.False(option.Local);
        Assert.True(option.Installed);
    }

    [Fact]
    public async Task An_API_model_answers_and_its_conversation_is_kept()
    {
        _model.Says("Hello from the API.").Says("Still here.");
        await _chat.SelectAsync("api:" + FakeModel.Model.Id);

        await _chat.SendAsync("hi").WaitAsync(Timeout);
        await _chat.SendAsync("again").WaitAsync(Timeout);

        Assert.Equal(["hi", "Hello from the API.", "again", "Still here."], _chat.State.Entries.Select(e => e.Text));
        Assert.Equal("ready", _chat.State.Status);
        var second = _model.Requests[1]["messages"]!.AsArray().Select(m => m!["role"]!.GetValue<string>());
        Assert.Equal(["system", "user", "assistant", "user"], second);
    }

    [Fact]
    public async Task An_API_model_asks_before_changing_tasks_and_shows_what_it_would_do()
    {
        _model.Calls(("c1", "get_dashboard", "{}"), ("c2", "create_task", """{"title":"Milk"}""")).Says("Added Milk.");
        await _chat.SelectAsync("api:" + FakeModel.Model.Id);

        var turn = _chat.SendAsync("add milk");
        var question = await WaitForQuestion();
        Assert.Contains("create task", question.Text, StringComparison.Ordinal);
        Assert.Contains("Milk", question.Text, StringComparison.Ordinal);
        Assert.Equal(["get_dashboard"], _tools.Calls.Select(c => c.Name));
        await _chat.AnswerAsync(question.Id, "allow");
        await turn.WaitAsync(Timeout);

        Assert.Equal(["get_dashboard", "create_task"], _tools.Calls.Select(c => c.Name));
        Assert.All(_chat.State.Entries.Where(e => e.Kind == "tool"), e => Assert.Equal("completed", e.Status));
        Assert.Equal("Added Milk.", _chat.State.Entries[^1].Text);
    }

    [Fact]
    public async Task Allowing_changes_for_the_chat_is_forgotten_in_another_chat()
    {
        _model.Calls(("c1", "create_task", """{"title":"A"}""")).Says("ok")
            .Calls(("c2", "create_task", """{"title":"B"}""")).Says("ok");
        await _chat.SelectAsync("api:" + FakeModel.Model.Id);
        var turn = _chat.SendAsync("add A");
        await _chat.AnswerAsync((await WaitForQuestion()).Id, "allow-chat");
        await turn.WaitAsync(Timeout);

        await _chat.NewChatAsync();
        turn = _chat.SendAsync("add B");

        var question = await WaitForQuestion();
        await _chat.AnswerAsync(question.Id, "reject");
        await turn.WaitAsync(Timeout);
        Assert.Single(_tools.Calls);
    }

    [Fact]
    public async Task Stopping_an_API_answer_and_opening_another_chat_never_mixes_them()
    {
        _model.Hangs();
        await _chat.SelectAsync("api:" + FakeModel.Model.Id);
        var turn = _chat.SendAsync("slow please");
        await WaitFor(() => _chat.State.Status == "busy");
        var first = _chat.State.ChatId;

        await _chat.NewChatAsync().WaitAsync(Timeout);

        Assert.True(turn.IsCompleted);
        Assert.Empty(_chat.State.Entries);
        await _chat.OpenChatAsync(first);
        Assert.Equal("Stopped.", _chat.State.Entries[^1].Text);
    }

    [Fact]
    public async Task A_model_that_was_removed_says_so_instead_of_failing_quietly()
    {
        await _chat.SelectAsync("api:" + FakeModel.Model.Id);
        _model.Says("hi");
        await _chat.SendAsync("hi").WaitAsync(Timeout);
        var id = _chat.State.ChatId;
        new ApiModelStore(Path.Combine(_dir, "agent-chat")).Remove(FakeModel.Model.Id);

        _chat = Create();
        await _chat.OpenChatAsync(id);

        var error = Assert.Throws<InvalidOperationException>(() => { _ = _chat.SendAsync("more"); });
        Assert.Contains("new chat", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_the_open_chat_starts_a_new_one_and_it_can_be_brought_back()
    {
        await _chat.SendAsync("oops").WaitAsync(Timeout);
        var id = _chat.State.ChatId;

        Assert.True(await _chat.DeleteChatAsync(id));

        Assert.NotEqual(id, _chat.State.ChatId);
        Assert.Empty(_chat.Chats);
        Assert.True(_chat.RestoreChat(id));
        Assert.Equal(id, Assert.Single(_chat.Chats).Id);
    }

    [Fact]
    public async Task A_chat_can_be_renamed_and_found()
    {
        await _chat.SendAsync("hi").WaitAsync(Timeout);
        var id = _chat.State.ChatId;

        _chat.RenameChat(id, "Dentist plans");
        await _chat.SendAsync("more").WaitAsync(Timeout);

        Assert.Equal("Dentist plans", Assert.Single(_chat.Chats).Title);
        Assert.Equal(id, Assert.Single(_chat.Search("dentist")).Id);
        Assert.Equal(id, Assert.Single(_chat.Search("Hello, there")).Id);
    }
}
