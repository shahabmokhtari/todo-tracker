using System.Globalization;
using OneThing.Core;

namespace OneThing.Desktop;

/// <summary>
/// How the sidebar was started: a test run on another folder or port, a theme for this run, a deep link…
/// <see cref="Error"/> says what was wrong with the command line, in words for the person who typed it.
/// </summary>
public sealed record StartupArgs(bool SmokeTest, string? SmokeLog, string? DataDirectory, int? Port, bool NoDock, string? Screenshot = null, string? Theme = null, bool Restart = false, string? Link = null, string? Error = null)
{
    public static StartupArgs Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        // Started for a link: only the link counts. Windows pastes the raw link into the command line, so a link
        // with a quote in it could otherwise add options of its own (--data, --screenshot…).
        if (args.FirstOrDefault(a => a.StartsWith(TaskLinks.Scheme + ":", StringComparison.OrdinalIgnoreCase)) is { } link)
        {
            return new StartupArgs(false, null, null, null, false, Link: link);
        }

        var result = new StartupArgs(false, null, null, null, false);
        for (var i = 0; i < args.Count; i++)
        {
            result = args[i] switch
            {
                "--smoke-test" => result with { SmokeTest = true },
                "--smoke-log" when i + 1 < args.Count => result with { SmokeLog = args[++i] },
                "--data" when i + 1 < args.Count => result with { DataDirectory = args[++i] },
                "--port" when i + 1 < args.Count => ParsePort(args[++i]) is { } port
                    ? result with { Port = port }
                    : result with { Error = $"The port must be a number from 1 to 65535, not \"{args[i]}\"." },
                "--no-dock" => result with { NoDock = true },
                "--restart" => result with { Restart = true },
                "--screenshot" when i + 1 < args.Count => result with { Screenshot = args[++i] },
                "--theme" when i + 1 < args.Count => result with { Theme = args[++i] },
                _ => result,
            };
        }

        return result;
    }

    /// <summary>
    /// What a restart (switching the tasks folder, a plugin) starts the new sidebar with: the same data folder, port,
    /// docking and theme as this run, so a test run never restarts on the user's own tasks.
    /// </summary>
    public IReadOnlyList<string> RestartArguments()
    {
        var args = new List<string> { "--restart" };
        if (DataDirectory is not null)
        {
            args.AddRange(["--data", DataDirectory]);
        }

        if (Port is { } port)
        {
            args.AddRange(["--port", port.ToString(CultureInfo.InvariantCulture)]);
        }

        if (NoDock)
        {
            args.Add("--no-dock");
        }

        if (Theme is not null)
        {
            args.AddRange(["--theme", Theme]);
        }

        return args;
    }

    private static int? ParsePort(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535 ? port : null;
}
