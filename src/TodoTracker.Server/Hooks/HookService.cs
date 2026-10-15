using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TodoTracker.Core;
using ActivityKind = TodoTracker.Core.ActivityKind;

namespace TodoTracker.Server.Hooks;

/// <summary>One run of a hook: what it ran for, whether it worked, and (the start of) what it printed.</summary>
public sealed record HookRun(DateTimeOffset At, string Event, string EventId, string Hook, bool Ok, int? ExitCode, string? Output, long Milliseconds);

/// <summary>What Settings › Hooks shows.</summary>
public sealed record HookStatus(string Path, bool Exists, string? Problem, IReadOnlyList<HookInfo> Hooks, IReadOnlyList<HookRun> Runs, IReadOnlyDictionary<string, string> Events, string Sample);

/// <summary>A hook as Settings shows it: what it is (<see cref="Hook"/>, to edit), and whether it runs.</summary>
public sealed record HookInfo(string Id, string Event, string Type, string Label, string State, string? Problem, HookSpec Hook);

/// <summary>
/// Turns what happens (the activity log: in the app, by agents, in the files, by sync, and the focus timer) into
/// <see cref="TaskEvent"/>s, once each; runs the hooks in <c>hooks.json</c> for them, one at a time in the background
/// (never holding up the app); streams them to <c>GET /api/events</c>; and raises <see cref="Published"/> for server
/// plugins.
/// </summary>
public sealed partial class HookService : BackgroundService
{
    private const int KeptRuns = 50;
    private const int MaxOutput = 4000;
    private const long MaxLogBytes = 1_000_000;

    /// <summary>Entries this old are never news (after a long time closed, nothing floods in).</summary>
    private static readonly TimeSpan Lookback = TimeSpan.FromDays(1);

    /// <summary>A clock a little ahead on another computer: its entries still count.</summary>
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(10);

    private static readonly HttpClient Http = new();

    private readonly IBoardStore _store;
    private readonly ServerEvents _events;
    private readonly TodoTrackerServerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<HookService> _logger;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Channel<TaskEvent> _queue = Channel.CreateUnbounded<TaskEvent>();
    private readonly ConcurrentDictionary<Channel<TaskEvent>, bool> _subscribers = new();
    private readonly List<HookRun> _runs = [];
    private readonly SemaphoreSlim _scanning = new(1, 1);
    private readonly SemaphoreSlim _running = new(1, 1);
    private Dictionary<string, DateTimeOffset>? _seen;
    private readonly HookStore _hooks;

    public HookService(IBoardStore store, ServerEvents events, HookStore hooks, TodoTrackerServerOptions options, TimeProvider time, ILogger<HookService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(hooks);
        _store = store;
        _hooks = hooks;
        _events = events;
        _options = options;
        _time = time ?? TimeProvider.System;
        _logger = logger;
        _store.Changed += OnChanged;
        _events.Pomodoro += OnPomodoro;
    }

    /// <summary>Every event, as it's found (for server plugins). Handlers run on a background thread and must be quick.</summary>
    public event EventHandler<TaskEvent>? Published;

    public string HooksPath => _hooks.FilePath;

    private string StatePath => Path.Combine(_options.DataDirectory, "hooks-state.json");

    private string LogPath => Path.Combine(_options.DataDirectory, "hooks.log");

    public override void Dispose()
    {
        _store.Changed -= OnChanged;
        _events.Pomodoro -= OnPomodoro;
        _scanning.Dispose();
        _running.Dispose();
        base.Dispose();
    }

    private void OnChanged(object? sender, EventArgs e) => _wake.Writer.TryWrite(true);

    private void OnPomodoro(object? sender, PomodoroEvent e)
    {
        var name = e.Kind == PomodoroEventKind.FocusCompleted ? "focusEnded" : "breakEnded";
        var task = e.ItemId is { } id ? _store.ReadAsync(b => b.Find(id) is { } item ? Describe(b, item) : null).GetAwaiter().GetResult() : null;
        Publish(new TaskEvent(TaskEvent.CurrentVersion, Hash($"pomodoro|{e.Kind}|{e.At.UtcTicks}"), name, e.At, new EventActor("system", null),
            name == "focusEnded" ? "Focus session ended" : "Break ended", task));
    }

    // ---- Finding events -------------------------------------------------------------------------

