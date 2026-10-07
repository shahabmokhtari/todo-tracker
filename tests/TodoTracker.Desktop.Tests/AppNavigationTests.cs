namespace TodoTracker.Desktop.Tests;

public sealed class AppNavigationTests
{
    private static readonly Uri Origin = new("http://127.0.0.1:5317");

    [Theory]
    [InlineData("http://127.0.0.1:5317/", AppLink.App)]
    [InlineData("http://127.0.0.1:5317/index.html#/board", AppLink.App)]
    // The sign-in link (ApiEndpoints.LaunchUrl): sending it to the browser left the window blank (reported by the user).
    [InlineData("http://127.0.0.1:5317/auth?code=abc&return=%2F", AppLink.App)]
    [InlineData("about:blank", AppLink.App)]
    [InlineData("http://127.0.0.1:5317/report.html?id=1", AppLink.OwnPageInBrowser)]
    [InlineData("http://localhost:5317/", AppLink.Outside)]
    [InlineData("https://example.com/a", AppLink.Outside)]
    [InlineData("mailto:me@example.com", AppLink.OutsideIfAsked)]
    [InlineData("obsidian://open?vault=x", AppLink.OutsideIfAsked)]
    [InlineData("data:text/html,<h1>hi</h1>", AppLink.Blocked)]
    [InlineData("javascript:alert(1)", AppLink.Blocked)]
    [InlineData("file:///C:/Windows/win.ini", AppLink.Blocked)]
    [InlineData("not a url", AppLink.Blocked)]
    public void Only_the_app_stays_in_the_window(string url, AppLink expected) =>
        Assert.Equal(expected, AppNavigation.Classify(Origin, url));

    [Fact]
    public void Opening_the_app_again_only_brings_it_forward_and_a_task_or_view_changes_the_address_in_place()
    {
        const string shell = "http://127.0.0.1:5317/#/board";

        Assert.Equal(AppOpen.Focus(), AppNavigation.Plan(shell, loaded: true, "/"));
        Assert.Equal(AppOpen.SetHash("#/task/42"), AppNavigation.Plan(shell, loaded: true, "/#/task/42"));
        Assert.Equal(AppOpen.SetHash("#/ask"), AppNavigation.Plan("http://127.0.0.1:5317/index.html", loaded: true, "/#/ask"));
    }

    [Fact]
    public void Not_loaded_yet_or_on_another_page_it_loads_the_app()
    {
        Assert.Equal(AppOpen.Navigate("/"), AppNavigation.Plan(null, loaded: false, "/"));
        Assert.Equal(AppOpen.Navigate("/#/task/42"), AppNavigation.Plan("http://127.0.0.1:5317/#/board", loaded: false, "/#/task/42"));
        // The report page took the window over: the app comes back (a hash on that page would do nothing).
        Assert.Equal(AppOpen.Navigate("/"), AppNavigation.Plan("http://127.0.0.1:5317/report.html", loaded: true, "/"));
        Assert.Equal(AppOpen.Navigate("/#/task/42"), AppNavigation.Plan("http://127.0.0.1:5317/report.html?id=1", loaded: true, "/#/task/42"));
        Assert.Equal(AppOpen.Navigate("/?connect=1"), AppNavigation.Plan("http://127.0.0.1:5317/", loaded: true, "/?connect=1"));
    }

    [Fact]
    public void The_window_loads_the_app_through_its_sign_in_link() =>
        Assert.Equal(AppLink.App, AppNavigation.Classify(Origin, "http://127.0.0.1:5317/auth?code=x&return=%2F%23%2Ftask%2F42"));

    [Fact]
    public void An_own_page_opened_in_the_browser_keeps_its_query_and_fragment() =>
        Assert.Equal("/report.html?id=1#top", AppNavigation.PathOf("http://127.0.0.1:5317/report.html?id=1#top"));
}
