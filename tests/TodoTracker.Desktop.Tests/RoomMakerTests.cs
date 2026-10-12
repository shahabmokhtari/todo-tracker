using TodoTracker.Desktop;

namespace TodoTracker.Desktop.Tests;

/// <summary>
/// Fitting a full-screen Remote Desktop window next to the docked sidebar: tried once per window, abandoned when the
/// client won't stay that size, and undone when the sidebar stops needing the room.
/// </summary>
public sealed class RoomMakerTests
{
    private static readonly PlacementBounds Monitor = new(0, 0, 2560, 1440);
    private static readonly PlacementBounds Room = new(0, 0, 2200, 1392);

    [Fact]
    public void A_window_is_fitted_into_the_room_once()
    {
        var maker = new RoomMaker();

        Assert.Equal(Room, maker.Plan(1, Monitor, Monitor, Room));

        // Full screen again later (the user chose it, or the client went back): it isn't fought over.
        Assert.Null(maker.Plan(1, Monitor, Monitor, Room));
        Assert.Equal(Room, maker.Plan(2, Monitor, Monitor, Room));
    }

    [Fact]
    public void A_session_spanning_several_monitors_is_not_squeezed_onto_one()
    {
        var maker = new RoomMaker();
        var allMonitors = new PlacementBounds(0, 0, 5120, 1440);

        Assert.Null(maker.Plan(1, allMonitors, Monitor, Room));
        Assert.Empty(maker.Restore(_ => Room));
    }

    [Fact]
    public void A_client_that_goes_back_to_full_screen_is_left_alone()
    {
        var maker = new RoomMaker();
        maker.Plan(1, Monitor, Monitor, Room);

        Assert.False(maker.Check(1, Monitor));
        Assert.Empty(maker.Restore(_ => Monitor));
    }

    [Fact]
    public void A_client_that_kept_the_size_is_put_back_full_screen_when_the_room_is_not_needed_anymore()
    {
        var maker = new RoomMaker();
        maker.Plan(1, Monitor, Monitor, Room);
        maker.Plan(2, Monitor, Monitor, Room);
        maker.Plan(3, Monitor, Monitor, Room);
        Assert.True(maker.Check(1, Room));
        Assert.True(maker.Check(2, Room));
        Assert.True(maker.Check(3, Room));

        // 2 was moved or resized by the user since: it stays where they put it. 3 is gone.
        var restored = maker.Restore(handle => handle switch
        {
            1 => Room with { Width = Room.Width + 1 },
            2 => new PlacementBounds(100, 100, 800, 600),
            _ => null,
        });

        Assert.Equal([new RestoreStep(1, Monitor)], restored);

        // Once put back, nothing is remembered: the next full screen is a fresh start.
        Assert.Empty(maker.Restore(_ => Room));
        Assert.Equal(Room, maker.Plan(1, Monitor, Monitor, Room));
    }

    [Fact]
    public void A_failed_resize_is_not_retried()
    {
        var maker = new RoomMaker();
        maker.Plan(1, Monitor, Monitor, Room);

        maker.Failed(1);

        Assert.Null(maker.Plan(1, Monitor, Monitor, Room));
        Assert.Empty(maker.Restore(_ => Room));
    }

    [Fact]
    public void Fitted_windows_follow_the_room_when_the_sidebar_collapses_or_opens()
    {
        var maker = new RoomMaker();
        maker.Plan(1, Monitor, Monitor, Room);
        maker.Plan(2, Monitor, Monitor, Room);
        var strip = Room with { Width = 2504 };

        // 2 was moved by the user since: it stays where they put it (and isn't put back later either).
        var moved = maker.Resize(strip, handle => handle == 1 ? Room : new PlacementBounds(100, 100, 800, 600));

        Assert.Equal([new RestoreStep(1, strip)], moved);
        Assert.Empty(maker.Resize(strip, _ => strip));
        Assert.Equal([new RestoreStep(1, Monitor)], maker.Restore(_ => strip));
    }

    [Fact]
    public void Checking_an_unknown_window_says_it_is_fine()
    {
        Assert.True(new RoomMaker().Check(9, Monitor));
    }
}
