namespace TodoTracker.Desktop.Tests;

public sealed class SingleInstanceTests
{
    private static string Unique() => "tt-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Only_the_first_launch_runs_and_later_ones_ask_it_to_show_itself()
    {
        var name = Unique();
        var shown = new TaskCompletionSource();
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        // The running instance owns the lock on its own thread (a named mutex is re-entrant on the owning thread,
        // so the "second launch" must never run there).
        var first = Task.Factory.StartNew(
            () =>
            {
                using var instance = SingleInstance.TryAcquire(name)!;
                instance.OnShowRequested(() => shown.TrySetResult());
                holding.Set();
                release.Wait();
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        holding.Wait(TestContext.Current.CancellationToken);

        var second = SingleInstance.TryAcquire(name);

        Assert.Null(second);
        if (OperatingSystem.IsWindows())
        {
            await shown.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        release.Set();
        await first;
    }

    [Fact]
    public async Task A_later_launch_with_a_link_hands_it_to_the_running_instance()
    {
        var name = Unique();
        var received = new TaskCompletionSource<string>();
        var shown = false;
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var first = Task.Factory.StartNew(
            () =>
            {
                using var instance = SingleInstance.TryAcquire(name)!;
                instance.OnShowRequested(() => shown = true);
                instance.OnMessage(m => received.TrySetResult(m));
                holding.Set();
                release.Wait();
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        holding.Wait(TestContext.Current.CancellationToken);

        var second = SingleInstance.TryAcquire(name, message: "todotracker://task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f");

        Assert.Null(second);
        Assert.Equal("todotracker://task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f", await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.False(shown);
        release.Set();
        await first;
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
