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

    [Theory]
    [InlineData("flag")]
    [InlineData("environment")]
    public async Task A_change_with_a_data_folder_of_its_own_leaves_nothing_in_local_app_data(string how)
    {
        // Only on Linux does local app data follow HOME (macOS and Windows ask the system), so only there can a test
        // give the process a local app data of its own: elsewhere a regression would write into the real one.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "local app data follows HOME only on Linux");
        var home = Directory.CreateDirectory(Path.Combine(_data, "home")).FullName;
        var data = Path.Combine(_data, "data");
        var exe = Path.Combine(AppContext.BaseDirectory, "onething");
        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Environment =
            {
                ["HOME"] = home,
                ["XDG_DATA_HOME"] = null,
                ["XDG_CONFIG_HOME"] = null,
                ["ONETHING_VAULT"] = null,
                ["ONETHING_DATA"] = how == "environment" ? data : null,
                ["ONETHING_CLOUD"] = "off",
            },
        };
        foreach (var arg in (string[])["add", "Buy milk", "--as", "test-agent", .. how == "flag" ? new[] { "--data", data } : []])
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.True(process.ExitCode == 0, $"onething add failed ({process.ExitCode}): {error}");
        Assert.Contains(Directory.GetFiles(Path.Combine(data, "vault"), "*.md", SearchOption.AllDirectories), f => File.ReadAllText(f).Contains("Buy milk", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFileSystemEntries(home, "OneThing", SearchOption.AllDirectories));
        Assert.Single(Directory.GetFiles(Path.Combine(data, "locks"), "vault-*.lock"));
    }

    public void Dispose()
    {
        CliHarness.ForgetDefaultLock(Path.Combine(_data, "vault"));
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