    /// <summary>
    /// Looks for what happened since the last look and publishes it (each once, also across restarts). The first look
    /// ever starts from now: nothing from before hooks existed runs.
    /// </summary>
    public async Task<IReadOnlyList<TaskEvent>> ScanAsync(CancellationToken cancellationToken = default)
    {
        await _scanning.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            var fresh = _seen is null && !LoadState();

            // The whole last day, not just after the newest entry seen: sync brings in entries made elsewhere with
            // the time they happened there (earlier than what happened here meanwhile). What was seen is skipped.
            var from = now - Lookback;
            var found = await _store.ReadAsync(
                b => b.Activity.Where(a => a.At > from && a.At <= now + Overlap).Select(a => (Key: Key(a), Entry: a)).Where(x => !_seen!.ContainsKey(x.Key))
                    .Select(x => (x.Key, x.Entry, Event: ToEvent(b, x.Entry, x.Key))).ToList(),
                cancellationToken).ConfigureAwait(false);
            if (fresh)
            {
                // The very first look: what's already in the log happened before hooks were here.
                foreach (var (key, entry, _) in found)
                {
                    _seen![key] = entry.At;
                }

                SaveState();
                return [];
            }

            if (found.Count == 0)
            {
                return [];
            }

            foreach (var (key, entry, _) in found)
            {
                _seen![key] = entry.At;
            }

            foreach (var old in _seen!.Where(s => s.Value < from).Select(s => s.Key).ToList())
            {
                _seen!.Remove(old);
            }

            SaveState();
            var events = found.Select(f => f.Event).OfType<TaskEvent>().ToList();
            foreach (var e in events)
            {
                Publish(e);
            }

            return events;
        }
        finally
        {
            _scanning.Release();
        }
    }

    private void Publish(TaskEvent e)
    {
        _queue.Writer.TryWrite(e);
        foreach (var subscriber in _subscribers.Keys)
        {
            subscriber.Writer.TryWrite(e);
        }

        try
        {
            Published?.Invoke(this, e);
        }
#pragma warning disable CA1031 // A plugin's handler failing doesn't stop the others or the hooks.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogHandlerFailed(_logger, ex);
        }
    }

    /// <summary>The events as they happen, until <paramref name="cancellationToken"/> (for <c>GET /api/events</c>).</summary>
    public async IAsyncEnumerable<TaskEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<TaskEvent>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        _subscribers[channel] = true;
        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var e))
                {
                    yield return e;
                }
            }
        }
        finally
        {
            _subscribers.TryRemove(channel, out _);
        }
    }

    private static readonly Dictionary<ActivityKind, string> Names = new Dictionary<ActivityKind, string>
    {
        [ActivityKind.Created] = "taskCreated",
        [ActivityKind.Updated] = "taskUpdated",
        [ActivityKind.NoteAdded] = "noteAdded",
        [ActivityKind.Completed] = "taskCompleted",
        [ActivityKind.Reopened] = "taskReopened",
        [ActivityKind.Scheduled] = "taskScheduled",
        [ActivityKind.ReminderAdded] = "reminderAdded",
        [ActivityKind.ReminderFired] = "reminderDue",
        [ActivityKind.ReminderDismissed] = "reminderDismissed",
        [ActivityKind.Deleted] = "taskDeleted",
        [ActivityKind.Moved] = "taskMoved",
        [ActivityKind.FocusStarted] = "focusStarted",
        [ActivityKind.GroupChanged] = "groupChanged",
        [ActivityKind.AttachmentAdded] = "attachmentAdded",
        [ActivityKind.AttachmentRemoved] = "attachmentRemoved",
        [ActivityKind.LabelChanged] = "labelChanged",
        [ActivityKind.Archived] = "taskArchived",
        [ActivityKind.TimeLogged] = "timeLogged",

        // FocusCompleted: the focus timer says so (focusEnded), also when no task was focused.
    };

    private TaskEvent? ToEvent(TaskBoard board, ActivityEntry a, string key)
    {
        if (!Names.TryGetValue(a.Kind, out var name))
        {
            return null;
        }

        EventTask? task = null;
        if (a.ItemId != Guid.Empty)
        {
            task = board.Find(a.ItemId) is { } item
                ? Describe(board, item)
                : new EventTask(a.ItemId, QuotedTitle().Match(a.Summary) is { Success: true } m ? m.Groups[1].Value : string.Empty, [], null, [], [], "normal", null, false, null, null);
        }

        return new TaskEvent(TaskEvent.CurrentVersion, key, name, a.At, ActorOf(a.Actor), a.Summary, task);
    }

    private EventTask Describe(TaskBoard board, WorkItem item) => new(
        item.Id,
        item.Title,
        item.Path,
        board.Groups.FirstOrDefault(g => g.Id == item.GroupId)?.Name,
        item.Tags,
        item.Labels,
        item.Priority.ToString().ToLowerInvariant(),
        item.Deadline,
        item.IsDone,
        item.Repeat?.ToString(),
        $"{BaseUrl}/#/task/{item.Id}");

    private string BaseUrl => (_options.PublicBaseUrl ?? $"http://127.0.0.1:{_options.Port}").TrimEnd('/');

    private static EventActor ActorOf(Actor actor) => new(JsonNamingPolicy.CamelCase.ConvertName(actor.Kind.ToString()), actor.Name);

    private static string Key(ActivityEntry a) =>
        Hash(string.Join('\u001f', a.At.UtcTicks.ToString(CultureInfo.InvariantCulture), a.ItemId, a.Kind, a.Summary, a.Actor.Kind, a.Actor.Name));

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..20];

    private sealed record SavedState(Dictionary<string, DateTimeOffset> Seen);

    /// <summary>Reads what was already seen; false when there's nothing saved (the first look ever).</summary>
    private bool LoadState()
    {
        SavedState? saved = null;
        try
        {
            if (File.Exists(StatePath))
            {
                saved = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(StatePath), TaskEvent.Json);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogStateFailed(_logger, ex);
        }

        _seen = saved?.Seen ?? [];
        return saved is not null;
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(_options.DataDirectory);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(new SavedState(_seen ?? []), TaskEvent.Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogStateFailed(_logger, ex);
        }
    }

    // ---- Running hooks --------------------------------------------------------------------------

    public HookStatus Status()
    {
        var (_, problem) = _hooks.Read();
        var hooks = _hooks.List().Select(e => new HookInfo(e.Id, e.Event, e.Hook.Type, e.Hook.Label, e.State, e.Problem ?? NoScript(e.Hook), e.Hook)).ToList();
        lock (_runs)
        {
            return new HookStatus(HooksPath, _hooks.Exists, problem, hooks, _runs.AsEnumerable().Reverse().ToList(), EventNames.All, HookFile.Sample);
        }
    }

    private static string? NoScript(HookSpec hook) =>
        hook.Type == "command" && hook.Script is null ? $"Nothing to run on this computer (it needs \"{(OperatingSystem.IsWindows() ? "powershell" : "bash")}\")." : null;

    /// <summary>Runs the hooks for <paramref name="e"/> now, one after another; returns how each went.</summary>
    public async Task<IReadOnlyList<HookRun>> RunAsync(TaskEvent e, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(e);
        var (file, _) = _hooks.Read();
        if (file is null)
        {
            return [];
        }

        // Switched off, or added or changed by an agent and not approved yet: doesn't run.
        var hooks = file.Hooks.Where(p => p.Key == "*" || p.Key == e.Event).SelectMany(p => p.Value).Where(h => h.IsActive && h.Problem() is null && h.Matches(e)).ToList();
        var runs = new List<HookRun>();
        await _running.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var hook in hooks)
            {
                if (hook.Type == "command" && hook.Script is null)
                {
                    continue;
                }

                var run = hook.Type == "http" ? await PostAsync(hook, e, cancellationToken).ConfigureAwait(false) : await ExecuteAsync(hook, e, cancellationToken).ConfigureAwait(false);
                runs.Add(run);
                Record(run);
            }
        }
        finally
        {
            _running.Release();
        }

        return runs;
    }

    private async Task<HookRun> ExecuteAsync(HookSpec hook, TaskEvent e, CancellationToken cancellationToken)
    {
        var started = _time.GetUtcNow();
        var watch = Stopwatch.StartNew();
        var info = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = hook.Cwd is { Length: > 0 } cwd ? Environment.ExpandEnvironmentVariables(cwd) : _options.DataDirectory,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (OperatingSystem.IsWindows())
        {
            info.FileName = PowerShell.Value;
            // UTF-8 both ways (the console's code page would garble anything but plain English).
            var script = "[Console]::InputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); " + hook.Powershell;
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script })
            {
                info.ArgumentList.Add(arg);
            }
        }
        else
        {
            info.FileName = "bash";
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(hook.Bash!);
        }

        info.Environment["TT_EVENT"] = e.Event;
        info.Environment["TT_EVENT_ID"] = e.Id;
        info.Environment["TT_TASK_ID"] = e.Task?.Id.ToString();
        info.Environment["TT_TASK_TITLE"] = e.Task?.Title;
        foreach (var (key, value) in hook.Env ?? [])
        {
            info.Environment[key] = value;
        }

        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException("The hook didn't start.");

            // Read until the pipes close (not cancellable: they close when the process tree is gone).
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(hook.Timeout);
            try
            {
                try
                {
                    await process.StandardInput.WriteAsync(e.ToJson().AsMemory(), timeout.Token).ConfigureAwait(false);
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // It finished without reading the event (a broken pipe): fine, its exit code tells how it went.
                    // Let go of the input now (what's left in it can't be written; disposing again later is then a no-op).
                    try
                    {
                        process.StandardInput.Dispose();
                    }
                    catch (IOException)
                    {
                        // Same broken pipe.
                    }
                }
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

                // Something it left running in the background can hold its output open: that's in the time limit too.
                await Task.WhenAll(output, error).WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Out of time, or the app is stopping (or whoever asked left): nothing it started keeps running.
                Stop(process);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return new HookRun(started, e.Event, e.Id, hook.Label, false, null, $"Stopped after {hook.Timeout.TotalSeconds:0} s (timeoutSec).", watch.ElapsedMilliseconds);
            }

            var text = (await output.ConfigureAwait(false)) + (await error.ConfigureAwait(false));
            return new HookRun(started, e.Event, e.Id, hook.Label, process.ExitCode == 0, process.ExitCode, Trim(text), watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new HookRun(started, e.Event, e.Id, hook.Label, false, null, ex.Message, watch.ElapsedMilliseconds);
        }
    }

    private async Task<HookRun> PostAsync(HookSpec hook, TaskEvent e, CancellationToken cancellationToken)
    {
        var started = _time.GetUtcNow();
        var watch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(hook.Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, hook.Url) { Content = new StringContent(e.ToJson(), Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("X-Todo-Tracker-Event", e.Event);
            foreach (var (key, value) in hook.Headers ?? [])
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }

            using var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return new HookRun(started, e.Event, e.Id, hook.Label, response.IsSuccessStatusCode, (int)response.StatusCode, Trim(body), watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new HookRun(started, e.Event, e.Id, hook.Label, false, null, ex is OperationCanceledException ? $"No answer after {hook.Timeout.TotalSeconds:0} s (timeoutSec)." : ex.Message, watch.ElapsedMilliseconds);
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    private static string? Trim(string text)
    {
        var clean = text.Trim();
        return clean.Length == 0 ? null : clean.Length <= MaxOutput ? clean : clean[..MaxOutput] + "…";
    }

    private static readonly Lazy<string> PowerShell = new(() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim('"'), "pwsh.exe")).FirstOrDefault(File.Exists) ?? "powershell.exe");

    private void Record(HookRun run)
    {
        lock (_runs)
        {
            _runs.Add(run);
            if (_runs.Count > KeptRuns)
            {
                _runs.RemoveAt(0);
            }
        }

        if (!run.Ok)
        {
            LogHookFailed(_logger, run.Hook, run.Event, run.Output);
        }

        try
        {
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes)
            {
                var lines = File.ReadAllLines(LogPath);
                File.WriteAllLines(LogPath, lines[(lines.Length / 2)..]);
            }

            File.AppendAllText(LogPath, JsonSerializer.Serialize(run, TaskEvent.Json) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogStateFailed(_logger, ex);
        }
    }

    // ---- The loop -------------------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableBackgroundLoop)
        {
            return;
        }

        var runner = RunQueueAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(stoppingToken).ConfigureAwait(false);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(_options.TickInterval);
                try
                {
                    await _wake.Reader.ReadAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Look again now and then: sync and other processes change the files too.
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // Keep watching; the next look retries.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogScanFailed(_logger, ex);
            }
        }

        await runner.ConfigureAwait(false);
    }

    private async Task RunQueueAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var e in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await RunAsync(e, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // One hook failing doesn't stop the next event's.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogScanFailed(_logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    [GeneratedRegex("\"(.+?)\"")]
    private static partial Regex QuotedTitle();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hook \"{Hook}\" for {Event} failed: {Output}")]
    private static partial void LogHookFailed(ILogger logger, string hook, string @event, string? output);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Looking for events failed")]
    private static partial void LogScanFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An event handler failed")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hooks state or log couldn't be saved")]
    private static partial void LogStateFailed(ILogger logger, Exception exception);
}
