using System.Diagnostics;
using System.Globalization;

namespace TodoTracker.Server;

/// <summary>
/// The app that started the server (the Mac app runs it in the background): when it's gone, the server stops too, so
/// it never keeps running on its own after the app quits or crashes.
/// </summary>
public static class ParentProcess
{
    /// <summary>The <c>--parent-pid</c> on the command line, if any.</summary>
    public static int? FromArguments(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] == "--parent-pid" && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                return pid;
            }
        }

        return null;
    }

    /// <summary>Calls <paramref name="onGone"/> once the process is gone; returns then, or when cancelled.</summary>
    public static async Task WatchAsync(int pid, Action onGone, TimeSpan interval, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onGone);
        try
        {
            while (IsAlive(pid))
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        onGone();
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
