using System.Diagnostics;

namespace TodoTracker.Server.Tests;

/// <summary>The Mac app starts the server with --parent-pid: the server must never outlive the app.</summary>
public sealed class ParentProcessTests
{
    [Fact]
    public async Task The_server_stops_once_the_app_that_started_it_is_gone()
    {
        using var app = Process.Start(new ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true, UseShellExecute = false })!;
        await app.WaitForExitAsync(TestContext.Current.CancellationToken);
        var stopped = new TaskCompletionSource();

        await ParentProcess.WatchAsync(app.Id, () => stopped.TrySetResult(), TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(stopped.Task.IsCompleted);
    }

    [Fact]
    public async Task While_the_app_runs_the_server_keeps_running()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var stopped = false;

        await ParentProcess.WatchAsync(Environment.ProcessId, () => stopped = true, TimeSpan.FromMilliseconds(20), cts.Token);

        Assert.False(stopped);
    }

    [Theory]
    [InlineData(new[] { "--port", "5400" }, null)]
    [InlineData(new[] { "--parent-pid", "4242" }, 4242)]
    [InlineData(new[] { "--parent-pid", "nope" }, null)]
    public void The_parent_comes_from_the_command_line(string[] args, int? expected) =>
        Assert.Equal(expected, ParentProcess.FromArguments(args));
}
