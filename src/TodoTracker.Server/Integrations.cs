using System.Net.Http.Json;
using System.Text.Json;
using TodoTracker.Core;

namespace TodoTracker.Server;

/// <param name="Storage">Where the tasks live: null, the tasks folder; <see cref="SettingsStore.GitHubStorage"/>, GitHub
/// (no tasks folder: the app keeps its copy in its own data folder).</param>
/// <param name="ActiveStorage">Where they live now: <see cref="Storage"/> takes effect when the app next starts (only the
/// app moves them; <c>tt</c> and <c>tt mcp</c> keep using this one until then).</param>
public sealed record ServerSettings(string? TeamsWebhookUrl, string? VaultPath = null, string? Theme = null, IReadOnlyDictionary<string, PlanSettings>? Plan = null, string? Storage = null, string? ActiveStorage = null);

public sealed record SettingsDto(bool TeamsConfigured, string? TeamsWebhookHost, string Theme);

public sealed record ThemeRequest(string? Theme);

public sealed record VaultSettingRequest(string? Path);

public sealed record VaultSettingDto(string Path, bool RestartRequired);

/// <summary>Small JSON settings file in the data directory (kept separate from the board so it is never exported).</summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly Lock _lock = new();
    private ServerSettings _current;

    public SettingsStore(TodoTrackerServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _path = Path.Combine(options.DataDirectory, "settings.json");
        _current = File.Exists(_path) ? JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(_path), JsonSerializerOptions.Web) ?? new(null) : new(null);
    }

    public ServerSettings Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    public ServerSettings SetTeamsWebhook(string? url)
    {
        var clean = string.IsNullOrWhiteSpace(url) ? null : url.Trim();
        if (clean is not null && (!Uri.TryCreate(clean, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("The Teams webhook must be an https URL (Teams > Workflows > \"Post to a channel when a webhook request is received\").", nameof(url));
        }

        lock (_lock)
        {
            _current = _current with { TeamsWebhookUrl = clean };
            Save();
            return _current;
        }
    }

    /// <summary>Light, dark, or the system's (every window follows it: the app, the browser and the sidebar).</summary>
    public static readonly IReadOnlyList<string> Themes = ["system", "light", "dark"];

    /// <summary>Raised with the new theme after it changes (the desktop sidebar follows it).</summary>
    public event EventHandler<string>? ThemeChanged;

    public ServerSettings SetTheme(string? theme)
    {
        var clean = theme?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!Themes.Contains(clean))
        {
            throw new ArgumentException("The theme is system, light or dark.", nameof(theme));
        }

        ServerSettings saved;
        lock (_lock)
        {
            _current = _current with { Theme = clean };
            Save();
            saved = _current;
        }

        ThemeChanged?.Invoke(this, clean);
        return saved;
    }

    /// <summary>Remembers the vault folder chosen in the app (used from the next start).</summary>
    public ServerSettings SetVaultPath(string? path)
    {
        var clean = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        if (clean is not null && !System.IO.Path.IsPathFullyQualified(clean))
        {
            throw new ArgumentException("Choose a full folder path for the vault.", nameof(path));
        }

        lock (_lock)
        {
            _current = _current with { VaultPath = clean is null ? null : System.IO.Path.GetFullPath(clean) };
            Save();
            return _current;
        }
    }

    public const string GitHubStorage = "github";

    /// <summary>The tasks are to live on GitHub (no tasks folder): from the next start of the app.</summary>
    public bool TasksOnGitHub => Current.Storage == GitHubStorage;

    /// <summary>The tasks live on GitHub now (the app's copy in its data folder is the one in use).</summary>
    public bool UsingGitHub => Current.ActiveStorage == GitHubStorage;

    /// <summary>The tasks were moved (by the app, at its start).</summary>
    public void SetActiveStorage(string? storage)
    {
        lock (_lock)
        {
            _current = _current with { ActiveStorage = storage };
            Save();
        }
    }

    /// <summary>Where the tasks live from the next start: the tasks folder (null) or GitHub.</summary>
    public ServerSettings SetStorage(string? storage)
    {
        if (storage is not null && storage != GitHubStorage)
        {
            throw new ArgumentException("Tasks live in the tasks folder or on GitHub.", nameof(storage));
        }

        lock (_lock)
        {
            _current = _current with { Storage = storage };
            Save();
            return _current;
        }
    }

    /// <summary>The Plan view's settings for a group (<c>all</c>: every group together); unset: the defaults.</summary>
    public PlanSettings PlanFor(string key) => Current.Plan?.GetValueOrDefault(key) ?? new PlanSettings();

    public PlanSettings SetPlan(string key, PlanSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var clean = settings.Validated();
        lock (_lock)
        {
            var all = new Dictionary<string, PlanSettings>(_current.Plan ?? new Dictionary<string, PlanSettings>(), StringComparer.Ordinal) { [key] = clean };
            _current = _current with { Plan = all };
            Save();
        }

        return clean;
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_current, JsonSerializerOptions.Web));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public SettingsDto ToDto()
    {
        var url = Current.TeamsWebhookUrl;
        return new SettingsDto(url is not null, url is null ? null : new Uri(url).Host, Current.Theme ?? "system");
    }
}

/// <summary>Posts reminder Adaptive Cards to a Teams Workflows (incoming webhook) URL, if one is configured.</summary>
public sealed class TeamsWebhookNotifier(IHttpClientFactory httpClients, SettingsStore settings, TodoTrackerServerOptions options, Plugins.PluginHost? plugins = null) : IReminderNotifier
{
    public const string HttpClientName = "teams";

    public async Task NotifyAsync(ReminderNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        // Switched off since the app started: no more cards.
        if (settings.Current.TeamsWebhookUrl is not { } url || plugins?.IsSwitchedOff("teams") == true)
        {
            return;
        }

        using var client = httpClients.CreateClient(HttpClientName);
        using var response = await client.PostAsJsonAsync(url, BuildCard(notification, options.BaseUrl), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    internal static object BuildCard(ReminderNotification n, string baseUrl)
    {
        var body = new List<object>
        {
            new { type = "TextBlock", text = $"⏰ {n.Title}", weight = "Bolder", size = "Medium", wrap = true },
        };
        if (n.Breadcrumb.Count > 0)
        {
            body.Add(new { type = "TextBlock", text = string.Join(" › ", n.Breadcrumb), isSubtle = true, spacing = "None", wrap = true });
        }

        body.Add(new { type = "TextBlock", text = n.Message, wrap = true });
        body.Add(new
        {
            type = "FactSet",
            facts = new[]
            {
                new { title = "Priority", value = n.Priority.ToString() },
                new { title = "Due", value = n.DueAt.ToString("u", System.Globalization.CultureInfo.InvariantCulture) },
            },
        });

        return new
        {
            type = "message",
            attachments = new[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    content = new Dictionary<string, object>
                    {
                        ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                        ["type"] = "AdaptiveCard",
                        ["version"] = "1.4",
                        ["body"] = body,
                        ["actions"] = new[] { new { type = "Action.OpenUrl", title = "Open report", url = $"{baseUrl}/report.html?id={n.ItemId}" } },
                    },
                },
            },
        };
    }
}
