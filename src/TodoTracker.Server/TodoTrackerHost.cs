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
        services.AddSingleton<AppSessions>();
        services.AddSingleton(OwnerKey.LoadOrCreate(options));
        services.AddSingleton<ServerEvents>();
        services.AddSingleton<IReminderNotifier, EventNotifier>();
        Plugins.PluginHost.AddServices(services, options);
        services.AddSingleton<ReminderLoop>();
        services.AddHostedService(sp => sp.GetRequiredService<ReminderLoop>());
        services.AddSingleton<Hooks.HookService>();
        services.AddHostedService(sp => sp.GetRequiredService<Hooks.HookService>());
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
        services.AddSingleton(new Hooks.HookStore(options.DataDirectory));
    }

    /// <summary>
    /// The folder the tasks are kept in: the tasks folder (<see cref="TodoTrackerServerOptions.ResolveVaultPath"/>), or,
    /// with the tasks on GitHub, the app's own copy in its data folder (a folder named for one run still wins).
    /// </summary>
    public static string VaultPathOf(TodoTrackerServerOptions options, SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);
        return settings.TasksOnGitHub && !options.IsVaultOverridden ? GitHubCopyOf(options) : options.ResolveVaultPath(settings.Current.VaultPath);
    }

    /// <summary>Where the app keeps its copy of the tasks when they live on GitHub.</summary>
    public static string GitHubCopyOf(TodoTrackerServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Path.Combine(options.DataDirectory, "github-tasks");
    }

    /// <summary>
    /// Where the tasks live changed (Settings › Connected apps › GitHub): done at start, so nothing changed in between is
    /// left behind. To GitHub: the tasks folder is copied into the app's own folder (once; the tasks folder stays as it
    /// is). Back: the app's copy goes into the tasks folder; what the folder had, and the old copy, are kept renamed.
    /// </summary>
    public static void PrepareStorage(TodoTrackerServerOptions options, SettingsStore settings, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(time);
        if (options.IsVaultOverridden)
        {
            return;
        }

        var copy = GitHubCopyOf(options);
        var folder = options.ResolveVaultPath(settings.Current.VaultPath);
        var stamp = time.GetUtcNow().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        if (settings.TasksOnGitHub && !HasFiles(copy))
        {
            CopyFolder(folder, copy);
        }
        else if (!settings.TasksOnGitHub && settings.Current.LeavingGitHub)
        {
            if (HasFiles(copy))
            {
                if (HasFiles(folder))
                {
                    Directory.Move(folder, $"{folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)}.before-github-{stamp}");
                }

                CopyFolder(copy, folder);
                Directory.Move(copy, $"{copy}.returned-{stamp}");
            }

            settings.LeftGitHub();
        }
    }

    private static bool HasFiles(string folder) => Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any();

    /// <summary>Copies a tasks folder (its version history, in .git, stays behind: it's per computer).</summary>
    internal static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        if (!Directory.Exists(from))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(from, file);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0] is ".git")
            {
                continue;
            }

            var target = Path.Combine(to, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>Opens the vault the app uses (see <see cref="VaultPathOf"/>).</summary>
    public static VaultBoardStore OpenVault(TodoTrackerServerOptions options, SettingsStore settings, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);
        PrepareStorage(options, settings, time ?? TimeProvider.System);
        return VaultBoardStore.Open(new VaultOptions(VaultPathOf(options, settings))
        {
            TimeZone = options.TimeZone,
            Time = time ?? TimeProvider.System,

            // An old board.json moves into the app's own folder only, never into a folder named for one command.
            LegacyBoardPath = options.IsVaultOverridden ? null : Path.Combine(options.DataDirectory, "board.json"),
            ImportLegacyIntoExisting = options.ImportLegacyIntoExisting,
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
        Hooks.HookEndpoints.Map(app);
        PlanEndpoints.Map(app);
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
        // Made in this process (the sidebar, the tray, the console): the session it opens is the owner's.
        return ApiEndpoints.LaunchUrl(services.GetRequiredService<LaunchCodes>(), services.GetRequiredService<TodoTrackerServerOptions>(), returnPath, lifetime, owner: true);
    }

    /// <summary>Embedded web UI assets, exposed for hosts that want to check they are present.</summary>
    public static IFileProvider WebAssets => new ManifestEmbeddedFileProvider(typeof(TodoTrackerHost).Assembly, "wwwroot");
}


