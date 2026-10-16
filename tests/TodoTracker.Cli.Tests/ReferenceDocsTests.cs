using System.ComponentModel;
using System.Reflection;
using System.Text;
using ModelContextProtocol.Server;
using TodoTracker.Server;

namespace TodoTracker.Cli.Tests;

/// <summary>
/// docs/mcp.md and docs/cli.md are made from the code (the MCP tools and the tt commands), so what agents and people
/// read is what exists. A change to a tool or a command fails here until the docs are made again:
/// <c>TT_WRITE_DOCS=1 dotnet test tests/TodoTracker.Cli.Tests --filter ReferenceDocsTests</c>, then commit them.
/// </summary>
public sealed class ReferenceDocsTests
{
    private const string Generated = "<!-- Made from the code by tests/TodoTracker.Cli.Tests/ReferenceDocsTests.cs. Don't edit by hand: run `TT_WRITE_DOCS=1 dotnet test tests/TodoTracker.Cli.Tests --filter ReferenceDocsTests`. -->";

    private static readonly string Root = FindRoot();

    [Fact]
    public void The_MCP_tool_reference_is_current() => Check(Path.Combine(Root, "docs", "mcp.md"), McpReference());

    [Fact]
    public void The_tt_command_reference_is_current() => Check(Path.Combine(Root, "docs", "cli.md"), CliReference());

    [Fact]
    public void The_skill_and_the_server_instructions_tell_agents_about_every_tool()
    {
        var skill = File.ReadAllText(Path.Combine(Root, "integrations", "agent-plugin", "skills", "todo-tracker", "SKILL.md"));
        Assert.All(Tools(), t => Assert.True(skill.Contains($"`{t.Name}`", StringComparison.Ordinal), $"SKILL.md doesn't mention {t.Name}"));
        Assert.All(CliCommands.All.Keys.Where(c => c != "mcp"), c => Assert.True(skill.Contains($"`tt {c}", StringComparison.Ordinal), $"SKILL.md doesn't mention tt {c}"));
    }

    private static void Check(string path, string expected)
    {
        if (Environment.GetEnvironmentVariable("TT_WRITE_DOCS") == "1")
        {
            File.WriteAllText(path, expected);
        }

        var actual = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : string.Empty;
        Assert.True(actual == expected, $"{Path.GetFileName(path)} is out of date: run TT_WRITE_DOCS=1 dotnet test tests/TodoTracker.Cli.Tests --filter ReferenceDocsTests, and commit it.");
    }

