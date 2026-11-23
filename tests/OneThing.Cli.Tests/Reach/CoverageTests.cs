using System.Reflection;
using System.Text;
using ModelContextProtocol.Server;
using OneThing.Server;

namespace OneThing.Cli.Tests.Reach;

/// <summary>
/// One Thing is agent-first: everything the app does is reachable from ot, and everything safe for an agent from the
/// MCP tools. The server's endpoints are read from the server itself; each must be in <see cref="Coverage"/> with the
/// command and tool that do it, or a reason there is none. docs/ai-connectors.md shows the table: after a change run
/// <c>ONETHING_WRITE_DOCS=1 dotnet test tests/OneThing.Cli.Tests --filter CoverageTests</c> and commit it.
/// </summary>
public sealed class CoverageTests
{
    private const string Start = "<!-- Coverage table: made from tests/OneThing.Cli.Tests/Reach/Coverage.cs by CoverageTests. Don't edit by hand. -->";
    private const string End = "<!-- End of the coverage table. -->";

    private static readonly HashSet<string> Tools = typeof(TodoTools).GetMethods()
        .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
        .OfType<string>()
        .ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> Listed => Coverage.Features.SelectMany(f => f.Routes);

    [Fact]
    public void Every_endpoint_the_server_maps_has_a_command_and_a_tool_or_a_reason()
    {
        var missing = MappedRoutes.All.Except(Listed, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0, "Endpoints with no ot command, MCP tool or reason in tests/OneThing.Cli.Tests/Reach/Coverage.cs:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Every_endpoint_in_the_table_is_still_mapped_and_listed_once()
    {
        Assert.Empty(Listed.Except(MappedRoutes.All, StringComparer.Ordinal));
        Assert.Empty(Listed.GroupBy(r => r, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key));
    }

    [Fact]
    public void Every_command_and_tool_in_the_table_exists_and_whatever_is_missing_says_why()
    {
        foreach (var f in Coverage.Features)
        {
            Assert.All(f.Cli, c => Assert.True(CommandOf(c) is { } name && CliCommands.All.ContainsKey(name), $"{f.Name}: \"{c}\" isn't an ot command"));
            Assert.All(f.Mcp, t => Assert.True(Tools.Contains(t), $"{f.Name}: {t} isn't an MCP tool"));
            if (f.Cli.Length == 0 || f.Mcp.Length == 0)
            {
                Assert.False(string.IsNullOrWhiteSpace(f.Why), $"{f.Name} has no {(f.Cli.Length == 0 ? "ot command" : "MCP tool")}: say why");
            }
        }
    }

    [Fact]
    public void Every_command_and_every_tool_is_in_the_table()
    {
        var commands = Coverage.Features.SelectMany(f => f.Cli).Select(CommandOf).ToHashSet(StringComparer.Ordinal);
        var tools = Coverage.Features.SelectMany(f => f.Mcp).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(CliCommands.All.Keys, c => !commands.Contains(c));
        Assert.DoesNotContain(Tools, t => !tools.Contains(t));
    }

    [Fact]
    public void Nothing_that_deletes_is_open_to_agents()
    {
        // Hooks are the exception the product allows: an agent removes only a hook it added.
        var deletes = Coverage.Features.Where(f => f.Routes.Any(r => r.StartsWith("DELETE /api/", StringComparison.Ordinal) && !r.StartsWith("DELETE /api/hooks/", StringComparison.Ordinal)));

        Assert.All(deletes, f => Assert.True(f.Cli.Length == 0 && f.Mcp.Length == 0, $"{f.Name} deletes, but has an ot command or MCP tool"));
        Assert.DoesNotContain(CliCommands.All.Keys, c => CliCommands.DeleteWords.Contains(c));
        Assert.DoesNotContain(Tools, t => t.Contains("delete", StringComparison.Ordinal));
    }

    [Fact]
    public void The_coverage_table_in_the_docs_is_current()
    {
        var path = Path.Combine(Root(), "docs", "ai-connectors.md");
        var text = File.ReadAllText(path).ReplaceLineEndings("\n");
        var from = text.IndexOf(Start, StringComparison.Ordinal);
        var to = text.IndexOf(End, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from, $"docs/ai-connectors.md needs the lines {Start} and {End}");
        var expected = text[..from] + Table() + text[to..];
        if (Environment.GetEnvironmentVariable("ONETHING_WRITE_DOCS") == "1")
        {
            File.WriteAllText(path, expected);
            text = expected;
        }

        Assert.True(text == expected, "The coverage table in docs/ai-connectors.md is out of date: run ONETHING_WRITE_DOCS=1 dotnet test tests/OneThing.Cli.Tests --filter CoverageTests, and commit it.");
    }

    /// <summary>"ot plan settings --view" → plan; "onething mcp" → mcp.</summary>
    private static string? CommandOf(string usage)
    {
        var words = usage.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 2 && words[0] is "ot" or "onething" ? words[1] : null;
    }

    private static string Table()
    {
        var text = new StringBuilder();
        text.Append(Start).Append('\n').Append('\n');
        text.Append("| What | `ot` | MCP tool | Why not (where missing) |\n| --- | --- | --- | --- |\n");
        // Inside code spans < and > show as they are; | still ends a cell.
        static string Code(string[] items) => items.Length == 0 ? "—" : string.Join(", ", items.Select(i => $"`{i.Replace("|", "\\|", StringComparison.Ordinal)}`"));
        foreach (var f in Coverage.Features)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"| {Prose(f.Name)} | {Code(f.Cli)} | {Code(f.Mcp)} | {Prose(f.Cli.Length == 0 || f.Mcp.Length == 0 ? f.Why ?? string.Empty : string.Empty)} |\n");
        }

        text.Append('\n');
        return text.ToString();
    }

    private static string Prose(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OneThing.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("repository root not found");
    }
}
