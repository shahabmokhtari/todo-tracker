using System.IO.Pipes;
using System.Text;

namespace TodoTracker.Desktop;

/// <summary>
/// One Todo Tracker per signed-in user: the first launch owns a named mutex; later launches ask it to come to the
/// front and exit, or hand it what they were started with (a deep link). A restart (switching the tasks folder) waits
/// for the old instance to let go.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string DefaultName = "TodoTracker.Sidebar";

    // A deep link is short; anything longer isn't one of ours.
    private const int MaxMessage = 2048;
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    private readonly string _name;
    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _show;
    private readonly CancellationTokenSource _stop = new();
    private RegisteredWaitHandle? _listener;

    private SingleInstance(string name, Mutex mutex, EventWaitHandle? show)
    {
        _name = name;
        _mutex = mutex;
        _show = show;
    }

    /// <summary>
    /// The instance's lock, or null when another instance runs: that one is handed <paramref name="message"/> (see
    /// <see cref="OnMessage"/>) or, without one (or when it can't take it), asked to show itself.
    /// </summary>
    public static SingleInstance? TryAcquire(string name = DefaultName, TimeSpan wait = default, string? message = null)
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName(name));
        bool owned;
        try
        {
            owned = mutex.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            // The previous instance crashed: the lock is ours now.
            owned = true;
        }

        if (!owned)
        {
            mutex.Dispose();
            if (string.IsNullOrEmpty(message) || !TrySend(name, message))
            {
                SignalShow(name);
            }

            return null;
        }

        return new SingleInstance(name, mutex, OperatingSystem.IsWindows() ? new EventWaitHandle(false, EventResetMode.AutoReset, EventName(name)) : null);
    }

    /// <summary>
    /// Calls <paramref name="onMessage"/> (on a pool thread) with what later launches hand over. Only the same user's
    /// processes can connect; treat the text as untrusted input all the same.
    /// </summary>
    public void OnMessage(Action<string> onMessage)
    {
        ArgumentNullException.ThrowIfNull(onMessage);
        _ = Task.Run(() => ListenAsync(onMessage, _stop.Token));
    }

    /// <summary>Calls <paramref name="onShow"/> (on a pool thread) whenever another launch asks this one to show itself.</summary>
    public void OnShowRequested(Action onShow)
    {
        ArgumentNullException.ThrowIfNull(onShow);
        if (_show is not null)
        {
            _listener = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => onShow(), null, Timeout.Infinite, executeOnlyOnce: false);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        _listener?.Unregister(null);
        _show?.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Released already, or on another thread at shutdown: the OS frees it when the process ends.
        }

        _mutex.Dispose();
    }

    private static void SignalShow(string name)
    {
        if (OperatingSystem.IsWindows() && EventWaitHandle.TryOpenExisting(EventName(name), out var show))
        {
            using (show)
            {
                show.Set();
            }
        }
    }

    private async Task ListenAsync(Action<string> onMessage, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeServerStream(PipeName(_name), PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await using (pipe.ConfigureAwait(false))
                {
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    var buffer = new char[MaxMessage];
                    using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, MaxMessage, leaveOpen: true);
                    var read = await reader.ReadBlockAsync(buffer, cancellationToken).ConfigureAwait(false);
                    var message = new string(buffer, 0, read).Trim();
                    if (message.Length > 0 && read < MaxMessage)
                    {
                        onMessage(message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // A launch that went away mid-message, or the pipe name taken: keep listening (briefly backing off).
                try
                {
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private static bool TrySend(string name, string message)
    {
        if (message.Length >= MaxMessage)
        {
            return false;
        }

        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName(name), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            // The running instance may still be starting up: give it a moment to listen.
            pipe.Connect(SendTimeout);
            var bytes = Encoding.UTF8.GetBytes(message);
            pipe.Write(bytes);
            pipe.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Pipe names are machine-wide: keep each signed-in session's apart (one user can have several, e.g. over RDP).
    private static string PipeName(string name) => $"{name}.{Environment.UserName}.{CurrentSessionId()}";

    private static int CurrentSessionId()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return process.SessionId;
    }

    private static string MutexName(string name) => OperatingSystem.IsWindows() ? $@"Local\{name}" : name;

    private static string EventName(string name) => $@"Local\{name}.Show";
}
