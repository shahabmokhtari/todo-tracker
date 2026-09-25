using System.Net.Http.Json;
using System.Text.Json;
using TodoTracker.Core;

namespace TodoTracker.Server;

public sealed record ServerSettings(string? TeamsWebhookUrl);

public sealed record SettingsDto(bool TeamsConfigured, string? TeamsWebhookHost);

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
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_current, JsonSerializerOptions.Web));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return _current;
        }
    }

    public SettingsDto ToDto()
    {
        var url = Current.TeamsWebhookUrl;
        return new SettingsDto(url is not null, url is null ? null : new Uri(url).Host);
    }
}

/// <summary>Posts reminder Adaptive Cards to a Teams Workflows (incoming webhook) URL, if one is configured.</summary>
public sealed class TeamsWebhookNotifier(IHttpClientFactory httpClients, SettingsStore settings, TodoTrackerServerOptions options) : IReminderNotifier
{
    public const string HttpClientName = "teams";

    public async Task NotifyAsync(ReminderNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (settings.Current.TeamsWebhookUrl is not { } url)
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
