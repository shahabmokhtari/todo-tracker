using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TodoTracker.Core;
using TodoTracker.Core.Vault;

namespace TodoTracker.Server;

/// <summary>Version history of the vault (null <see cref="History"/> when it's off or git isn't installed).</summary>
public sealed class HistoryService : IDisposable
{
    public HistoryService(VaultBoardStore vault, TodoTrackerServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        History = options.EnableHistory ? VaultHistory.TryCreate(vault, options.Git) : null;
    }

    public VaultHistory? History { get; }

    public VaultHistory Required =>
        History ?? throw new NotSupportedException("Version history is off (it needs git: install it from git-scm.com and restart).");

    public void Dispose() => History?.Dispose();
}

/// <summary>
/// Saves a version a few seconds after things settle (so a burst of edits becomes one version), and checks
/// periodically so edits made outside the app (Obsidian, agents) are captured too.
/// </summary>
public sealed partial class HistoryLoop(HistoryService history, IBoardStore store, ILogger<HistoryLoop> logger) : BackgroundService
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Periodic = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (history.History is not { } versions)
        {
            return;
        }

        var changed = new SemaphoreSlim(0);
        void OnChanged(object? sender, EventArgs e) => changed.Release();
        store.Changed += OnChanged;
        try
        {
            await CommitAsync(versions, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await changed.WaitAsync(Periodic, stoppingToken).ConfigureAwait(false);

                // Wait until changes stop for a moment, then save one version.
                while (await changed.WaitAsync(Quiet, stoppingToken).ConfigureAwait(false))
                {
                }

                await CommitAsync(versions, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            store.Changed -= OnChanged;
            changed.Dispose();
        }
    }

    private async Task CommitAsync(VaultHistory versions, CancellationToken cancellationToken)
    {
        try
        {
            await versions.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            LogCommitFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't save a version of the vault.")]
    private static partial void LogCommitFailed(ILogger logger, Exception exception);
}
