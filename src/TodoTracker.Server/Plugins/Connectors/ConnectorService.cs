using Microsoft.Extensions.Hosting;
using TodoTracker.Core;
using TodoTracker.Core.Connectors;

namespace TodoTracker.Server.Plugins.Connectors;

public sealed record ConnectorView(
    string Id,
    string Name,
    string? Missing,
    string? Target,
    string? TargetName,
    Guid? GroupId,
    ConnectorDirection Direction,
    bool Enabled,
    DateTimeOffset? LastRun,
    string? LastResult,
    IReadOnlyList<string> Problems,
    int Linked,
    string? ClientId,
    MicrosoftSignInView? SignIn,
    bool HasToken);

public sealed record ConnectorConfigRequest(string? Target, string? TargetName, Guid? GroupId, ConnectorDirection? Direction, bool? Enabled, string? ClientId);

/// <summary>What a sync would do, counted (shown before the first one).</summary>
public sealed record ConnectorPreview(int AddHere, int SendThere, int UpdateHere, int UpdateThere, int Archive, int Linked);

public sealed record ConnectorTokenRequest(string? Token);

/// <summary>
/// Runs the connectors: on request and every few minutes for those switched on. One thing at a time: a sync, a change
/// of settings and a disconnect never overlap (so a sync can't write back links of a list that was just changed).
/// Syncs run to the end even if the page that asked closes (the app stopping cancels them).
/// </summary>
public sealed class ConnectorService(IEnumerable<IConnectorKind> kinds, IBoardStore store, TimeProvider time, TodoTrackerServerOptions options, IHostApplicationLifetime lifetime) : IDisposable
{
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly IReadOnlyList<IConnectorKind> _kinds = [.. kinds];

    public IReadOnlyList<ConnectorView> View => [.. _kinds.Select(ViewOf)];

    public T Kind<T>()
        where T : IConnectorKind => _kinds.OfType<T>().FirstOrDefault() ?? throw new KeyNotFoundException("That connector is switched off (Plugins).");

    public void Dispose() => _busy.Dispose();

