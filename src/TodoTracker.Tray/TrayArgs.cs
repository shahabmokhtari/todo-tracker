using System.Globalization;

namespace TodoTracker.Tray;

/// <summary>
/// The command line: <c>--data &lt;dir&gt;</c>, <c>--port &lt;n&gt;</c>, <c>--smoke-test</c> (start, check, quit: CI), and a
/// todotracker:// link (the desktop starts the app with one when it's clicked).
/// </summary>
internal sealed record TrayArgs(string? DataDirectory = null, int? Port = null, bool SmokeTest = false, string? Link = null)
{
    /// <summary>The page of the app the link opens ("/" without one).</summary>
    public string AppPath => TodoTracker.Core.TaskLinks.AppPath(Link) ?? "/";

    public static TrayArgs Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        // Started for a link: only the link counts (a link's text can never add options of its own).
        if (args.FirstOrDefault(a => a.StartsWith(TodoTracker.Core.TaskLinks.Scheme + ":", StringComparison.OrdinalIgnoreCase)) is { } link)
        {
            return new TrayArgs(Link: link);
        }

        var result = new TrayArgs();
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--data" when i + 1 < args.Count:
                    result = result with { DataDirectory = args[++i] };
                    break;
                case "--port" when i + 1 < args.Count && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var port):
                    result = result with { Port = port };
                    i++;
                    break;
                case "--smoke-test":
                    result = result with { SmokeTest = true };
                    break;
            }
        }

        return result;
    }
}
