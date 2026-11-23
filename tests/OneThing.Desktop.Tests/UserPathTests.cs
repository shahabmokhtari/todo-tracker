namespace OneThing.Desktop.Tests;

/// <summary>Windows: Add ot to your PATH puts the app's cli folder at the end of your own PATH, once.</summary>
public sealed class UserPathTests
{
    private const string Cli = @"C:\Program Files\One Thing\cli";

    [Fact]
    public void The_folder_goes_at_the_end_and_the_rest_stays_as_it_was()
    {
        Assert.Equal(@"%USERPROFILE%\bin;C:\Tools;" + Cli, UserPath.WithFolder(@"%USERPROFILE%\bin;C:\Tools", Cli));
    }

    [Fact]
    public void An_empty_path_gets_just_the_folder()
    {
        Assert.Equal(Cli, UserPath.WithFolder(null, Cli));
        Assert.Equal(Cli, UserPath.WithFolder(string.Empty, Cli));
    }

    [Fact]
    public void A_trailing_semicolon_doesnt_make_an_empty_entry()
    {
        Assert.Equal(@"C:\Tools;" + Cli, UserPath.WithFolder(@"C:\Tools;", Cli));
    }

    [Theory]
    [InlineData(@"C:\Program Files\One Thing\cli")]
    [InlineData(@"c:\program files\one thing\CLI\")]
    [InlineData("\"C:\\Program Files\\One Thing\\cli\"")]
    public void A_folder_already_there_isnt_added_again(string existing)
    {
        Assert.Null(UserPath.WithFolder(@"C:\Tools;" + existing, Cli));
        Assert.True(UserPath.Contains(@"C:\Tools;" + existing, Cli));
    }
}
