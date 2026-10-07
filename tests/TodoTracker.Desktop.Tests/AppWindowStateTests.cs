namespace TodoTracker.Desktop.Tests;

public sealed class AppWindowStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tt-appwin-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string File(string name = "app-window.json") => Path.Combine(_dir, name);

    [Fact]
    public void Nothing_saved_means_a_default_window_with_full_screen_breaks_on()
    {
        var state = new AppWindowStateStore(File()).Load();

        Assert.Null(state.Bounds);
        Assert.False(state.Maximized);
        Assert.True(state.FullScreenBreaks);
    }

    [Fact]
    public void Placement_and_the_break_choice_are_remembered()
    {
        var store = new AppWindowStateStore(File());
        store.Save(new AppWindowState(new PlacementBounds(100, 50, 1280, 800), Maximized: true, FullScreenBreaks: false));

        var loaded = new AppWindowStateStore(File()).Load();

        Assert.Equal(new PlacementBounds(100, 50, 1280, 800), loaded.Bounds);
        Assert.True(loaded.Maximized);
        Assert.False(loaded.FullScreenBreaks);
    }

    [Fact]
    public void A_broken_file_or_a_window_too_small_to_use_falls_back_to_defaults()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File(), "{ not json");
        Assert.Equal(new AppWindowState(), new AppWindowStateStore(File()).Load());

        System.IO.File.WriteAllText(File(), """{ "bounds": { "left": 0, "top": 0, "width": 40, "height": 30 }, "fullScreenBreaks": false }""");
        var loaded = new AppWindowStateStore(File()).Load();
        Assert.Null(loaded.Bounds);
        Assert.False(loaded.FullScreenBreaks);
    }
}
