namespace TodoTracker.Desktop;

/// <summary>What placement needs to know to recognize a monitor.</summary>
/// <param name="Id">Stable identity: the monitor's device path (survives reconnects and renumbering).</param>
/// <param name="DeviceName">GDI name such as <c>\\.\DISPLAY2</c>; Windows renumbers it when monitors reconnect.</param>
/// <param name="FriendlyName">Model name from the monitor's EDID (may be empty, e.g. for some built-in panels).</param>
public record MonitorKey(string Id, string DeviceName, string? FriendlyName, bool IsPrimary, int Width, int Height)
{
    /// <summary>Device paths of other monitors cloning this one's desktop (same GDI source).</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];
}

public static class MonitorIdentity
{
    /// <summary>
    /// Finds the saved monitor among the connected ones: by stable id, or by device name for placements saved by
    /// older versions. Null when it is not connected (callers fall back to the primary monitor but keep the saved
    /// value, so the sidebar returns once the monitor is plugged back in).
    /// </summary>
    public static T? Resolve<T>(string? saved, IReadOnlyList<T> monitors)
        where T : MonitorKey
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (string.IsNullOrWhiteSpace(saved))
        {
            return null;
        }

        return monitors.FirstOrDefault(m => string.Equals(m.Id, saved, StringComparison.OrdinalIgnoreCase))
            ?? monitors.FirstOrDefault(m => m.Aliases.Any(a => string.Equals(a, saved, StringComparison.OrdinalIgnoreCase)))
            ?? monitors.FirstOrDefault(m => string.Equals(m.DeviceName, saved, StringComparison.OrdinalIgnoreCase));
    }

    public static string Label(int index, MonitorKey monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var name = string.IsNullOrWhiteSpace(monitor.FriendlyName) ? string.Empty : $" · {monitor.FriendlyName.Trim()}";
        return $"Display {index + 1}{name}{(monitor.IsPrimary ? " (primary)" : string.Empty)} · {monitor.Width}×{monitor.Height}";
    }
}
