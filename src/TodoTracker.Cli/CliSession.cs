using System.Text.Json;
using TodoTracker.Core;
using TodoTracker.Core.Vault;
using TodoTracker.Server;

namespace TodoTracker.Cli;

/// <summary>One <c>tt</c> invocation: the open vault, who is acting, and how to print.</summary>
internal sealed class CliSession : IDisposable
{
    private readonly CliContext _context;
    private readonly TodoTrackerServerOptions _options;
    private HistoryService? _history;

    private CliSession(CliContext context, TodoTrackerServerOptions options, SettingsStore settings, VaultBoardStore store, Actor actor, bool json)
    {
        _context = context;
        _options = options;
        Settings = settings;
        Store = store;
        Links = new VaultLinks(store);
        Actor = actor;
        Json = json;
        Output = new CliOutput(context.Out, context.TimeZone);
    }

    public VaultBoardStore Store { get; }

    public VaultLinks Links { get; }

    public SettingsStore Settings { get; }

    public Actor Actor { get; }

    public bool Json { get; }

    public CliOutput Output { get; }

    public TimeZoneInfo Zone => _context.TimeZone;

    public DateTimeOffset Now => _context.Time.GetUtcNow();

    public CliContext Context => _context;

    /// <summary>Version history; created on first use so read-only commands never start git.</summary>
    public HistoryService History => _history ??= new HistoryService(Store, _options);

    public static CliSession Open(CliArgs args, CliContext context)
    {
        var options = CliApp.Options(args, context, watch: false);
        var settings = new SettingsStore(options);
        var store = TodoTrackerHost.OpenVault(options, settings, context.Time);
        var agent = args.Value("as") ?? context.Agent;
        return new CliSession(context, options, settings, store, string.IsNullOrWhiteSpace(agent) ? Actor.User : Actor.Agent(agent), args.Json);
    }

    public Task<T> Read<T>(Func<TaskBoard, T> read) => Store.ReadAsync(read);

    /// <summary>Applies a change to one task, saves a version, and returns the task as the API shows it.</summary>
    public async Task<ItemDto> Change(Func<TaskBoard, DateTimeOffset, WorkItem> change)
    {
        var id = await Store.UpdateAsync(b => change(b, Now).Id).ConfigureAwait(false);
        await SaveVersion().ConfigureAwait(false);
        return await Item(id).ConfigureAwait(false);
    }

    public Task<ItemDto> Item(Guid id) => Store.ReadAsync(b => Wire.Item(b.Get(id), Now, b, Links));

    /// <summary>
    /// Saves a version right away. The change itself is already saved, so a failure here is only a warning (exit 0):
    /// an agent that retried would add the task twice. The app's periodic snapshot records it later anyway.
    /// </summary>
    public async Task SaveVersion()
    {
        try
        {
            if (_options.EnableHistory && History.History is { } versions)
            {
                await versions.CommitAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            await _context.Error.WriteLineAsync($"tt: saved, but couldn't save a version yet ({ex.Message}).").ConfigureAwait(false);
        }
    }

    /// <summary>Prints <paramref name="value"/> as JSON with <c>--json</c>, otherwise runs <paramref name="human"/>.</summary>
    public async Task Print<T>(T value, Action<CliOutput, T> human)
    {
        if (Json)
        {
            await _context.Out.WriteLineAsync(JsonSerializer.Serialize(value, CliApp.JsonOptions)).ConfigureAwait(false);
        }
        else
        {
            human(Output, value);
        }
    }

    public string FullPath(string path) => Path.GetFullPath(Path.Combine(_context.WorkingDirectory, path));

    public void Dispose()
    {
        _history?.Dispose();
        Store.Dispose();
    }
}
