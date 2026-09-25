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
