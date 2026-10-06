using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TodoTracker.Core;

namespace TodoTracker.Server.Plugins;

public sealed record PluginInfo(string Id, string Name, string Description, bool DefaultEnabled = true);

/// <summary>What the app shows about a plugin (and the web module that adds its UI, when enabled).</summary>
public sealed record PluginDto(string Id, string Name, string Description, bool Enabled, string? WebModule);

public sealed record PluginToggle(bool Enabled);

/// <summary>
/// A feature that can be switched on or off: it adds services, endpoints under <c>/api/plugins/{id}</c>, and
/// optionally a web module (a script in the embedded web UI). Plugins are built in and reviewed with the app; code
/// from elsewhere is never loaded. Switching one on or off applies after a restart.
/// </summary>
public interface ITodoPlugin
{
    PluginInfo Info { get; }

    /// <summary>The web UI module, relative to the site root (e.g. <c>/js/plugins/agent-chat.js</c>).</summary>
    string? WebModule => null;

    void ConfigureServices(IServiceCollection services, TodoTrackerServerOptions options);

    void MapEndpoints(RouteGroupBuilder group)
    {
    }
}

/// <summary>Which plugins are on (<c>plugins.json</c> in the data folder).</summary>
public sealed class PluginSettings
{
    private readonly string _path;
    private readonly Lock _lock = new();

    public PluginSettings(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "plugins.json");
    }

    public bool IsEnabled(PluginInfo plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        lock (_lock)
        {
            return Read()[plugin.Id] is JsonValue v && v.TryGetValue<bool>(out var on) ? on : plugin.DefaultEnabled;
        }
    }

    public void SetEnabled(string id, bool enabled)
    {
        lock (_lock)
        {
            var all = Read();
            all[id] = enabled;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, all.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private JsonObject Read()
    {
        try
        {
            return File.Exists(_path) && JsonNode.Parse(File.ReadAllText(_path)) is JsonObject o ? o : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>The built-in plugins and which are running (decided at startup).</summary>
public sealed class PluginHost
{
    private PluginHost(IReadOnlyList<(ITodoPlugin Plugin, bool Enabled)> plugins, PluginSettings settings)
    {
        Plugins = plugins;
        Settings = settings;
    }

    public static IReadOnlyList<ITodoPlugin> BuiltIn { get; } = [new TeamsPlugin(), new AgentChat.AgentChatPlugin()];

    public IReadOnlyList<(ITodoPlugin Plugin, bool Enabled)> Plugins { get; }

    public PluginSettings Settings { get; }

    public bool IsRunning(string id) => Plugins.Any(p => p.Plugin.Info.Id == id && p.Enabled);

    public static void AddServices(IServiceCollection services, TodoTrackerServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = new PluginSettings(options.DataDirectory);
        var plugins = BuiltIn.Select(p => (p, settings.IsEnabled(p.Info))).ToList();
        foreach (var (plugin, enabled) in plugins)
        {
            if (enabled)
            {
                plugin.ConfigureServices(services, options);
            }
        }

        services.AddSingleton(new PluginHost(plugins, settings));
    }

    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var host = app.Services.GetRequiredService<PluginHost>();
        app.MapGet("/api/plugins", () => host.Plugins.Select(p => new PluginDto(p.Plugin.Info.Id, p.Plugin.Info.Name, p.Plugin.Info.Description, p.Enabled, p.Enabled ? p.Plugin.WebModule : null)));
        app.MapPut("/api/plugins/{id}", (string id, PluginToggle request) =>
        {
            if (!host.Plugins.Any(p => p.Plugin.Info.Id == id))
            {
                return Results.Problem($"There is no plugin \"{id}\".", statusCode: StatusCodes.Status404NotFound);
            }

            host.Settings.SetEnabled(id, request.Enabled);
            return Results.Ok(new { id, request.Enabled, restartRequired = host.IsRunning(id) != request.Enabled });
        });

        foreach (var (plugin, enabled) in host.Plugins)
        {
            if (enabled)
            {
                plugin.MapEndpoints(app.MapGroup($"/api/plugins/{plugin.Info.Id}").AddEndpointFilter(ApiEndpoints.MapDomainErrors));
            }
        }
    }
}

/// <summary>Reminder cards in a Teams channel through a Workflows webhook.</summary>
public sealed class TeamsPlugin : ITodoPlugin
{
    public PluginInfo Info { get; } = new("teams", "Teams reminders", "Posts reminder cards to a Teams channel through a Workflows webhook.");

    public void ConfigureServices(IServiceCollection services, TodoTrackerServerOptions options)
    {
        services.AddSingleton<IReminderNotifier, TeamsWebhookNotifier>();
        services.AddHttpClient(TeamsWebhookNotifier.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
    }
}
