using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using TodoTracker.Server.Plugins.AgentChat;

namespace TodoTracker.Server.Tests.AgentChat;

/// <summary>The JSON-RPC link to an ACP agent process, tested against a scripted fake agent.</summary>
public sealed class AcpConnectionTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly ConcurrentQueue<(string Method, JsonElement Params)> _notifications = new();
    private AcpConnection _agent = null!;

    public static AgentLaunch FakeAgent() =>
        new("fake", "dotnet", [Path.Combine(AppContext.BaseDirectory, "FakeAcpAgent.dll")], Path.GetTempPath(), new Dictionary<string, string?>());

    public async ValueTask InitializeAsync()
    {
        _agent = AcpConnection.Start(FakeAgent());
        _agent.Notification += (method, p) => _notifications.Enqueue((method, p));
        await _agent.RequestAsync("initialize", new { protocolVersion = 1, clientCapabilities = new { }, clientInfo = new { name = "tests", version = "1" } }, Timeout, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await _agent.DisposeAsync();

    private async Task<string> NewSession() =>
        (await _agent.RequestAsync("session/new", new { cwd = Path.GetTempPath(), mcpServers = Array.Empty<object>() }, Timeout, TestContext.Current.CancellationToken)).GetProperty("sessionId").GetString()!;

    private Task<JsonElement> Prompt(string session, string text) =>
        _agent.RequestAsync("session/prompt", new { sessionId = session, prompt = new[] { new { type = "text", text } } }, Timeout, TestContext.Current.CancellationToken);

    private List<string> Texts() => _notifications
        .Where(n => n.Method == "session/update" && n.Params.GetProperty("update").GetProperty("sessionUpdate").GetString() == "agent_message_chunk")
        .Select(n => n.Params.GetProperty("update").GetProperty("content").GetProperty("text").GetString()!)
        .ToList();

    [Fact]
    public async Task A_prompt_streams_message_chunks_and_ends_the_turn()
    {
        var session = await NewSession();

        var result = await Prompt(session, "hi");

        Assert.Equal("end_turn", result.GetProperty("stopReason").GetString());
        Assert.Equal(["Hello, ", "there."], Texts());
    }

    [Fact]
    public async Task Requests_from_the_agent_are_answered_by_the_handler()
    {
        _agent.RequestHandler = (method, p, _) =>
        {
            Assert.Equal("session/request_permission", method);
            Assert.Equal("edit", p.GetProperty("toolCall").GetProperty("kind").GetString());
            return Task.FromResult<object?>(new { outcome = new { outcome = "selected", optionId = "allow-once" } });
        };
        var session = await NewSession();

        await Prompt(session, "add Buy milk");

        Assert.Contains("Added \"Buy milk\".", Texts());
    }

    [Fact]
    public async Task Without_a_handler_agent_requests_get_an_error_instead_of_hanging()
    {
        var session = await NewSession();

        await Prompt(session, "add Buy milk");

        Assert.Contains("Okay, I didn't add it.", Texts());
    }

    [Fact]
    public async Task Cancel_ends_a_running_turn()
    {
        var session = await NewSession();
        var turn = Prompt(session, "slow");
        await WaitFor(() => Texts().Count > 0);

        await _agent.NotifyAsync("session/cancel", new { sessionId = session });

        Assert.Equal("cancelled", (await turn).GetProperty("stopReason").GetString());
    }

    [Fact]
    public async Task A_crash_fails_the_pending_request_and_later_ones()
    {
        var session = await NewSession();

        var error = await Assert.ThrowsAsync<AcpException>(() => Prompt(session, "crash"));

        Assert.Contains("stopped", error.Message, StringComparison.Ordinal);
        await _agent.Exited.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<AcpException>(() => NewSession());
    }

    [Fact]
    public async Task A_flood_on_stderr_never_blocks_the_agent_and_only_the_tail_is_kept()
    {
        var session = await NewSession();

        await Prompt(session, "stderr");

        Assert.InRange(_agent.StderrTail.Length, 1, 64 * 1024);
        Assert.Contains("xxxx", _agent.StderrTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Errors_from_the_agent_become_exceptions()
    {
        var error = await Assert.ThrowsAsync<AcpException>(() => _agent.RequestAsync("nope/never", null, Timeout, TestContext.Current.CancellationToken));

        Assert.Contains("Unknown method", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_that_gets_no_answer_times_out()
    {
        var session = await NewSession();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            _agent.RequestAsync("session/prompt", new { sessionId = session, prompt = new[] { new { type = "text", text = "slow" } } }, TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Disposing_stops_the_agent_and_everything_it_started()
    {
        var session = await NewSession();
        await Prompt(session, "spawn");
        var child = int.Parse(Texts().Single().Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);

        await _agent.DisposeAsync();

        await WaitFor(() => !IsRunning(child));
        Assert.True(_agent.Exited.IsCompleted);
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "timed out waiting");
            await Task.Delay(25);
        }
    }
}
