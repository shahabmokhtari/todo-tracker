using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OneThing.Core;
using OneThing.Server;
using OneThing.Server.GitHub;
using OneThing.Server.Plugins;
using OneThing.Server.Plugins.Connectors;
using OneThing.Server.Plugins.Sync;

namespace OneThing.Cli;

/// <summary>
/// Sync and the connectors, run here the way the app runs them (the same services, on this tasks folder): their
/// status, and "sync now". Setting them up (places, sign-ins, what to connect) is the person's, in the app. While the
/// app runs it syncs by itself, and ot leaves that to it (two syncs of one folder at once would trip over each other).
/// </summary>
internal static partial class CliCommands
{
    private static async Task Sync(CliSession s, CliArgs a)
    {
        a.Allow();
        var verb = a.Words.Count > 0 ? a.Words[0].ToLowerInvariant() : null;
        if (verb is not (null or "now"))
        {
            throw new CliUsageException("ot sync shows how syncing is going; ot sync now syncs. Setting it up is in the app (Settings › Sync).");
        }

        using var services = PluginServices(s);
        var sync = services.GetService<SyncService>()
            ?? throw new InvalidOperationException("No sync extra is on (ot plugins lists them; switch one on in Settings › Extras).");
        SyncView view;
        if (verb == "now")
        {
            using var app = AppNotRunning(s, "syncs by itself every few minutes (or Sync now in Settings › Sync)");
            view = await sync.SyncNowAsync().ConfigureAwait(false);
        }
        else
        {
            view = sync.View;
        }

        await s.Print(view, (o, v) => o.Sync(v)).ConfigureAwait(false);
    }

    private static async Task Connectors(CliSession s, CliArgs a)
    {
        a.Allow();
        var verb = a.Words.Count > 0 ? a.Words[0].ToLowerInvariant() : null;
        using var services = PluginServices(s);
        var connectors = services.GetService<ConnectorService>();
        var github = services.GetService<GitHubIssues>();
        switch (verb)
        {
            case null:
                // GitHub only when it's on: its status asks the GitHub CLI who's signed in.
                var view = new ConnectorsDto(
                    connectors?.View ?? [],
                    github is not null && github.On ? await github.ViewAsync(CancellationToken.None).ConfigureAwait(false) : null);
                await s.Print(view, (o, v) => o.Connectors(v)).ConfigureAwait(false);
                return;
            case "sync":
                var id = a.Word(1, "which connector (its id, as ot connectors lists it; github <group> for a group's repository)").ToLowerInvariant();
                using (AppNotRunning(s, "syncs its connectors by itself every few minutes (or Sync now in Settings › Connected apps)"))
                {
                    if (id is "github" or "connector-github")
                    {
                        if (github is null || !github.On)
                        {
                            throw new InvalidOperationException("The GitHub Issues extra is off (switch it on in Settings › Extras).");
                        }

                        var group = await s.Read(b => RequireGroup(b, a.WordsFrom(2, "which group (its repository syncs)"))).ConfigureAwait(false);
                        var synced = await github.SyncAsync(group, CancellationToken.None).ConfigureAwait(false);
                        await s.Print(synced, (o, g) => o.Line($"{g.Group}: {g.LastResult ?? "synced"}{(g.Problems.Count > 0 ? " · " + string.Join("; ", g.Problems) : string.Empty)}")).ConfigureAwait(false);
                        return;
                    }

                    var connector = connectors ?? throw new InvalidOperationException("No connector is on (switch one on in Settings › Extras).");
                    var known = connector.View.FirstOrDefault(c => c.Id == id || c.PluginId == id || c.Name.Equals(id, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"There's no connector \"{id}\". ot connectors lists them.");
                    var done = await connector.SyncAsync(known.Id).ConfigureAwait(false);
                    await s.Print(done, (o, c) => o.Line($"{c.Name}: {c.LastResult ?? "synced"}{(c.Problems.Count > 0 ? " · " + string.Join("; ", c.Problems) : string.Empty)}")).ConfigureAwait(false);
                }

                return;
            default:
                throw new CliUsageException("ot connectors lists them; ot connectors sync <id> syncs one. Setting one up is in the app (Settings › Connected apps).");
        }
    }

    /// <summary>
    /// The app isn't running on this data folder (it holds the same lock while it runs), and it can't start until
    /// this is done. Running: an error that says the app does it already.
    /// </summary>
    private static InstanceLock AppNotRunning(CliSession s, string what)
    {
        try
        {
            return InstanceLock.Acquire(s.Options.DataDirectory);
        }
        catch (StoreLockedException ex)
        {
            throw new InvalidOperationException($"One Thing is running: it {what}. ot does this only while the app isn't running.", ex);
        }
    }

    /// <summary>The extras' services as the app builds them, on this ot's tasks folder and settings (nothing runs in the background).</summary>
    private static ServiceProvider PluginServices(CliSession s)
    {
        var services = new ServiceCollection();
        services.AddSingleton(s.Options);
        services.AddSingleton(s.Context.Time);
        services.AddSingleton(s.Settings);
        services.AddSingleton(s.Store);
        services.AddSingleton<IBoardStore>(s.Store);
        services.AddSingleton(s.Links);
        services.AddSingleton<IHostApplicationLifetime>(new CliLifetime());
        services.AddLogging();
        services.AddHttpClient();
        PluginHost.AddServices(services, s.Options);
        return services.BuildServiceProvider();
    }

    /// <summary>ot's own lifetime: nothing stops it from outside but Ctrl+C, which ends the process.</summary>
    private sealed class CliLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}

/// <summary>The connectors (Notion, Microsoft To Do…) and the GitHub Issues connector (null: off).</summary>
internal sealed record ConnectorsDto(IReadOnlyList<ConnectorView> Connectors, GitHubView? GitHub);
