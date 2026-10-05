using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using TodoTracker.Core;
using TodoTracker.Core.Vault;

namespace TodoTracker.Server;

/// <summary>
/// Composes the local Todo Tracker server: REST API, MCP endpoint, web UI, reminder loop, and integrations.
/// Used by the standalone web host and embedded in the Windows sidebar.
/// </summary>
public static class TodoTrackerHost
{
    public static WebApplicationBuilder CreateBuilder(TodoTrackerServerOptions options, string[]? args = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = args ?? [],
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (options.LocalOnly)
            {
                kestrel.Listen(IPAddress.Loopback, options.Port);
            }
            else
            {
                kestrel.ListenAnyIP(options.Port);
            }
        });

        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        AddServices(builder.Services, options);
        return builder;
    }

    public static void AddServices(IServiceCollection services, TodoTrackerServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(options.DataDirectory);
        services.AddSingleton(options);
        services.AddSingleton(ApiToken.LoadOrCreate(options));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(_ => InstanceLock.Acquire(options.DataDirectory));
        services.TryAddSingleton(sp => VaultBoardStore.Open(new VaultOptions(options.ResolveVaultPath(sp.GetRequiredService<SettingsStore>().Current.VaultPath))
        {
            TimeZone = options.TimeZone,
            Time = sp.GetRequiredService<TimeProvider>(),
            LegacyBoardPath = Path.Combine(options.DataDirectory, "board.json"),
            Watch = options.WatchVault,
        }));
        services.TryAddSingleton<IBoardStore>(sp => sp.GetRequiredService<VaultBoardStore>());
        services.AddSingleton<VaultLinks>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<LaunchCodes>();
        services.AddSingleton<ServerEvents>();
        services.AddSingleton<IReminderNotifier, EventNotifier>();
        services.AddSingleton<IReminderNotifier, TeamsWebhookNotifier>();
        services.AddHttpClient(TeamsWebhookNotifier.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<ReminderLoop>();
        services.AddHostedService(sp => sp.GetRequiredService<ReminderLoop>());
        services.AddProblemDetails();
        services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        });
        services.AddMcpServer(o => o.ServerInfo = new() { Name = "todo-tracker", Version = typeof(TodoTrackerHost).Assembly.GetName().Version?.ToString() ?? "1.0" })
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateful)
            .WithTools<TodoTools>();
    }

    public static WebApplication Build(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var app = builder.Build();
        Configure(app);
        return app;
    }

    public static void Configure(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = app.Services.GetRequiredService<TodoTrackerServerOptions>();
        var token = app.Services.GetRequiredService<ApiToken>();

        // Fail fast if another instance owns the data folder, or the vault can't be opened.
        app.Services.GetRequiredService<InstanceLock>();
        app.Services.GetRequiredService<IBoardStore>();

        app.Use((context, next) => Security.Guard(context, next, options, token));

        var files = new ManifestEmbeddedFileProvider(typeof(TodoTrackerHost).Assembly, "wwwroot");
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });

        ApiEndpoints.Map(app);
        app.MapMcp("/mcp");
    }

    /// <summary>Connection details (launch link, MCP URL/config) for the running server.</summary>
    public static ConnectionDto GetConnection(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return ApiEndpoints.Connection(services.GetRequiredService<ApiToken>(), services.GetRequiredService<TodoTrackerServerOptions>());
    }

    /// <summary>A single-use browser link that signs in and opens <paramref name="returnPath"/> (default: dashboard).</summary>
    public static string CreateLaunchUrl(IServiceProvider services, string returnPath = "/", TimeSpan? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        return ApiEndpoints.LaunchUrl(services.GetRequiredService<LaunchCodes>(), services.GetRequiredService<TodoTrackerServerOptions>(), returnPath, lifetime);
    }

    /// <summary>Embedded web UI assets, exposed for hosts that want to check they are present.</summary>
    public static IFileProvider WebAssets => new ManifestEmbeddedFileProvider(typeof(TodoTrackerHost).Assembly, "wwwroot");
}


