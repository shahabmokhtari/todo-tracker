using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OneThing.Core;
using OneThing.Core.Vault;

namespace OneThing.Server;

/// <summary>Version history of the vault (null <see cref="History"/> when it's off or git isn't installed).</summary>
public sealed class HistoryService : IDisposable
{
    private readonly Plugins.PluginHost? _plugins;
    private readonly VaultHistory? _history;

    public HistoryService(VaultBoardStore vault, OneThingServerOptions options, Plugins.PluginHost? plugins = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _plugins = plugins;
        _history = options.EnableHistory ? VaultHistory.TryCreate(vault, options.Git) : null;
    }

    /// <summary>The versions, or null: switched off (also since the app started) or git isn't installed.</summary>
    public VaultHistory? History => IsSwitchedOff ? null : _history;

    private bool IsSwitchedOff => _plugins?.IsSwitchedOff(Plugins.HistoryPlugin.Definition.Id) == true;

    public VaultHistory Required =>
        IsSwitchedOff
            ? throw new NotSupportedException("Version history is switched off. Switch it on in Settings › Extras to save and restore versions.")
            : _history ?? throw new NotSupportedException("Version history is off (it needs git: install it from git-scm.com and restart).");

    public void Dispose() => _history?.Dispose();
}

/// <summary>
/// Saves a version a few seconds after things settle (so a burst of edits becomes one version), and checks
/// periodically so edits made outside the app (Obsidian, agents) are captured too.
/// </summary>
public sealed partial class HistoryLoop(HistoryService history, IBoardStore store, ILogger<HistoryLoop> logger, Plugins.PluginHost? plugins = null) : BackgroundService
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
        // Switched off since the app started: no more versions.
        if (plugins?.IsSwitchedOff(Plugins.HistoryPlugin.Definition.Id) == true)
        {
            return;
        }

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
