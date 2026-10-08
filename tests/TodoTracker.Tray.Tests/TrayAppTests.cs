using System.Net;
using System.Net.Sockets;
using TodoTracker.Desktop;

namespace TodoTracker.Tray.Tests;

public sealed class TrayAppTests : IDisposable
{
    private readonly string _data = Directory.CreateTempSubdirectory("tt-tray-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
            // A file the server just closed: the temp folder is cleaned up later.
        }
    }

    private sealed class RecordingShell : IDesktopShell
    {
        public List<string> Opened { get; } = [];

        public void OpenUrl(string url) => Opened.Add(url);

        public void CopyToClipboard(string text)
        {
        }

        public void RunOnUi(Action action) => action();

        public string? Prompt(string title, string message, string? initialValue = null) => null;

        public bool Confirm(string message) => false;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public void The_command_line_names_the_data_folder_and_port()
    {
        Assert.Equal(new TrayArgs("/tmp/tt", 5400, SmokeTest: true), TrayArgs.Parse(["--data", "/tmp/tt", "--port", "5400", "--smoke-test"]));
        Assert.Equal(new TrayArgs(), TrayArgs.Parse(["--port", "not-a-port", "--unknown"]));
    }

    [Theory]
    [InlineData(true, false, "xdg-open")]
    [InlineData(false, true, "open")]
    public void Links_open_with_the_systems_own_opener(bool linux, bool mac, string opener)
    {
        var start = TrayShell.Opener("http://127.0.0.1:5317/", linux, mac);

        Assert.Equal(opener, start.FileName);
        Assert.Equal(["http://127.0.0.1:5317/"], start.ArgumentList);
        Assert.False(start.UseShellExecute);
    }

    [Fact]
    public async Task It_runs_the_server_and_a_second_copy_opens_the_first_ones_window_instead()
    {
        Environment.SetEnvironmentVariable("TODOTRACKER_CLOUD", "off");
        var options = TrayHost.Options(new TrayArgs(_data, FreePort()));
        var first = new RecordingShell();
        await using var host = await TrayHost.StartAsync(options, first);
        Assert.NotNull(host);
        Assert.Equal(0, host.ViewModel.NowCount);
        Assert.StartsWith("http://127.0.0.1:", host.LaunchUrl("/"), StringComparison.Ordinal);

        var second = new RecordingShell();
        var again = await TrayHost.StartAsync(TrayHost.Options(new TrayArgs(_data, options.Port)), second);

        Assert.Null(again);
        Assert.Single(second.Opened);
        Assert.Contains("/auth?code=", second.Opened[0], StringComparison.Ordinal);
    }
}
