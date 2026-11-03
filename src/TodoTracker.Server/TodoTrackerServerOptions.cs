namespace TodoTracker.Server;

public sealed class TodoTrackerServerOptions
{
    public const int DefaultPort = 5317;

    /// <summary>Folder holding <c>board.json</c>, <c>settings.json</c>, and <c>api-token</c>.</summary>
    public string DataDirectory { get; set; } = DefaultDataDirectory();

    public int Port { get; set; } = DefaultPort;

    /// <summary>Bearer/cookie secret. When null, a random token is generated once and stored in the data directory.</summary>
    public string? ApiToken { get; set; }

    /// <summary>Bind to loopback only and reject non-loopback Host headers (DNS-rebinding protection).</summary>
    public bool LocalOnly { get; set; } = true;

    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(15);

    public bool EnableBackgroundLoop { get; set; } = true;

    /// <summary>Time zone used for quick-capture words like <c>@tomorrow</c>.</summary>
    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Local;

    /// <summary>Base URL used in links (Teams cards, MCP config). Defaults to <c>http://127.0.0.1:{Port}</c>.</summary>
    public string? PublicBaseUrl { get; set; }

    public string BaseUrl => (PublicBaseUrl ?? $"http://127.0.0.1:{Port}").TrimEnd('/');

    /// <summary>
    /// Markdown vault folder. When null: <c>TODOTRACKER_VAULT</c>, then the folder chosen in the app, then
    /// <c>Documents/Todo Tracker</c> (or <c>&lt;data&gt;/vault</c> when a custom data folder is used, e.g. tests).
    /// </summary>
    public string? VaultPath { get; set; }

    /// <summary>The <c>tt</c> command shown in "Connect an AI app" (default: the one next to the app, else <c>tt</c>).</summary>
    public string? TtPath { get; set; }

    /// <summary>Watch the vault for edits made outside the app (Obsidian, editors, agents).</summary>
    public bool WatchVault { get; set; } = true;

    /// <summary>Keep version history of the vault in a private git repository (needs git on the PATH).</summary>
    public bool EnableHistory { get; set; } = true;

    /// <summary>The Mac app's own board.json joins the vault even when the vault already has tasks (see VaultOptions).</summary>
    public bool ImportLegacyIntoExisting { get; set; }

    /// <summary>Per-vault locks and version history (default: local app data). Tests point it at a temp folder.</summary>
    public string? LockDirectory { get; set; }

    /// <summary>The git used for version history.</summary>
    public string Git { get; set; } = "git";

    /// <summary>Whether the vault was named explicitly (<see cref="VaultPath"/> or <c>TODOTRACKER_VAULT</c>) rather than the app's own.</summary>
    public bool IsVaultOverridden => Given(VaultPath) is not null || Given(Environment.GetEnvironmentVariable("TODOTRACKER_VAULT")) is not null;

    public string ResolveVaultPath(string? chosenInApp)
    {
        if (Given(VaultPath) is { } explicitPath)
        {
            return Path.GetFullPath(explicitPath);
        }

        if (Given(Environment.GetEnvironmentVariable("TODOTRACKER_VAULT")) is { } fromEnvironment)
        {
            return Path.GetFullPath(fromEnvironment);
        }

        if (Given(chosenInApp) is { } chosen)
        {
            return Path.GetFullPath(chosen);
        }

        var usesDefaultData = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TODOTRACKER_DATA"))
            && string.Equals(Path.GetFullPath(DataDirectory), Path.GetFullPath(DefaultDataDirectory()), StringComparison.OrdinalIgnoreCase);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return usesDefaultData && !string.IsNullOrEmpty(documents)
            ? Path.Combine(documents, "Todo Tracker")
            : Path.Combine(DataDirectory, "vault");
    }

    /// <summary>
    /// A value someone actually set: not blank, and not a placeholder an app left unexpanded (Claude Desktop passes
    /// <c>${user_config.x}</c> through literally when an optional setting is empty).
    /// </summary>
    private static string? Given(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().StartsWith("${", StringComparison.Ordinal) ? null : value.Trim();

    public static string DefaultDataDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable("TODOTRACKER_DATA");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        return Path.Combine(string.IsNullOrEmpty(root) ? Path.GetTempPath() : root, "TodoTracker");
    }
}
