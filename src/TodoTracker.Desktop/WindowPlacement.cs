using System.Text.Json;
using System.Text.Json.Serialization;

namespace TodoTracker.Desktop;

public enum PlacementMode
{
    /// <summary>Reserves a screen edge (Win32 AppBar) so maximized windows never cover the sidebar.</summary>
    Docked,

    /// <summary>A normal movable, resizable window.</summary>
    Floating,
}

public enum DockEdge
{
    Left,
    Right,
}

/// <summary>Rectangle. Floating bounds and monitor work areas are physical pixels (one shared desktop coordinate space,
/// correct on mixed-DPI multi-monitor setups); the dock width is in device-independent pixels.</summary>
public sealed record PlacementBounds(double Left, double Top, double Width, double Height)
{
    [JsonIgnore]
    public double Right => Left + Width;

    [JsonIgnore]
    public double Bottom => Top + Height;

    public double IntersectionArea(PlacementBounds other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var w = Math.Min(Right, other.Right) - Math.Max(Left, other.Left);
        var h = Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top);
        return w > 0 && h > 0 ? w * h : 0;
    }
}

/// <summary>Where and how the sidebar lives. Persisted per user in <c>desktop.json</c>.</summary>
/// <param name="Monitor">Device name of the monitor to dock on (null = primary).</param>
/// <param name="Floating">Last floating window bounds (null = use a default near the chosen edge).</param>
public sealed record WindowPlacement(
    PlacementMode Mode,
    DockEdge Edge,
    string? Monitor,
    double DockWidth,
    bool AlwaysOnTop,
    PlacementBounds? Floating)
{
    public const double MinDockWidth = 280;
    public const double MaxDockWidth = 600;
    public const double DefaultDockWidth = 360;
    public const double MinFloatingSize = 200;

    public static WindowPlacement Default { get; } = new(PlacementMode.Docked, DockEdge.Right, null, DefaultDockWidth, false, null);

    /// <summary>A docked sidebar always stays on top (it owns its screen edge); a floating one follows the user's choice.</summary>
    [JsonIgnore]
    public bool IsTopmost => Mode == PlacementMode.Docked || AlwaysOnTop;

    internal WindowPlacement Sanitized() => this with
    {
        Mode = Enum.IsDefined(Mode) ? Mode : PlacementMode.Docked,
        Edge = Enum.IsDefined(Edge) ? Edge : DockEdge.Right,
        DockWidth = DockWidth <= 0 ? DefaultDockWidth : Math.Clamp(DockWidth, MinDockWidth, MaxDockWidth),
        Floating = Floating is { Width: >= MinFloatingSize, Height: >= MinFloatingSize } f
            && double.IsFinite(f.Left) && double.IsFinite(f.Top) ? f : null,
    };
}

public static class PlacementMath
{
    private const double FloatingWidth = 380;
    private const double FloatingHeight = 760;
    private const double Inset = 24;

    /// <summary>
    /// Keeps a floating window fully on screen: it stays on the monitor it mostly overlaps, or moves to the primary
    /// (first) monitor when its monitor is gone, shrinking to fit if needed.
    /// </summary>
    public static PlacementBounds EnsureVisible(PlacementBounds bounds, IReadOnlyList<PlacementBounds> workAreas)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        ArgumentNullException.ThrowIfNull(workAreas);
        if (workAreas.Count == 0)
        {
            return bounds;
        }

        var target = workAreas.MaxBy(bounds.IntersectionArea)!;
        if (target.IntersectionArea(bounds) <= 0)
        {
            target = workAreas[0];
        }

        var width = Math.Min(bounds.Width, target.Width);
        var height = Math.Min(bounds.Height, target.Height);
        var left = Math.Clamp(bounds.Left, target.Left, target.Right - width);
        var top = Math.Clamp(bounds.Top, target.Top, target.Bottom - height);
        return new PlacementBounds(left, top, width, height);
    }

    /// <summary>Default floating window near <paramref name="edge"/>; all values in the monitor's physical pixels.</summary>
    public static PlacementBounds DefaultFloating(PlacementBounds workArea, DockEdge edge, double scale = 1)
    {
        ArgumentNullException.ThrowIfNull(workArea);
        var inset = Inset * scale;
        var width = Math.Min(FloatingWidth * scale, workArea.Width - (2 * inset));
        var height = Math.Min(FloatingHeight * scale, workArea.Height - (2 * inset));
        var left = edge == DockEdge.Left ? workArea.Left + inset : workArea.Right - width - inset;
        return new PlacementBounds(left, workArea.Top + inset, width, height);
    }
}

/// <summary>Loads and saves <see cref="WindowPlacement"/>; a missing or unreadable file means defaults.</summary>
public sealed class WindowPlacementStore(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public WindowPlacement Load()
    {
        try
        {
            return File.Exists(path) && JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(path), Json) is { } placement
                ? placement.Sanitized()
                : WindowPlacement.Default;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return WindowPlacement.Default;
        }
    }

    public void Save(WindowPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(placement.Sanitized(), Json));
        File.Move(temp, path, overwrite: true);
    }
}
