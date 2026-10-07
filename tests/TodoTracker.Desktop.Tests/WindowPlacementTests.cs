using TodoTracker.Desktop;

namespace TodoTracker.Desktop.Tests;

public sealed class WindowPlacementTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tt-placement-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "desktop.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Default_is_docked_on_the_right_of_the_primary_monitor()
    {
        var placement = WindowPlacement.Default;

        Assert.Equal(PlacementMode.Docked, placement.Mode);
        Assert.Equal(DockEdge.Right, placement.Edge);
        Assert.Null(placement.Monitor);
        Assert.False(placement.AlwaysOnTop);
        Assert.Equal(360, placement.DockWidth);
    }

    [Fact]
    public void Store_returns_default_when_file_is_missing_or_corrupt()
    {
        var store = new WindowPlacementStore(FilePath);
        Assert.Equal(WindowPlacement.Default, store.Load());

        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");
        Assert.Equal(WindowPlacement.Default, store.Load());
    }

    [Fact]
    public void Store_round_trips_placement()
    {
        var store = new WindowPlacementStore(FilePath);
        var placement = new WindowPlacement(PlacementMode.Floating, DockEdge.Left, @"\\.\DISPLAY2", 420, AlwaysOnTop: true, new PlacementBounds(100, 120, 400, 700));

        store.Save(placement);

        Assert.Equal(placement, new WindowPlacementStore(FilePath).Load());
    }

    [Fact]
    public void Store_sanitizes_out_of_range_values()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{"mode":"docked","edge":"left","dockWidth":5000,"floating":{"left":0,"top":0,"width":10,"height":-3}}""");

        var placement = new WindowPlacementStore(FilePath).Load();

        Assert.Equal(DockEdge.Left, placement.Edge);
        Assert.Equal(WindowPlacement.MaxDockWidth, placement.DockWidth);
        Assert.Null(placement.Floating);
    }

    [Fact]
    public void Floating_bounds_stay_on_the_monitor_they_are_mostly_on()
    {
        var monitors = new[] { new PlacementBounds(0, 0, 1920, 1040), new PlacementBounds(1920, 0, 2560, 1400) };
        var onSecond = new PlacementBounds(2000, 100, 400, 800);

        Assert.Equal(onSecond, PlacementMath.EnsureVisible(onSecond, monitors));
    }

    [Theory]
    // Docked right: the strip opens toward the screen (left), over the windows, from its own edge.
    [InlineData(DockEdge.Right, 1864, 1560)]
    // Docked left: it opens to the right.
    [InlineData(DockEdge.Left, 0, 0)]
    public void A_docked_strip_opens_over_the_screen_from_its_edge(DockEdge edge, double stripLeft, double expectedLeft)
    {
        var screen = new PlacementBounds(0, 0, 1920, 1080);
        var strip = new PlacementBounds(stripLeft, 0, 56, 1080);

        var peek = PlacementMath.PeekBounds(strip, 360, screen, edge);

        Assert.Equal(new PlacementBounds(expectedLeft, 0, 360, 1080), peek);
    }

    [Fact]
    public void A_floating_strip_opens_where_there_is_room()
    {
        var screen = new PlacementBounds(0, 0, 1920, 1040);

        Assert.Equal(new PlacementBounds(100, 50, 380, 700), PlacementMath.PeekBounds(new PlacementBounds(100, 50, 56, 700), 380, screen, docked: null));
        // Near the right edge: it opens to the left instead, keeping the strip where it is.
        Assert.Equal(new PlacementBounds(1476, 50, 380, 700), PlacementMath.PeekBounds(new PlacementBounds(1800, 50, 56, 700), 380, screen, docked: null));
        // Wider than the screen allows: it stays on screen.
        Assert.Equal(new PlacementBounds(0, 50, 1920, 700), PlacementMath.PeekBounds(new PlacementBounds(1800, 50, 56, 700), 4000, screen, docked: null));
    }
    [Fact]
    public void Floating_bounds_on_a_disconnected_monitor_move_back_to_the_primary()
    {
        // The window was on a monitor that is gone (e.g. laptop undocked).
        var monitors = new[] { new PlacementBounds(0, 0, 1920, 1040) };

        var fixedUp = PlacementMath.EnsureVisible(new PlacementBounds(3000, 200, 400, 800), monitors);

        Assert.Equal(new PlacementBounds(1520, 200, 400, 800), fixedUp);
    }

    [Fact]
    public void Floating_bounds_larger_than_the_screen_are_shrunk_to_fit()
    {
        var monitors = new[] { new PlacementBounds(0, 0, 1280, 700) };

        var fixedUp = PlacementMath.EnsureVisible(new PlacementBounds(-50, -20, 2000, 1200), monitors);

        Assert.Equal(new PlacementBounds(0, 0, 1280, 700), fixedUp);
    }

    [Fact]
    public void Default_floating_bounds_sit_near_the_chosen_edge()
    {
        var work = new PlacementBounds(0, 0, 1920, 1040);

        Assert.Equal(new PlacementBounds(1920 - 380 - 24, 24, 380, 760), PlacementMath.DefaultFloating(work, DockEdge.Right));
        Assert.Equal(new PlacementBounds(24, 24, 380, 760), PlacementMath.DefaultFloating(work, DockEdge.Left));
    }

    [Fact]
    public void Default_floating_bounds_scale_with_the_monitor_dpi()
    {
        // Review finding: floating bounds are physical pixels of the target monitor, so a 150% monitor gets 1.5x.
        var secondary = new PlacementBounds(2880, 0, 1920, 1040);

        Assert.Equal(new PlacementBounds(2880 + 1920 - 570 - 36, 36, 570, 1040 - 72), PlacementMath.DefaultFloating(secondary, DockEdge.Right, scale: 1.5));
    }

    [Theory]
    [InlineData(PlacementMode.Docked, false, true)]
    [InlineData(PlacementMode.Floating, false, false)]
    [InlineData(PlacementMode.Floating, true, true)]
    public void Docked_sidebars_are_always_on_top_and_floating_ones_follow_the_option(PlacementMode mode, bool alwaysOnTop, bool expected)
    {
        Assert.Equal(expected, (WindowPlacement.Default with { Mode = mode, AlwaysOnTop = alwaysOnTop }).IsTopmost);
    }
}
