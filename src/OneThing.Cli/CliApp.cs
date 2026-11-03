using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using OneThing.Core;
using OneThing.Core.Vault;
using OneThing.Server;

namespace OneThing.Cli;

/// <summary>
/// <c>ot</c>: One Thing from a terminal, scripts and AI agents. It works directly on the tasks folder (the same one
/// the app uses, safely alongside it), so it needs no running app. <c>--json</c> prints the same shapes as the REST API
/// and MCP tools. Exit codes: 0 ok, 1 the change couldn't be made (message on stderr), 2 wrong usage.
/// </summary>
public static class CliApp
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Whether Ctrl+C should stop the command cleanly instead of ending the process: the MCP server, however it was
    /// started (<c>onething mcp</c>, <c>onething --json mcp</c>, <c>onething MCP</c>).
    /// </summary>
    public static bool StopsOnCtrlC(IReadOnlyList<string> args)
    {
        try
        {
            return CliArgs.Parse(args).Command == "mcp";
        }
        catch (CliUsageException)
        {
            return false;
        }
    }

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CliContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        CliArgs parsed;
        try
        {
            parsed = CliArgs.Parse(args);
        }
        catch (CliUsageException ex)
        {
            await context.Error.WriteLineAsync($"ot: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        if (parsed.Has("version"))
        {
            await context.Out.WriteLineAsync(McpInfo.Version).ConfigureAwait(false);
            return 0;
        }

        if (parsed.Command == "help" || parsed.Help)
        {
            var topic = parsed.Command == "help" ? (parsed.Words.Count > 0 ? parsed.Words[0].ToLowerInvariant() : null) : parsed.Command;
            if (topic is not null && !CliCommands.All.ContainsKey(topic))
            {
                await context.Error.WriteLineAsync($"ot: there is no command \"{topic}\".").ConfigureAwait(false);
                return 2;
            }

            await context.Out.WriteAsync(CliCommands.Help(topic)).ConfigureAwait(false);
            return 0;
        }

        if (!CliCommands.All.TryGetValue(parsed.Command, out var command))
        {
            await context.Error.WriteLineAsync($"ot: there is no command \"{parsed.Command}\". See ot help.").ConfigureAwait(false);
            return 2;
        }

        try
        {
            if (parsed.Command == "mcp")
            {
                parsed.Allow();
                await RunMcpAsync(parsed, context, cancellationToken).ConfigureAwait(false);
                return 0;
            }

            using var session = CliSession.Open(parsed, context);
            await command.Run(session, parsed).ConfigureAwait(false);
            return 0;
        }
        catch (CliUsageException ex)
        {
            await context.Error.WriteLineAsync($"ot: {ex.Message}").ConfigureAwait(false);
            return 2;
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or IOException or TimeoutException)
        {
            await context.Error.WriteLineAsync($"ot: {ErrorText.Friendly(ex)}").ConfigureAwait(false);
            return 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped with Ctrl+C: a clean stop.
            return 0;
        }
#pragma warning disable CA1031 // Last stop: say what went wrong (exit 1) instead of a crash with a stack trace.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await context.Error.WriteLineAsync($"ot: something went wrong: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    internal static OneThingServerOptions Options(CliArgs args, CliContext context, bool watch)
    {
        // A mistyped --vault must not quietly start an empty task list somewhere else.
        if (args.Value("vault") is { } vault && !Directory.Exists(Path.GetFullPath(vault)))
        {
            throw new ArgumentException($"There's no folder {Path.GetFullPath(vault)}. Check the path (or create the folder first).");
        }

        return new()
        {
            DataDirectory = args.Value("data") ?? context.DataDirectory ?? OneThingServerOptions.DefaultDataDirectory(),
            VaultPath = args.Value("vault") ?? context.VaultPath,
            TimeZone = context.TimeZone,
            WatchVault = watch,
            EnableHistory = context.History && !args.Has("no-history")
                && Server.Plugins.PluginHost.IsEnabled(args.Value("data") ?? context.DataDirectory ?? OneThingServerOptions.DefaultDataDirectory(), Server.Plugins.HistoryPlugin.Definition.Id),
            LockDirectory = context.LockDirectory,
            Git = context.Git,
        };
    }

    private static async Task RunMcpAsync(CliArgs args, CliContext context, CancellationToken cancellationToken)
    {
        var builder = StdioMcpHost.CreateBuilder(Options(args, context, watch: true));
        using var host = builder.Build();
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}
