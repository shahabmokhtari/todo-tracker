using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace TodoTracker.Server;

/// <summary>Name, version, and the guidance agents get when they connect (same for HTTP and stdio).</summary>
public static class McpInfo
{
    public const string Instructions =
        "Todo Tracker is the user's ADHD-friendly task list. Keep them focused: one clear next action beats a long list.\n" +
        "- Start with get_dashboard (what to do now, what's waiting). Find tasks with search_tasks (#tag, label:x, group:work, is:done).\n" +
        "- Capture work as tasks with create_task; break big work into subtasks (parentId) or ordered steps (add_steps).\n" +
        "- Log progress with add_note (\"did X, next Y\"); defer with schedule_next_action instead of leaving stale tasks in Now.\n" +
        "- Groups are tabs such as Work and Personal. Tags are free-form; labels are a curated colored set (list_labels).\n" +
        "- Never delete: complete_task or move_task instead. Every change is versioned (task_history, restore_task_version).\n" +
        "- Top-level tasks are cards on a board (inbox, next, doing; move_card). Time work with start_timer/stop_timer or log_time; get_time_report says where the time went. Finished tasks can be put away with archive_task.\n" +
        "- Tasks are markdown files in a folder the user may also open in Obsidian; vault_info explains the format.";

    public static string Version => typeof(McpInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public static void Configure(McpServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ServerInfo = new() { Name = "todo-tracker", Title = "Todo Tracker", Version = Version };
        options.ServerInstructions = Instructions;
    }
}

/// <summary>
/// The MCP server over stdio (<c>tt mcp</c>), for AI apps that launch local servers: Claude Desktop, Claude Code,
/// Copilot CLI, VS Code. It works on the same vault as the app, whether or not the app is running.
/// </summary>
public static class StdioMcpHost
{
    public static HostApplicationBuilder CreateBuilder(TodoTrackerServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });

        // stdout carries the protocol: logs go to stderr only.
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // The same switches as the app: no versions when version history is turned off.
        if (!Plugins.PluginHost.IsEnabled(options.DataDirectory, Plugins.HistoryPlugin.Definition.Id))
        {
            options.EnableHistory = false;
        }

        TodoTrackerHost.AddVaultServices(builder.Services, options);
        builder.Services.AddHostedService<HistoryLoop>();
        builder.Services.AddMcpServer(McpInfo.Configure)
            .WithStdioServerTransport()
            .WithTools<TodoTools>();
        return builder;
    }
}