    public async Task<ConnectorView> ConfigureAsync(string id, ConnectorConfigRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var kind = Get(id);
        if (request.GroupId is { } g && !await store.ReadAsync(b => b.Groups.Any(x => x.Id == g), cancellationToken).ConfigureAwait(false))
        {
            throw new KeyNotFoundException("There is no such group.");
        }

        if (request.ClientId is { } clientId && !Guid.TryParse(clientId.Trim(), out _))
        {
            throw new ArgumentException("A client id looks like 00000000-0000-0000-0000-000000000000.", nameof(request));
        }

        await OneAtATimeAsync(() =>
        {
            kind.Files.Update(s =>
            {
                var targetChanged = request.Target is not null && request.Target != s.Target;
                if (targetChanged)
                {
                    // Another list: its links start over (its items carry task ids, so nothing is copied).
                    kind.Files.SaveLinks([]);
                }

                return s with
                {
                    Target = request.Target ?? s.Target,
                    TargetName = request.TargetName ?? (targetChanged ? null : s.TargetName),
                    GroupId = request.GroupId ?? s.GroupId,
                    Direction = request.Direction ?? s.Direction,
                    Enabled = request.Enabled ?? (!targetChanged && s.Enabled),
                    ClientId = request.ClientId?.Trim() ?? s.ClientId,
                };
            });
            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
        return ViewOf(kind);
    }

    public async Task<ConnectorPreview> PreviewAsync(string id, CancellationToken cancellationToken)
    {
        var kind = Ready(id);
        var remote = await kind.ConnectAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plan = await Runner(kind).PlanAsync(remote, Target(kind), kind.Files.Links, cancellationToken).ConfigureAwait(false);
            return new ConnectorPreview(
                plan.Ops.OfType<CreateLocal>().Count(),
                plan.Ops.OfType<CreateRemote>().Count(),
                plan.Ops.OfType<UpdateLocal>().Count(),
                plan.Ops.OfType<UpdateRemote>().Count(),
                plan.Ops.OfType<ArchiveRemote>().Count(),
                plan.Links.Count(l => l.State == LinkState.Active));
        }
        finally
        {
            (remote as IDisposable)?.Dispose();
        }
    }

    /// <summary>Syncs now (and from then on every few minutes).</summary>
    public async Task<ConnectorView> SyncAsync(string id)
    {
        var kind = Ready(id);
        await OneAtATimeAsync(async () =>
        {
            await RunAsync(kind, lifetime.ApplicationStopping).ConfigureAwait(false);
            kind.Files.Update(s => s with { Enabled = true });
        }, lifetime.ApplicationStopping).ConfigureAwait(false);
        return ViewOf(kind);
    }

    /// <summary>The background sync: every connector that's on and ready. Never throws for a connector's problem.</summary>
    public async Task SyncAllAsync(CancellationToken cancellationToken)
    {
        foreach (var kind in _kinds)
        {
            if (!SafeSettings(kind, out var settings) || !settings.Enabled || SafeMissing(kind) is not null)
            {
                continue;
            }

            await OneAtATimeAsync(() => RunAsync(kind, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<ConnectorView> DisconnectAsync(string id, CancellationToken cancellationToken)
    {
        var kind = Get(id);
        await OneAtATimeAsync(() =>
        {
            var clientId = SafeSettings(kind, out var s) ? s.ClientId : null;
            (kind as MicrosoftToDoConnector)?.SignIn.SignOut();
            kind.Files.Clear();
            if (clientId is not null)
            {
                kind.Files.Update(x => x with { ClientId = clientId });
            }

            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
        return ViewOf(kind);
    }

    private async Task OneAtATimeAsync(Func<Task> work, CancellationToken cancellationToken)
    {
        await _busy.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await work().ConfigureAwait(false);
        }
        finally
        {
            _busy.Release();
        }
    }

    private async Task RunAsync(IConnectorKind kind, CancellationToken cancellationToken)
    {
        try
        {
            var remote = await kind.ConnectAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var run = await Runner(kind).RunAsync(remote, Target(kind), kind.Files.Links, cancellationToken).ConfigureAwait(false);
                kind.Files.SaveLinks(run.Links);
                kind.Files.Update(s => s with { LastRun = time.GetUtcNow(), LastResult = Summary(run), Problems = run.Problems });
            }
            finally
            {
                (remote as IDisposable)?.Dispose();
            }
        }
        catch (Exception ex) when (ConnectorRunner.IsProblem(ex, cancellationToken))
        {
            try
            {
                kind.Files.Update(s => s with { LastRun = time.GetUtcNow(), LastResult = "Couldn't sync", Problems = [ex.Message] });
            }
            catch (IOException)
            {
                // Its settings can't be written either: the panel says so (see ViewOf).
            }
        }
    }

    /// <summary>What went through, counted from the changes that did (not the ones planned).</summary>
    private static string Summary(ConnectorRun run)
    {
        var done = run.Done.Select(d => d.Op).ToList();
        var parts = new List<string>();
        void Add(int n, string what)
        {
            if (n > 0)
            {
                parts.Add($"{n} {what}");
            }
        }

        Add(done.OfType<CreateLocal>().Count(), "added here");
        Add(done.OfType<CreateRemote>().Count(), "sent");
        Add(done.OfType<UpdateLocal>().Count() + done.OfType<UpdateRemote>().Count(), "updated");
        Add(done.OfType<ArchiveRemote>().Count(), "archived");
        Add(run.Failed, "didn't go through");
        return parts.Count == 0 ? "Up to date" : string.Join(", ", parts);
    }

    private ConnectorRunner Runner(IConnectorKind kind) => new(kind.Id, kind.Name, store, time, options.TimeZone);

    private static ConnectorTarget Target(IConnectorKind kind) => new(kind.Files.Settings.GroupId!.Value, kind.Files.Settings.Direction);

    private IConnectorKind Get(string id) => _kinds.FirstOrDefault(k => k.Id == id) ?? throw new KeyNotFoundException($"There is no connector \"{id}\" (or it's switched off in Plugins).");

    private IConnectorKind Ready(string id)
    {
        var kind = Get(id);
        try
        {
            _ = kind.Files.Links; // a links file that can't be read stops it (it would forget every link)
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        return kind.Missing() is { } missing ? throw new InvalidOperationException(missing) : kind;
    }

    private static bool SafeSettings(IConnectorKind kind, out ConnectorSettings settings)
    {
        try
        {
            settings = kind.Files.Settings;
            return true;
        }
        catch (IOException)
        {
            settings = new ConnectorSettings();
            return false;
        }
    }

    private static string? SafeMissing(IConnectorKind kind)
    {
        try
        {
            return kind.Missing();
        }
        catch (IOException ex)
        {
            return ex.Message;
        }
    }

    private static ConnectorView ViewOf(IConnectorKind kind)
    {
        var readable = SafeSettings(kind, out var s);
        IReadOnlyList<ConnectorLink> links;
        try
        {
            links = kind.Files.Links;
        }
        catch (IOException)
        {
            links = [];
            readable = false;
        }

        return new ConnectorView(
            kind.Id,
            kind.Name,
            readable ? kind.Missing() : $"Couldn't read {kind.Name}'s files in {kind.Files.Folder} (another app may have them open). Nothing was changed.",
            s.Target,
            s.TargetName,
            s.GroupId,
            s.Direction,
            s.Enabled,
            s.LastRun,
            s.LastResult,
            s.Problems,
            links.Count(l => l.State == LinkState.Active),
            s.ClientId,
            (kind as MicrosoftToDoConnector)?.SignIn.View,
            kind.Files.Secret is not null);
    }
}