    private static IEnumerable<(string Name, McpServerToolAttribute Tool, MethodInfo Method)> Tools() =>
        typeof(TodoTools).GetMethods()
            .Select(m => (Method: m, Tool: m.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(x => x.Tool?.Name is not null)
            .Select(x => (x.Tool!.Name!, x.Tool!, x.Method));

    private static string McpReference()
    {
        var text = new StringBuilder();
        void Line(string line = "") => text.Append(line).Append('\n');
        Line("# MCP tools");
        Line();
        Line(Generated);
        Line();
        Line("Todo Tracker's MCP server is `tt mcp` (stdio; works without the app running) or `http://127.0.0.1:5317/mcp` while");
        Line("the app runs (with the API token). Setup for each AI app: [ai-connectors.md](ai-connectors.md). Changes are");
        Line("attributed to the agent and versioned; there is deliberately no tool that deletes a task.");
        Line();
        Line("## What the server tells agents when they connect");
        Line();
        foreach (var line in McpInfo.Instructions.Split('\n'))
        {
            Line($"> {line}");
        }

        Line();
        Line("## Tools");
        Line();
        Line("| Tool | What it does |");
        Line("| --- | --- |");
        foreach (var (name, _, method) in Tools())
        {
            Line($"| [`{name}`](#{name}) | {Cell(FirstSentence(Describe(method)))} |");
        }

        foreach (var (name, tool, method) in Tools())
        {
            Line();
            Line($"### `{name}`");
            Line();
            // Only what the tool says about itself (the MCP defaults count every tool as destructive).
            var said = method.CustomAttributes.First(a => a.AttributeType == typeof(McpServerToolAttribute)).NamedArguments
                .Where(a => a.TypedValue.Value is true).Select(a => a.MemberName).ToHashSet(StringComparer.Ordinal);
            var traits = new List<string>();
            if (said.Contains(nameof(McpServerToolAttribute.ReadOnly)))
            {
                traits.Add("read-only");
            }

            if (said.Contains(nameof(McpServerToolAttribute.Idempotent)))
            {
                traits.Add("idempotent");
            }

            if (said.Contains(nameof(McpServerToolAttribute.Destructive)))
            {
                traits.Add("removes something");
            }

            Line(Escape(Describe(method)) + (traits.Count > 0 ? $" *({string.Join(", ", traits)})*" : string.Empty));
            var parameters = method.GetParameters().Where(p => IsArgument(p.ParameterType)).ToList();
            if (parameters.Count == 0)
            {
                continue;
            }

            Line();
            Line("| Parameter | Type | | Description |");
            Line("| --- | --- | --- | --- |");
            foreach (var p in parameters)
            {
                var required = !p.HasDefaultValue && Nullable.GetUnderlyingType(p.ParameterType) is null && !IsNullableReference(p);
                Line($"| `{p.Name}` | {TypeName(p.ParameterType)} | {(required ? "required" : "optional")} | {Cell(p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty)} |");
            }
        }

        return text.ToString();
    }

    private static string CliReference()
    {
        var text = new StringBuilder();
        void Line(string line = "") => text.Append(line).Append('\n');
        Line("# The `tt` command");
        Line();
        Line(Generated);
        Line();
        Line("`tt` reads and changes your tasks (the markdown files in your tasks folder) from a terminal or a script, without");
        Line("the app running, and `tt mcp` is the MCP server for AI apps. It's next to the app on Windows, in `~/.local/bin`");
        Line("with the Linux install, or `dotnet tool install -g TodoTracker.Cli` ([ai-connectors.md](ai-connectors.md#get-tt)).");
        Line();
        Line("`<task>` is an id (or its first 6+ characters, as `tt` prints them) or words from the title. Exit codes: `0` done,");
        Line("`1` couldn't (the reason is on stderr), `2` wrong usage.");
        Line();
        Line("## Options for every command");
        Line();
        Line("| Option | What it does |");
        Line("| --- | --- |");
        foreach (var (option, what) in CliCommands.GlobalOptions)
        {
            Line($"| `{option}` | {Cell(what)} |");
        }

        Line();
        Line("## Commands");
        Line();
        Line("| Command | What it does |");
        Line("| --- | --- |");
        foreach (var c in CliCommands.All.Values)
        {
            Line($"| [`tt {c.Name}`](#tt-{c.Name}) | {Cell(c.Summary)} |");
        }

        foreach (var c in CliCommands.All.Values)
        {
            Line();
            Line($"### tt {c.Name}");
            Line();
            Line(c.Summary + ".");
            Line();
            Line("```");
            Line(c.Usage);
            Line("```");
            if (c.Details is { } details)
            {
                Line();
                foreach (var line in details.Split('\n'))
                {
                    Line(line.Length == 0 ? string.Empty : line + "  ");
                }
            }
        }

        return text.ToString();
    }

    private static string Describe(MethodInfo method) => method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;

    /// <summary>Up to the first ". " before a capital letter (so "e.g. a meeting" doesn't end it).</summary>
    private static string FirstSentence(string text)
    {
        for (var i = text.IndexOf(". ", StringComparison.Ordinal); i >= 0; i = text.IndexOf(". ", i + 1, StringComparison.Ordinal))
        {
            if (i + 2 < text.Length && char.IsUpper(text[i + 2]))
            {
                return text[..(i + 1)];
            }
        }

        return text;
    }

    private static string Escape(string text) => text.Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    private static string Cell(string text) => Escape(text).Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    /// <summary>What the agent passes (not what the server hands the tool: the server itself, a cancellation token).</summary>
    private static bool IsArgument(Type type) => type != typeof(CancellationToken) && !typeof(McpServer).IsAssignableFrom(type) && !type.Name.StartsWith("RequestContext", StringComparison.Ordinal) && !type.Name.StartsWith("IProgress", StringComparison.Ordinal);

    private static bool IsNullableReference(ParameterInfo p) => new NullabilityInfoContext().Create(p).WriteState == NullabilityState.Nullable;

    private static string TypeName(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsArray)
        {
            return $"list of {TypeName(type.GetElementType()!)}";
        }

        return type switch
        {
            _ when type == typeof(string) => "text",
            _ when type == typeof(bool) => "true/false",
            _ when type == typeof(int) || type == typeof(long) => "integer",
            _ when type == typeof(double) || type == typeof(decimal) => "number",
            _ when type == typeof(DateTimeOffset) || type == typeof(DateTime) => "date-time (ISO 8601)",
            _ when type == typeof(Guid) => "id",
            _ => type.Name,
        };
    }

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
}
