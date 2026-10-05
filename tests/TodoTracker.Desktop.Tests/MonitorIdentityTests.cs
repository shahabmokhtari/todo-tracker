using TodoTracker.Desktop;

namespace TodoTracker.Desktop.Tests;

public sealed class MonitorIdentityTests
{
    private static readonly MonitorKey Primary = new(@"\\?\DISPLAY#BOE0A1B#4&1&UID8388688#{e6f07b5f}", @"\\.\DISPLAY273", "Built-in", IsPrimary: true, 1920, 1200);
    private static readonly MonitorKey Dell = new(@"\\?\DISPLAY#DEL4321#5&2&UID4352#{e6f07b5f}", @"\\.\DISPLAY274", "DELL U2720Q", IsPrimary: false, 2560, 1440);
    private static readonly MonitorKey Lg = new(@"\\?\DISPLAY#GSM5B7F#5&3&UID4353#{e6f07b5f}", @"\\.\DISPLAY275", "LG HDR QHD", IsPrimary: false, 2560, 1440);

    [Fact]
    public void Resolves_the_saved_monitor_by_its_stable_id_even_when_windows_renumbered_the_device_name()
    {
        // Reconnecting a monitor gives it a new \\.\DISPLAYn name; the device path stays the same.
        var reconnected = Dell with { DeviceName = @"\\.\DISPLAY301" };

        Assert.Equal(reconnected, MonitorIdentity.Resolve(Dell.Id, [Primary, reconnected, Lg]));
    }

    [Fact]
    public void Still_understands_placements_saved_with_the_old_device_name()
    {
        Assert.Equal(Lg, MonitorIdentity.Resolve(@"\\.\DISPLAY275", [Primary, Dell, Lg]));
    }

    [Fact]
    public void A_missing_monitor_resolves_to_nothing_so_callers_fall_back_to_the_primary()
    {
        Assert.Null(MonitorIdentity.Resolve(Dell.Id, [Primary, Lg]));
        Assert.Null(MonitorIdentity.Resolve(null, [Primary, Lg]));
    }

    [Fact]
    public void Matching_ignores_case()
    {
        Assert.Equal(Dell, MonitorIdentity.Resolve(Dell.Id.ToUpperInvariant(), [Primary, Dell]));
    }

    [Fact]
    public void A_monitor_in_a_clone_group_is_found_even_when_another_clone_reports_first()
    {
        // Cloned monitors share one desktop (one GDI source); the saved one may not be the first path reported.
        var clone = Lg with { Aliases = [Dell.Id] };

        Assert.Equal(clone, MonitorIdentity.Resolve(Dell.Id, [Primary, clone]));
    }

    [Fact]
    public void Labels_use_the_friendly_name_resolution_and_primary_marker()
    {
        Assert.Equal("Display 1 · Built-in (primary) · 1920×1200", MonitorIdentity.Label(0, Primary));
        Assert.Equal("Display 2 · DELL U2720Q · 2560×1440", MonitorIdentity.Label(1, Dell));
    }

    [Fact]
    public void Labels_skip_a_missing_friendly_name()
    {
        Assert.Equal("Display 3 · 2560×1440", MonitorIdentity.Label(2, Lg with { FriendlyName = " " }));
    }
}
