namespace TodoTracker.Server;

/// <summary>How to connect one AI app: a short instruction and something to paste.</summary>
public sealed record AiSetupDto(string Id, string App, string Steps, string Snippet, string Kind, string? Link = null);

public sealed record ConnectDto(string Tt, IReadOnlyList<AiSetupDto> Setups);

/// <summary>
/// Ready-made setups for AI apps, shown in "Connect an AI app". Local apps launch <c>tt mcp</c> (works whether or not
/// Todo Tracker is running); HTTP clients use the running app's <c>/mcp</c> endpoint with the token.
/// </summary>
public static class AiSetups
{
    public const string Repository = "shahabmokhtari/todo-tracker";
    public const string DocsUrl = "https://github.com/" + Repository + "/blob/master/docs/ai-connectors.md";

    /// <summary>The <c>tt</c> shipped next to the app if there is one, otherwise <c>tt</c> from the PATH.</summary>
    public static string FindTt(string? directory = null)
    {
        var dir = directory ?? AppContext.BaseDirectory;
        foreach (var name in OperatingSystem.IsWindows() ? new[] { "tt.exe" } : ["tt"])
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return "tt";
    }

    public static ConnectDto For(ConnectionDto connection, string tt)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var command = Json(tt);
        var quoted = tt.Contains(' ', StringComparison.Ordinal) ? $"\"{tt}\"" : tt;

        // PowerShell needs "&" to run a quoted path; titles are quoted because shells treat # ! @ specially.
        var run = OperatingSystem.IsWindows() && quoted != tt ? "& " + quoted : quoted;
        var setups = new List<AiSetupDto>
        {
            new("claude-code", "Claude Code", "Run this once in a terminal:", $"claude mcp add todo-tracker --scope user -- {quoted} mcp", "command"),
            new("copilot-cli", "GitHub Copilot CLI", "Add this to ~/.copilot/mcp-config.json (or run /mcp add in Copilot):",
                $$"""{ "mcpServers": { "todo-tracker": { "type": "local", "command": {{command}}, "args": ["mcp"], "tools": ["*"] } } }""", "json"),
            new("claude-desktop", "Claude Desktop", "Settings → Developer → Edit config, add this, then restart Claude:",
                $$"""{ "mcpServers": { "todo-tracker": { "command": {{command}}, "args": ["mcp"] } } }""", "json"),
            new("vscode", "VS Code (Copilot Chat)", "Command palette → MCP: Open User Configuration, add:",
                $$"""{ "servers": { "todo-tracker": { "type": "stdio", "command": {{command}}, "args": ["mcp"] } } }""", "json"),
            new("plugin", "Skill + tools for Claude Code / Copilot CLI", "Also teaches the agent how you like to work (needs tt on your PATH):",
                $"/plugin marketplace add {Repository}\n/plugin install todo-tracker@todo-tracker", "command"),
            new("http", "Other MCP apps (HTTP, while Todo Tracker runs)", "Use this server URL and header:",
                $"URL: {connection.McpUrl}\nHeader: Authorization: Bearer {connection.Token}", "text"),
            new("terminal", "Terminal and scripts", "Every command has --json for scripts and agents:",
                $"{run} now\n{run} add 'Renew passport !! due:7d #admin'\n{run} done passport", "command"),
            new("cloud", "ChatGPT, Claude.ai, Copilot Studio", "These connect from the cloud, so they need a public HTTPS address (a tunnel). Their API description:",
                $"{connection.BaseUrl}/openapi/v1.json\n{connection.BaseUrl}/openapi/swagger2.json  (Copilot Studio)", "text", DocsUrl),
        };
        return new ConnectDto(tt, setups);
    }

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);
}
