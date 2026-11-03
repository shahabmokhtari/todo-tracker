namespace OneThing.Server;

/// <summary>How to connect one AI app: a short instruction and something to paste.</summary>
public sealed record AiSetupDto(string Id, string App, string Steps, string Snippet, string Kind, string? Link = null);

public sealed record ConnectDto(string Cli, IReadOnlyList<AiSetupDto> Setups);

/// <summary>
/// Ready-made setups for AI apps, shown in "Connect an AI app". Local apps launch <c>onething mcp</c> (works whether or
/// not One Thing is running); HTTP clients use the running app's <c>/mcp</c> endpoint with the token.
/// </summary>
public static class AiSetups
{
    public const string Repository = "shahabmokhtari/todo-tracker";
    public const string DocsUrl = "https://github.com/" + Repository + "/blob/master/docs/ai-connectors.md";

    /// <summary>
    /// The command line shipped with the app (<c>onething</c>, else its short name <c>ot</c>): in the <c>cli</c> folder
    /// next to the app (the Windows app keeps it there) or next to the app itself; otherwise <c>onething</c> from the PATH.
    /// </summary>
    public static string FindCli(string? directory = null)
    {
        var dir = directory ?? AppContext.BaseDirectory;
        foreach (var folder in new[] { Path.Combine(dir, "cli"), dir })
        {
            foreach (var name in new[] { "onething", "ot" })
            {
                var path = Path.Combine(folder, OperatingSystem.IsWindows() ? name + ".exe" : name);

                // Windows file names ignore case: OneThing.exe (the app itself) isn't the command line.
                if (File.Exists(path) && !string.Equals(Path.GetFullPath(path), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }
        }

        return "onething";
    }
    public static ConnectDto For(ConnectionDto connection, string cli)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var command = Json(cli);
        var quoted = cli.Contains(' ', StringComparison.Ordinal) ? $"\"{cli}\"" : cli;

        // PowerShell needs "&" to run a quoted path; titles are quoted because shells treat # ! @ specially.
        var run = OperatingSystem.IsWindows() && quoted != cli ? "& " + quoted : quoted;
        var setups = new List<AiSetupDto>
        {
            new("claude-code", "Claude Code", "Run this once in a terminal:", $"claude mcp add onething --scope user -- {quoted} mcp", "command"),
            new("copilot-cli", "GitHub Copilot CLI", "Add this to ~/.copilot/mcp-config.json (or run /mcp add in Copilot):",
                $$"""{ "mcpServers": { "onething": { "type": "local", "command": {{command}}, "args": ["mcp"], "tools": ["*"] } } }""", "json"),
            new("claude-desktop", "Claude Desktop", "Settings → Developer → Edit config, add this, then restart Claude:",
                $$"""{ "mcpServers": { "onething": { "command": {{command}}, "args": ["mcp"] } } }""", "json"),
            new("vscode", "VS Code (Copilot Chat)", "Command palette → MCP: Open User Configuration, add:",
                $$"""{ "servers": { "onething": { "type": "stdio", "command": {{command}}, "args": ["mcp"] } } }""", "json"),
            new("plugin", "Skill + tools for Claude Code / Copilot CLI", "Also teaches the agent how you like to work (needs onething on your PATH):",
                $"/plugin marketplace add {Repository}\n/plugin install onething@onething", "command"),
            new("http", "Other MCP apps (HTTP, while One Thing runs)", "Use this server URL and header:",
                $"URL: {connection.McpUrl}\nHeader: Authorization: Bearer {connection.Token}", "text"),
            new("terminal", "Terminal and scripts", "Every command has --json for scripts and agents:",
                $"{run} now\n{run} add 'Renew passport !! due:7d #admin'\n{run} done passport", "command"),
            new("cloud", "ChatGPT, Claude.ai, Copilot Studio", "These connect from the cloud, so they need a public HTTPS address (a tunnel). Their API description:",
                $"{connection.BaseUrl}/openapi/v1.json\n{connection.BaseUrl}/openapi/swagger2.json  (Copilot Studio)", "text", DocsUrl),
        };
        return new ConnectDto(cli, setups);
    }

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);
}
