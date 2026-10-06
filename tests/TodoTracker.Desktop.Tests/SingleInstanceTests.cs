namespace TodoTracker.Desktop.Tests;

public sealed class SingleInstanceTests
{
    private static string Unique() => "tt-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Only_the_first_launch_runs_and_later_ones_ask_it_to_show_itself()
    {
        var name = Unique();
        using var first = SingleInstance.TryAcquire(name);
        Assert.NotNull(first);
        var shown = new TaskCompletionSource();
        first.OnShowRequested(() => shown.TrySetResult());

        // A second launch is another thread here (a named mutex is re-entrant on the thread that owns it).
        var second = await Task.Run(() => SingleInstance.TryAcquire(name));

        Assert.Null(second);
        if (OperatingSystem.IsWindows())
        {
            await shown.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_restart_gets_the_lock_once_the_old_instance_lets_go()
    {
        var name = Unique();
        using var holding = new ManualResetEventSlim();

        // The old instance: owns the lock on its own thread for a moment, then exits.
        var old = Task.Factory.StartNew(
            () =>
            {
                using var first = SingleInstance.TryAcquire(name)!;
                holding.Set();
                Thread.Sleep(300);
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        holding.Wait(TestContext.Current.CancellationToken);

        using var next = await Task.Run(() => SingleInstance.TryAcquire(name, TimeSpan.FromSeconds(10)));

        Assert.NotNull(next);
        await old;
    }
}
