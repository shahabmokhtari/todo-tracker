namespace TodoTracker.Desktop;

/// <summary>
/// One Todo Tracker per signed-in user: the first launch owns a named mutex; later launches ask it to come to the
/// front and exit. A restart (switching the tasks folder) waits for the old instance to let go.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string DefaultName = "TodoTracker.Sidebar";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _show;
    private RegisteredWaitHandle? _listener;

    private SingleInstance(Mutex mutex, EventWaitHandle? show)
    {
        _mutex = mutex;
        _show = show;
    }

    /// <summary>The instance's lock, or null when another instance runs (it was asked to show itself).</summary>
    public static SingleInstance? TryAcquire(string name = DefaultName, TimeSpan wait = default)
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
            SignalShow(name);
            return null;
        }

        return new SingleInstance(mutex, OperatingSystem.IsWindows() ? new EventWaitHandle(false, EventResetMode.AutoReset, EventName(name)) : null);
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

    private static string MutexName(string name) => OperatingSystem.IsWindows() ? $@"Local\{name}" : name;

    private static string EventName(string name) => $@"Local\{name}.Show";
}
