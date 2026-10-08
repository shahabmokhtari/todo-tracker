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

    /// <summary>Obsidian's downloads (it isn't in the Microsoft Store), and its apps for iPhone/iPad and Android.</summary>
    public const string DownloadPage = "https://obsidian.md/download";

    public const string AppStorePage = "https://apps.apple.com/app/obsidian-connected-notes/id1557175442";

    public const string PlayStorePage = "https://play.google.com/store/apps/details?id=md.obsidian";

    /// <summary>
    /// Whether Obsidian is on this computer: on Windows, something opens <c>obsidian://</c> links (its settings file
    /// stays behind after it's uninstalled); elsewhere, its settings file is there (it writes it when it first starts),
    /// including the Flatpak and Snap ones on Linux.
    /// </summary>
    public static bool IsInstalled(string? configPath = null)
    {
        if (configPath is not null)
        {
            return File.Exists(configPath);
        }

        return OperatingSystem.IsWindows() ? HasLinkHandler() : DefaultConfigPath() is { } path && File.Exists(path);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool HasLinkHandler()
    {
        using var mine = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\obsidian");
        using var everyone = mine is null ? Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("obsidian") : null;
        return mine is not null || everyone is not null;
    }

    /// <summary>The link when Obsidian is here; else its download page (never the system's "find an app" search).</summary>
    public static string LinkOrDownload(string obsidianUrl, string? configPath = null) => IsInstalled(configPath) ? obsidianUrl : DownloadPage;

    /// <summary>A link that opens the file in Obsidian (works when the file is inside a vault Obsidian knows).</summary>
    public static string OpenUrl(string fullPath) => "obsidian://open?path=" + Uri.EscapeDataString(fullPath);

    private static string? DefaultConfigPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obsidian", "obsidian.json");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            return System.IO.Path.Combine(home, "Library", "Application Support", "obsidian", "obsidian.json");
        }

        // Linux: a regular install, else the Flatpak or Snap one (each keeps its settings in its own place).
        string[] candidates =
        [
            System.IO.Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? System.IO.Path.Combine(home, ".config"), "obsidian", "obsidian.json"),
            System.IO.Path.Combine(home, ".var", "app", "md.obsidian.Obsidian", "config", "obsidian", "obsidian.json"),
            System.IO.Path.Combine(home, "snap", "obsidian", "current", ".config", "obsidian", "obsidian.json"),
        ];
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
}
