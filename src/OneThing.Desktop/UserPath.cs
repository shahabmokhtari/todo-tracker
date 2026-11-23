namespace OneThing.Desktop;

/// <summary>
/// Windows: "Add ot to your PATH" (the sidebar's menu) puts the app's <c>cli</c> folder, where <c>onething.exe</c> and
/// <c>ot.exe</c> are, at the end of the person's own PATH. Only the text; the app reads and writes it.
/// </summary>
public static class UserPath
{
    /// <summary>The PATH with <paramref name="folder"/> added at the end, everything else as it was; null when it's there already.</summary>
    public static string? WithFolder(string? path, string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (Contains(path, folder))
        {
            return null;
        }

        var current = (path ?? string.Empty).TrimEnd(';');
        return current.Length == 0 ? folder : current + ";" + folder;
    }

    /// <summary>The folder is one of the PATH's entries (case and a trailing backslash or quotes don't matter).</summary>
    public static bool Contains(string? path, string folder) =>
        (path ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(entry => string.Equals(Clean(entry), Clean(folder), StringComparison.OrdinalIgnoreCase));

    private static string Clean(string entry) => entry.Trim().Trim('"').TrimEnd('\\', '/');
}
