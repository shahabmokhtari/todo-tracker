using System.Diagnostics;

namespace OneThing.Cli.Tests;

/// <summary>The command is <c>onething</c>, with <c>ot</c> as its short name: both are built next to each other and both run.</summary>
public sealed class LauncherTests : IDisposable
{
    private readonly string _data = Directory.CreateTempSubdirectory("onething-launcher-").FullName;

    [Theory]
    [InlineData("onething")]
    [InlineData("ot")]
    public async Task Both_names_run_the_same_command(string name)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? name + ".exe" : name);
        Assert.True(File.Exists(exe), $"{exe} is missing");
        Directory.CreateDirectory(Path.Combine(_data, "vault"));
        var start = new ProcessStartInfo(exe)
        {
            ArgumentList = { "now", "--data", _data, "--vault", Path.Combine(_data, "vault"), "--no-history" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Environment = { ["ONETHING_VAULT"] = null, ["ONETHING_DATA"] = null, ["ONETHING_CLOUD"] = "off" },
        };

        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.True(process.ExitCode == 0, $"{name} failed ({process.ExitCode}): {error}");
        Assert.Contains("ot add", output, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
