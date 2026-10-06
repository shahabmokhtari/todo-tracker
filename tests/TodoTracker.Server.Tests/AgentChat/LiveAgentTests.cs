using System.Diagnostics;
using TodoTracker.Server.Plugins.AgentChat;

namespace TodoTracker.Server.Tests.AgentChat;

/// <summary>
/// Talks to the real GitHub Copilot CLI over ACP. Opt-in (needs Copilot installed and signed in):
/// set TT_LIVE_AGENT=copilot. Skipped in CI.
/// </summary>
public sealed class LiveAgentTests
{
    [Fact]
    public async Task Copilot_answers_over_acp()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TT_LIVE_AGENT") == "copilot", "Set TT_LIVE_AGENT=copilot to talk to the real Copilot CLI.");
        var work = Directory.CreateTempSubdirectory("tt-live-").FullName;
        var launch = new AgentCatalog().LaunchFor("copilot", work);

        var started = Stopwatch.StartNew();
        await using var agent = AcpConnection.Start(launch);
        try
        {
            var init = await agent.RequestAsync("initialize", new { protocolVersion = 1, clientCapabilities = new { }, clientInfo = new { name = "tt-live", version = "1" } }, TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            TestContext.Current.SendDiagnosticMessage($"initialized after {started.Elapsed}: {init.GetRawText()[..Math.Min(200, init.GetRawText().Length)]}");
            var session = await agent.RequestAsync("session/new", new { cwd = work, mcpServers = Array.Empty<object>() }, TimeSpan.FromSeconds(180), TestContext.Current.CancellationToken);
            Assert.False(string.IsNullOrEmpty(session.GetProperty("sessionId").GetString()));
        }
        catch (AcpException ex)
        {
            Assert.Fail($"{ex.Message} after {started.Elapsed}; exited={agent.Exited.IsCompleted}; stderr: {agent.StderrTail}");
        }
    }
}
