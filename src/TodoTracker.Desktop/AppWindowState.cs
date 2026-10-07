using System.Text.Json;

namespace TodoTracker.Desktop;

/// <summary>
/// The app window (the full app next to the sidebar) and the desktop's full-screen breaks. Persisted per user in
/// <c>app-window.json</c>; bounds are in physical pixels, like the sidebar's.
/// </summary>
public sealed record AppWindowState(PlacementBounds? Bounds = null, bool Maximized = false, bool FullScreenBreaks = true)
{
    public const double MinWidth = 480;
    public const double MinHeight = 360;

    internal AppWindowState Sanitized() => this with
    {
        Bounds = Bounds is { Width: >= MinWidth, Height: >= MinHeight } b && double.IsFinite(b.Left) && double.IsFinite(b.Top) ? b : null,
    };
}

/// <summary>Loads and saves <see cref="AppWindowState"/>; a missing or unreadable file means defaults.</summary>
public sealed class AppWindowStateStore(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public AppWindowState Load()
    {
        try
        {
            return File.Exists(path) && JsonSerializer.Deserialize<AppWindowState>(File.ReadAllText(path), Json) is { } state
                ? state.Sanitized()
                : new AppWindowState();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new AppWindowState();
        }
    }

    public void Save(AppWindowState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state.Sanitized(), Json));
        File.Move(temp, path, overwrite: true);
    }
}
