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
        AddVaultServices(services, options);
        services.AddSingleton(ApiToken.LoadOrCreate(options));
        services.AddSingleton(_ => InstanceLock.Acquire(options.DataDirectory));
        services.AddSingleton<LaunchCodes>();
        services.AddSingleton<ServerEvents>();
        services.AddSingleton<IReminderNotifier, EventNotifier>();
        Plugins.PluginHost.AddServices(services, options);
        services.AddSingleton<ReminderLoop>();
        services.AddHostedService(sp => sp.GetRequiredService<ReminderLoop>());
        services.AddProblemDetails();
        services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        });
        OpenApiSetup.AddServices(services);
        services.AddMcpServer(McpInfo.Configure)
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateful)
            .WithTools<TodoTools>();
    }

    /// <summary>
    /// The task store and what the tools need, shared by the web host, <c>tt mcp</c> and the <c>tt</c> CLI. Several of
    /// them can use the same vault at once (changes run under a per-vault lock).
    /// </summary>
    public static void AddVaultServices(IServiceCollection services, TodoTrackerServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(options.DataDirectory);
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SettingsStore>();
        services.TryAddSingleton(sp => OpenVault(options, sp.GetRequiredService<SettingsStore>(), sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IBoardStore>(sp => sp.GetRequiredService<VaultBoardStore>());
        services.AddSingleton<VaultLinks>();
        services.AddSingleton<HistoryService>();
    }

    /// <summary>Opens the vault the app uses (see <see cref="TodoTrackerServerOptions.ResolveVaultPath"/>).</summary>
    public static VaultBoardStore OpenVault(TodoTrackerServerOptions options, SettingsStore settings, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);
        return VaultBoardStore.Open(new VaultOptions(options.ResolveVaultPath(settings.Current.VaultPath))
        {
            TimeZone = options.TimeZone,
            Time = time ?? TimeProvider.System,

            // An old board.json moves into the app's own folder only, never into a folder named for one command.
            LegacyBoardPath = options.IsVaultOverridden ? null : Path.Combine(options.DataDirectory, "board.json"),
            Watch = options.WatchVault,
            LockDirectory = options.LockDirectory,
        });
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
        OpenApiSetup.Map(app);
        Plugins.PluginHost.Map(app);
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


