using System.Net;
using TodoTracker.Server.Plugins.AgentChat;

namespace TodoTracker.Server.Tests.AgentChat;

/// <summary>One answer from an API model: text, tool calls (reads freely, changes with permission), within limits.</summary>
public sealed class ApiTurnRunnerTests : IAsyncDisposable
{
    private readonly FakeModel _model = new();
    private readonly FakeTools _tools = new();
    private readonly Events _events = new();
    private readonly List<ApiMessage> _messages = [new ApiMessage("user", "hi")];

    private sealed class Events : IApiTurnEvents
    {
        public string Text { get; private set; } = string.Empty;

        public List<string> Log { get; } = [];

        public bool Allow { get; set; } = true;

        public Func<CancellationToken, Task<bool>>? Asking { get; set; }

        void IApiTurnEvents.Text(string chunk) => Text += chunk;

        void IApiTurnEvents.ToolStarted(string callId, string name, string arguments) => Log.Add($"start {name}");

        void IApiTurnEvents.ToolFinished(string callId, string status) => Log.Add($"end {status}");

        Task<bool> IApiTurnEvents.AllowAsync(string name, string arguments, CancellationToken cancellationToken)
        {
            Log.Add($"ask {name} {arguments}");
            return Asking?.Invoke(cancellationToken) ?? Task.FromResult(Allow);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _model.Dispose();
        await _tools.DisposeAsync();
    }

    private Task<string> Run(ApiTurnLimits? limits = null, CancellationToken cancellationToken = default) =>
        new ApiTurnRunner(new ApiChatClient(new HttpClient(_model)), limits ?? new ApiTurnLimits())
            .RunAsync(FakeModel.Model, "key", "Be brief.", _messages, _tools, _events, cancellationToken == default ? TestContext.Current.CancellationToken : cancellationToken);

    [Fact]
    public async Task A_plain_answer_streams_its_text_and_joins_the_conversation()
    {
        _model.Says("Hello there.");

        Assert.Equal("end_turn", await Run());

        Assert.Equal("Hello there.", _events.Text);
        Assert.Equal(["user", "assistant"], _messages.Select(m => m.Role));
        Assert.Equal("Hello there.", _messages[1].Text);
    }

    [Fact]
    public async Task Reading_needs_no_permission_and_the_result_goes_back_to_the_model()
    {
        _model.Calls(("c1", "get_dashboard", "{}")).Says("You have 3.");

        await Run();

        Assert.Equal(["start get_dashboard", "end completed"], _events.Log);
        Assert.Equal(["user", "assistant", "tool", "assistant"], _messages.Select(m => m.Role));
        Assert.Equal("3 things to do", _messages[2].Text);
        Assert.Equal("c1", _model.Requests[1]["messages"]![3]!["tool_call_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_change_asks_first_showing_what_it_would_do_and_a_no_is_told_to_the_model()
    {
        _events.Allow = false;
        _model.Calls(("c1", "create_task", """{"title":"Milk"}""")).Says("Okay, not added.");

        await Run();

        Assert.Equal(["ask create_task {\"title\":\"Milk\"}", "start create_task", "end rejected"], _events.Log);
        Assert.Empty(_tools.Calls);
        Assert.Contains("declined", _messages[2].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_tool_that_is_not_offered_is_refused_without_running()
    {
        _model.Calls(("c1", "delete_everything", "{}")).Says("Sorry.");

        await Run();

        Assert.Empty(_tools.Calls);
        Assert.Contains("no tool", _messages[2].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["start delete_everything", "end failed"], _events.Log);
    }

    [Fact]
    public async Task A_model_that_keeps_calling_tools_is_stopped_with_every_call_answered()
    {
        for (var i = 0; i < 5; i++)
        {
            _model.Calls(($"c{i}", "get_dashboard", "{}"));
        }

        Assert.Equal("max_turn_requests", await Run(new ApiTurnLimits(MaxRounds: 3)));

        Assert.Equal(3, _model.Requests.Count);
        AssertEveryCallAnswered();
    }

    [Fact]
    public async Task Too_many_tool_calls_in_one_answer_are_not_run()
    {
        _model.Calls(("a", "get_dashboard", "{}"), ("b", "get_dashboard", "{}"), ("c", "get_dashboard", "{}")).Says("Done.");

        await Run(new ApiTurnLimits(MaxToolCalls: 2));

        Assert.Equal(2, _tools.Calls.Count);
        Assert.Contains("too many", _messages[4].Text, StringComparison.OrdinalIgnoreCase);
        AssertEveryCallAnswered();
    }

    [Fact]
    public async Task Long_tool_results_are_cut_and_say_so()
    {
        _tools.DashboardText = new string('x', 500);
        _model.Calls(("c1", "get_dashboard", "{}")).Says("ok");

        await Run(new ApiTurnLimits(MaxResultChars: 100));

        Assert.StartsWith(new string('x', 100), _messages[2].Text, StringComparison.Ordinal);
        Assert.Contains("cut", _messages[2].Text, StringComparison.Ordinal);
        Assert.True(_messages[2].Text!.Length < 200);
    }

    [Fact]
    public async Task Stopping_while_a_change_waits_for_permission_leaves_a_conversation_that_can_go_on()
    {
        using var stop = new CancellationTokenSource();
        _events.Asking = async ct =>
        {
            await stop.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return true;
        };
        _model.Calls(("c1", "create_task", "{}"), ("c2", "get_dashboard", "{}"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(cancellationToken: stop.Token));

        Assert.Empty(_tools.Calls);
        AssertEveryCallAnswered();
    }

    [Fact]
    public async Task Service_errors_say_what_to_check()
    {
        _model.Fails(HttpStatusCode.Unauthorized, "bad key");

        var error = await Assert.ThrowsAsync<ApiChatException>(() => Run());

        Assert.Contains("API key", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Every tool call has its result (APIs refuse a conversation where one is missing).</summary>
    private void AssertEveryCallAnswered()
    {
        var calls = _messages.Where(m => m.ToolCalls is not null).SelectMany(m => m.ToolCalls!).Select(c => c.Id).ToList();
        var answered = _messages.Where(m => m.Role == "tool").Select(m => m.ToolCallId!).ToList();
        Assert.Equal(calls, answered);
    }
}
