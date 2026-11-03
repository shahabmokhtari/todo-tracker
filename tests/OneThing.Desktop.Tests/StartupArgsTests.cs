using OneThing.Desktop;

namespace OneThing.Desktop.Tests;

public sealed class StartupArgsTests
{
    [Fact]
    public void A_test_run_on_another_folder_and_port_is_read_from_the_command_line()
    {
        var args = StartupArgs.Parse(["--data", @"C:\temp\board", "--port", "5399", "--no-dock", "--theme", "dark"]);

        Assert.Equal(@"C:\temp\board", args.DataDirectory);
        Assert.Equal(5399, args.Port);
        Assert.True(args.NoDock);
        Assert.Equal("dark", args.Theme);
        Assert.Null(args.Error);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("70000")]
    public void A_port_that_is_not_a_port_is_a_friendly_error_instead_of_a_crash(string port)
    {
        var args = StartupArgs.Parse(["--port", port]);

        Assert.Null(args.Port);
        Assert.Equal($"The port must be a number from 1 to 65535, not \"{port}\".", args.Error);
    }

    [Fact]
    public void A_deep_link_ignores_every_other_option()
    {
        var args = StartupArgs.Parse(["onething://task/abc\" --data C:\\other", "--data", "C:\\other"]);

        Assert.StartsWith("onething://task/abc", args.Link, StringComparison.Ordinal);
        Assert.Null(args.DataDirectory);
    }

    [Fact]
    public void A_restart_keeps_the_data_folder_port_docking_and_theme_of_this_run()
    {
        var args = StartupArgs.Parse(["--data", @"C:\temp\my board", "--port", "5399", "--no-dock", "--theme", "light", "--smoke-test", "--screenshot", "x.png"]);

        Assert.Equal(["--restart", "--data", @"C:\temp\my board", "--port", "5399", "--no-dock", "--theme", "light"], args.RestartArguments());
    }

    [Fact]
    public void A_restart_of_the_users_own_app_adds_nothing_but_restart()
    {
        Assert.Equal(["--restart"], StartupArgs.Parse([]).RestartArguments());
    }
}
