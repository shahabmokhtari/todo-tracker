using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TodoTracker.Core.Sync;

namespace TodoTracker.Server.Plugins.Sync;

public sealed record SyncChoiceRequest(string? Provider);

public sealed record SyncLibraryRequest(string? Library);

public sealed record SyncDeviceRequest(string? Device);

public sealed record SyncResolveRequest(string? Key, string? Choice);

public sealed record SyncToolRequest(string? Key, string? Tool);

/// <summary>A private GitHub gist (picked by the person; signs in with the GitHub CLI or GH_TOKEN/GITHUB_TOKEN).</summary>
public sealed class GistSyncProvider(TodoTrackerServerOptions options) : ISyncProvider
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly Lock _lock = new();
    private Task<string?>? _lookup;
    private DateTime _lookedUp;

    public string Id => "gist";

    public string Name => "GitHub gist";

    public int Order => 30;

    public bool Automatic => false;

    /// <summary>Tests point this at a fake GitHub.</summary>
    internal HttpClient Client { get; init; } = Http;

    /// <summary>How a token is found (tests replace it).</summary>
    internal Func<string?> FindToken { get; init; } = DefaultToken;

    private string IdPath(string library) => Path.Combine(options.DataDirectory, "sync", "gists", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(library)))[..16] + ".json");

    public SyncAvailability Check(string vaultRoot)
    {
        if (!TokenLookup().IsCompleted)
        {
            // Asking gh can take a moment: never on the caller's thread (the sidebar menu asks from the UI thread).
            return new SyncAvailability(false, "Checking your GitHub sign-in…");
        }

        return Token() is null
            ? new SyncAvailability(false, "Sign in to GitHub first: install the GitHub CLI and run gh auth login (or set GH_TOKEN).")
            : new SyncAvailability(true, "A private gist on your GitHub account");
    }

    public ISyncRemote CreateRemote(string vaultRoot, string library) =>
        new GistRemote(
            Client,
            async ct => await TokenLookup().WaitAsync(ct).ConfigureAwait(false) ?? throw new InvalidOperationException("Sign in to GitHub first (gh auth login)."),
            () => LoadId(library),
            id =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(IdPath(library))!);
                File.WriteAllText(IdPath(library), new JsonObject { ["id"] = id }.ToJsonString());
            },
            library);

    private string? LoadId(string library)
    {
        try
        {
            return File.Exists(IdPath(library)) ? JsonNode.Parse(File.ReadAllText(IdPath(library)))?["id"]?.GetValue<string>() : null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private string? Token() => TokenLookup() is { IsCompletedSuccessfully: true } done ? done.Result : null;

    /// <summary>The token, looked up in the background and remembered for a few minutes.</summary>
    private Task<string?> TokenLookup()
    {
        lock (_lock)
        {
            var age = DateTime.UtcNow - _lookedUp;
            var stale = _lookup is { IsCompleted: true } done && age > (done.Result is null ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(10));
            if (_lookup is null || stale)
            {
                _lookedUp = DateTime.UtcNow;
                _lookup = Task.Run(FindToken);
            }

            return _lookup;
        }
    }

    private static string? DefaultToken()
    {
        if (Environment.GetEnvironmentVariable("GH_TOKEN") is { Length: > 0 } gh)
        {
            return gh;
        }

        if (Environment.GetEnvironmentVariable("GITHUB_TOKEN") is { Length: > 0 } github)
        {
            return github;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("gh", "auth token") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                process.Kill();
                return null;
            }

            return process.ExitCode == 0 && output.Result.Trim() is { Length: > 0 } token ? token : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

/// <summary>What every sync provider plugin adds: the sync service, its background loop, its endpoints and panel.</summary>
public abstract class SyncProviderPlugin : ITodoPlugin
{
    public abstract PluginInfo Info { get; }

    public string? WebModule => "/js/plugins/sync.js";

    public void ConfigureServices(IServiceCollection services, TodoTrackerServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(CloudEnvironment.Current);
        services.TryAddSingleton<SyncService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SyncLoop>());
        services.AddSingleton(sp => Provider(sp, options));
    }

    protected abstract ISyncProvider Provider(IServiceProvider services, TodoTrackerServerOptions options);
}

/// <summary>Sync through OneDrive (on by default).</summary>
public sealed class OneDriveSyncPlugin : SyncProviderPlugin
{
    public override PluginInfo Info { get; } = new("sync-onedrive", "OneDrive sync", "Syncs your tasks with your other computers through OneDrive (a work or school account first, then personal).");

    protected override ISyncProvider Provider(IServiceProvider services, TodoTrackerServerOptions options) =>
        new OneDriveSyncProvider(services.GetRequiredService<CloudEnvironment>());
}

/// <summary>Sync through iCloud Drive (on by default).</summary>
public sealed class ICloudSyncPlugin : SyncProviderPlugin
{
    public override PluginInfo Info { get; } = new("sync-icloud", "iCloud Drive sync", "Syncs your tasks with your Mac, iPhone and iPad through iCloud Drive.");

    protected override ISyncProvider Provider(IServiceProvider services, TodoTrackerServerOptions options) =>
        new ICloudSyncProvider(services.GetRequiredService<CloudEnvironment>());
}

/// <summary>Sync through a private GitHub gist (off by default).</summary>
public sealed class GistSyncPlugin : SyncProviderPlugin
{
    public override PluginInfo Info { get; } = new("sync-gist", "GitHub gist sync", "Syncs your tasks through a private gist on your GitHub account (uses the GitHub CLI's sign-in).", DefaultEnabled: false);

    protected override ISyncProvider Provider(IServiceProvider services, TodoTrackerServerOptions options) => new GistSyncProvider(options);
}

/// <summary>The Sync panel's API (only when a sync plugin is on).</summary>
public static class SyncEndpoints
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Services.GetService<SyncService>() is null)
        {
            return;
        }

        var group = app.MapGroup("/api/plugins/sync").AddEndpointFilter(ApiEndpoints.MapDomainErrors);
        group.MapGet("/", (SyncService sync) => sync.View);
        group.MapPost("/now", (SyncService sync, CancellationToken cancellationToken) => sync.SyncNowAsync(cancellationToken));
        group.MapPut("/provider", (SyncChoiceRequest request, SyncService sync) =>
        {
            sync.Choose(request.Provider ?? "auto");
            return sync.View;
        });
        group.MapPut("/library", (SyncLibraryRequest request, SyncService sync) =>
        {
            sync.SetLibrary(request.Library ?? string.Empty);
            return sync.View;
        });
        group.MapPost("/forget", (SyncDeviceRequest request, SyncService sync, CancellationToken cancellationToken) =>
            sync.ForgetAsync(request.Device ?? string.Empty, cancellationToken));
        group.MapPost("/resolve", (SyncResolveRequest request, SyncService sync, CancellationToken cancellationToken) =>
            sync.ResolveAsync(request.Key ?? string.Empty, request.Choice ?? string.Empty, cancellationToken));
        group.MapPost("/compare", async (SyncToolRequest request, SyncService sync, CancellationToken cancellationToken) =>
        {
            await sync.OpenMergeToolAsync(request.Key ?? string.Empty, request.Tool ?? string.Empty, cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        });
    }
}
