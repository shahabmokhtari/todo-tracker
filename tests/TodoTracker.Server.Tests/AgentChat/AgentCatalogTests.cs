using TodoTracker.Server.Plugins.AgentChat;

namespace TodoTracker.Server.Tests.AgentChat;

public sealed class AgentCatalogTests : IDisposable
{
    private readonly string _bin = Directory.CreateTempSubdirectory("tt-agents-").FullName;

    public void Dispose() => Directory.Delete(_bin, recursive: true);

    private AgentCatalog Catalog(params string[] installed)
    {
        foreach (var name in installed)
        {
            File.WriteAllText(Path.Combine(_bin, OperatingSystem.IsWindows() ? name + (name == "npx" ? ".cmd" : ".exe") : name), string.Empty);
        }

        return new AgentCatalog(_bin);
    }

    [Fact]
    public void Nothing_installed_explains_how_to_get_an_agent()
    {
        var agents = Catalog().Detect();

        Assert.All(agents, a => Assert.False(a.Installed));
        Assert.Contains(agents, a => a.Id == "copilot" && a.Hint!.Contains("GitHub Copilot CLI", StringComparison.Ordinal));
        Assert.Contains(agents, a => a.Id == "claude" && a.Hint!.Contains("Claude Code", StringComparison.Ordinal));
        Assert.Null(AgentCatalog.DefaultChoice(agents, saved: null));
    }

    [Fact]
    public void Copilot_runs_in_acp_mode()
    {
        var catalog = Catalog("copilot");

        var launch = catalog.LaunchFor("copilot", _bin);

        Assert.EndsWith(OperatingSystem.IsWindows() ? "copilot.exe" : "copilot", launch.Command, StringComparison.Ordinal);
        // Its own home and logs (the person's own MCP servers made Copilot 1.0.93 crash); their home if sign-in fails.
        Assert.Equal(["--acp", "--stdio", "--log-dir", Path.Combine(_bin, "logs")], launch.Arguments);
        Assert.Equal(Path.Combine(_bin, "copilot-home"), launch.Environment["COPILOT_HOME"]);
        Assert.Empty(launch.SignInFallback!.Environment);
        Assert.Equal("copilot", AgentCatalog.DefaultChoice(catalog.Detect(), saved: null));
    }

    [Fact]
    public void Claude_uses_the_installed_adapter_or_a_pinned_one_through_npx()
    {
        var viaNpx = Catalog("claude", "npx").LaunchFor("claude", _bin);
        Assert.Contains("@agentclientprotocol/claude-agent-acp@" + AgentCatalog.ClaudeAdapterVersion, viaNpx.Arguments);
        Assert.Contains(Catalog().Detect(), a => a.Id == "claude");

        var adapter = Catalog("claude-agent-acp").LaunchFor("claude", _bin);
        Assert.Contains("claude-agent-acp", adapter.Command, StringComparison.Ordinal);
        Assert.Empty(adapter.Arguments);
    }

    [Fact]
    public void Claude_without_node_says_what_is_missing()
    {
        var claude = Catalog("claude").Detect().Single(a => a.Id == "claude");

        Assert.False(claude.Installed);
        Assert.Contains("Node.js", claude.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void When_both_are_installed_the_saved_choice_wins()
    {
        var agents = Catalog("copilot", "claude", "npx").Detect();

        Assert.True(agents.All(a => a.Installed));
        Assert.Equal("copilot", AgentCatalog.DefaultChoice(agents, saved: null));
        Assert.Equal("claude", AgentCatalog.DefaultChoice(agents, saved: "claude"));
        Assert.Equal("copilot", AgentCatalog.DefaultChoice(agents, saved: "gone"));
    }
}
