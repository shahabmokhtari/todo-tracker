namespace TodoTracker.Server.Plugins.AgentChat;

/// <summary>An AI agent the chat can use, whether it is installed, and how to get it if not.</summary>
public sealed record AgentOption(string Id, string Name, bool Installed, string? Hint);

/// <summary>
/// Finds the agents installed on this computer: GitHub Copilot CLI (<c>copilot --acp --stdio</c>) and Claude Code
/// (through its ACP adapter: an installed <c>claude-agent-acp</c>, else a pinned version run with npx). Nothing is
/// started or downloaded here; agents start only when the person opens the chat.
/// </summary>
public sealed class AgentCatalog
{
    public const string ClaudeAdapterVersion = "0.84.0";
    private const string ClaudeAdapterPackage = "@agentclientprotocol/claude-agent-acp";

    private readonly string? _searchPath;

    /// <param name="searchPath">Folders to look in (PATH format); null: the PATH.</param>
    public AgentCatalog(string? searchPath = null)
    {
        _searchPath = searchPath;
    }

    /// <summary>Extra agents (tests point this at a scripted agent).</summary>
    public IList<(AgentOption Option, Func<string, AgentLaunch> Launch)> Extra { get; } = [];

    public IReadOnlyList<AgentOption> Detect()
    {
        var list = new List<AgentOption>
        {
            new("copilot", "GitHub Copilot", Find("copilot") is not null, Find("copilot") is null ? "Install GitHub Copilot CLI (winget install GitHub.Copilot) and sign in once with copilot." : null),
            Claude(),
        };
        list.AddRange(Extra.Select(e => e.Option));
        return list;
    }

    /// <summary>The saved choice if it is still installed; else Copilot, then Claude; null when none is installed.</summary>
    public static string? DefaultChoice(IReadOnlyList<AgentOption> agents, string? saved)
    {
        ArgumentNullException.ThrowIfNull(agents);
        return agents.FirstOrDefault(a => a.Installed && a.Id == saved)?.Id ?? agents.FirstOrDefault(a => a.Installed)?.Id;
    }

    public AgentLaunch LaunchFor(string id, string workingDirectory)
    {
        var empty = new Dictionary<string, string?>();
        switch (id)
        {
            case "copilot":
                return new AgentLaunch(id, Find("copilot") ?? throw Missing("GitHub Copilot CLI"), ["--acp", "--stdio"], workingDirectory, empty);
            case "claude":
                if (Find("claude-agent-acp") is { } adapter)
                {
                    return new AgentLaunch(id, adapter, [], workingDirectory, empty);
                }

                var npx = Find("npx") ?? throw Missing("Node.js (for the Claude adapter)");
                return new AgentLaunch(id, npx, ["--yes", $"{ClaudeAdapterPackage}@{ClaudeAdapterVersion}"], workingDirectory, empty);
            default:
                var extra = Extra.FirstOrDefault(e => e.Option.Id == id);
                return extra.Launch is { } launch ? launch(workingDirectory) : throw new ArgumentException($"Unknown agent \"{id}\".", nameof(id));
        }
    }

    private static InvalidOperationException Missing(string what) => new($"{what} isn't installed.");

    private AgentOption Claude()
    {
        var hasAdapter = Find("claude-agent-acp") is not null;
        var hasClaude = Find("claude") is not null;
        var hasNode = Find("npx") is not null;
        string? hint = (hasAdapter, hasClaude, hasNode) switch
        {
            (true, _, _) => null,
            (_, false, _) => "Install Claude Code (claude.ai/download) and sign in once with claude.",
            (_, true, false) => "Install Node.js (nodejs.org): the chat talks to Claude Code through a small adapter that runs on it.",
            _ => "The first chat downloads the Claude adapter (needs internet once).",
        };
        return new AgentOption("claude", "Claude Code", hasAdapter || (hasClaude && hasNode), hint);
    }

    private string? Find(string name)
    {
        var path = _searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(dir.Trim('"'), name + extension.ToLowerInvariant());
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
