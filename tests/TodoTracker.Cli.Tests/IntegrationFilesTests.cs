using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using TodoTracker.Server;

namespace TodoTracker.Cli.Tests;

/// <summary>
/// The files AI apps install from (plugin marketplace, Claude Desktop extension, skill, VS Code sample) must match the
/// real tools, commands and version, or agents get told about things that don't exist.
/// </summary>
public sealed partial class IntegrationFilesTests
{
    private static readonly string Root = FindRoot();

    private static readonly HashSet<string> Tools = typeof(TodoTools).GetMethods()
        .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
        .OfType<string>()
        .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Both_marketplaces_list_the_plugin_from_this_repository()
    {
        var claude = Read(".claude-plugin", "marketplace.json");
        var copilot = Read(".github", "plugin", "marketplace.json");

        Assert.Equal(claude.ToJsonString(), copilot.ToJsonString());
        var entry = claude["plugins"]![0]!;
        var source = entry["source"]!.GetValue<string>();
        Assert.StartsWith("./", source, StringComparison.Ordinal);
        var plugin = Read(source[2..], ".claude-plugin", "plugin.json");
        Assert.Equal(entry["name"]!.GetValue<string>(), plugin["name"]!.GetValue<string>());
        Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", plugin["name"]!.GetValue<string>());
    }

    [Fact]
    public void Every_version_matches_the_cli()
    {
        var version = McpInfo.Version;

        Assert.Equal(version, Read(".claude-plugin", "marketplace.json")["plugins"]![0]!["version"]!.GetValue<string>());
        Assert.Equal(version, Read("integrations", "agent-plugin", ".claude-plugin", "plugin.json")["version"]!.GetValue<string>());
        Assert.Equal(version, Read("integrations", "claude-desktop", "manifest.json")["version"]!.GetValue<string>());
    }

    [Fact]
    public void The_plugin_and_vscode_sample_launch_tt_mcp()
    {
        var plugin = Read("integrations", "agent-plugin", ".mcp.json")["mcpServers"]!["todo-tracker"]!;
        var vscode = Read("integrations", "vscode", "mcp.json")["servers"]!["todo-tracker"]!;

        foreach (var server in new[] { plugin, vscode })
        {
            Assert.Equal("tt", server["command"]!.GetValue<string>());
            Assert.Equal(["mcp"], server["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        }
    }

    [Fact]
    public void The_claude_desktop_extension_lists_exactly_the_real_tools()
    {
        var manifest = Read("integrations", "claude-desktop", "manifest.json");

        var listed = manifest["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(Tools.Order(StringComparer.Ordinal), listed.Order(StringComparer.Ordinal));
        Assert.Equal("binary", manifest["server"]!["type"]!.GetValue<string>());
        Assert.Equal(["mcp"], manifest["server"]!["mcp_config"]!["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.True(File.Exists(Path.Combine(Root, "integrations", "claude-desktop", manifest["icon"]!.GetValue<string>())));
    }

    [Fact]
    public void The_skill_follows_the_agent_skills_format_and_only_mentions_real_tools_and_commands()
    {
        var path = Path.Combine(Root, "integrations", "agent-plugin", "skills", "todo-tracker", "SKILL.md");
        var text = File.ReadAllText(path).ReplaceLineEndings("\n");

        var front = Frontmatter().Match(text);
        Assert.True(front.Success, "SKILL.md needs YAML frontmatter");
        var fields = front.Groups[1].Value.Split('\n').Select(l => l.Split(':', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => p[1].Trim());
        Assert.Equal("todo-tracker", fields["name"]);
        Assert.Equal(Path.GetFileName(Path.GetDirectoryName(path)), fields["name"]);
        Assert.InRange(fields["description"].Length, 20, 1024);

        var mentionedTools = ToolMention().Matches(text).Select(m => m.Groups[1].Value).Where(n => n.Contains('_', StringComparison.Ordinal)).ToHashSet();
        Assert.NotEmpty(mentionedTools);
        Assert.All(mentionedTools, t => Assert.Contains(t, Tools));

        var mentionedCommands = CommandMention().Matches(text).Select(m => m.Groups[1].Value).ToHashSet();
        Assert.Contains("now", mentionedCommands);
        Assert.All(mentionedCommands, c => Assert.True(CliCommands.All.ContainsKey(c), $"tt {c} isn't a command"));
    }

    private static JsonNode Read(params string[] parts) => JsonNode.Parse(File.ReadAllText(Path.Combine([Root, .. parts])))!;

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TodoTracker.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("repository root not found");
    }

    [GeneratedRegex(@"^---\n(.*?)\n---\n", RegexOptions.Singleline)]
    private static partial Regex Frontmatter();

    [GeneratedRegex(@"`([a-z]+(?:_[a-z]+)+)`")]
    private static partial Regex ToolMention();

    [GeneratedRegex(@"`tt ([a-z]+)")]
    private static partial Regex CommandMention();
}
