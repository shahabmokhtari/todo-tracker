using System.Text.Json;

namespace TodoTracker.Core.Vault;

/// <summary>An Obsidian vault registered on this machine.</summary>
public sealed record ObsidianVault(string Name, string Path);

/// <summary>Finds the vaults Obsidian knows about (from its <c>obsidian.json</c>), so users can pick one.</summary>
public static class ObsidianVaults
{
    public static IReadOnlyList<ObsidianVault> Discover(string? configPath = null)
    {
        var path = configPath ?? DefaultConfigPath();
        if (path is null || !File.Exists(path))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("vaults", out var vaults) || vaults.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            return vaults.EnumerateObject()
                .Select(v => v.Value.TryGetProperty("path", out var p) ? p.GetString() : null)
                .OfType<string>()
                .Where(Directory.Exists)
                .Select(p => new ObsidianVault(System.IO.Path.GetFileName(p.TrimEnd('/', '\\')), p))
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Where Todo Tracker keeps its tasks inside an Obsidian vault.</summary>
    public static string TaskFolderIn(ObsidianVault vault)
    {
        ArgumentNullException.ThrowIfNull(vault);
        return System.IO.Path.Combine(vault.Path, "Todo Tracker");
    }

    /// <summary>A link that opens the file in Obsidian (works when the file is inside a vault Obsidian knows).</summary>
    public static string OpenUrl(string fullPath) => "obsidian://open?path=" + Uri.EscapeDataString(fullPath);

    private static string? DefaultConfigPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obsidian", "obsidian.json");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return OperatingSystem.IsMacOS()
            ? System.IO.Path.Combine(home, "Library", "Application Support", "obsidian", "obsidian.json")
            : System.IO.Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? System.IO.Path.Combine(home, ".config"), "obsidian", "obsidian.json");
    }
}
