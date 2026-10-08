using TodoTracker.Core;

namespace TodoTracker.Core.Tests;

/// <summary>Snooze: the quick choices every app offers, and the rules typed into "Pick a time…" (or @… in capture).</summary>
public sealed class SnoozeTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    // Monday 2026-01-05, 10:30.
    private static readonly DateTimeOffset Monday = new(2026, 1, 5, 10, 30, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int day, int hour, int minute = 0, int month = 1) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void The_quick_choices_go_from_minutes_to_a_month()
    {
        var choices = Snooze.Choices(Monday, Utc);

        Assert.Equal(["15m", "1h", "3h", "evening", "tomorrow", "2d", "monday", "week", "month"], choices.Select(c => c.Id));
        Assert.Equal(
            [Monday.AddMinutes(15), Monday.AddHours(1), Monday.AddHours(3), At(5, 18), At(6, 9), At(7, 9), At(12, 9), At(12, 9), At(5, 9, month: 2)],
            choices.Select(c => c.At));
        Assert.Equal("This evening", choices.Single(c => c.Id == "evening").Label);
        Assert.Equal("Next Monday", choices.Single(c => c.Id == "monday").Label);
    }

    [Fact]
    public void Late_in_the_day_there_is_no_this_evening_and_tomorrow_is_still_tomorrow()
    {
        var evening = new DateTimeOffset(2026, 1, 9, 19, 0, 0, TimeSpan.Zero); // Friday
        var choices = Snooze.Choices(evening, Utc);

        Assert.DoesNotContain(choices, c => c.Id == "evening");
        Assert.Equal(At(10, 9), choices.Single(c => c.Id == "tomorrow").At);
        Assert.Equal(At(12, 9), choices.Single(c => c.Id == "monday").At);
    }

    [Theory]
    [InlineData("3d", 8, 9, 0)]
    [InlineData("in 3 days", 8, 9, 0)]
    [InlineData("2w", 19, 9, 0)]
    [InlineData("2 weeks", 19, 9, 0)]
    [InlineData("fri", 9, 9, 0)]
    [InlineData("Friday 14:30", 9, 14, 30)]
    [InlineData("next week", 12, 9, 0)]
    [InlineData("weekend", 10, 9, 0)]
    [InlineData("tomorrow 2pm", 6, 14, 0)]
    [InlineData("tonight", 5, 18, 0)]
    [InlineData("14:00", 5, 14, 0)]
    [InlineData("9am", 6, 9, 0)]
    [InlineData("2026-01-20", 20, 9, 0)]
    [InlineData("2026-01-20 16:45", 20, 16, 45)]
    [InlineData("mon", 12, 9, 0)]
    public void Rules_are_read_in_the_local_time_zone(string rule, int day, int hour, int minute)
    {
        Assert.Equal(At(day, hour, minute), Snooze.Parse(rule, Monday, Utc));
    }

    [Theory]
    [InlineData("90m", 90)]
    [InlineData("in 2 hours", 120)]
    [InlineData("45 min", 45)]
    public void Short_spans_count_from_now(string rule, int minutes)
    {
        Assert.Equal(Monday.AddMinutes(minutes), Snooze.Parse(rule, Monday, Utc));
    }

    [Theory]
    [InlineData("")]
    [InlineData("someday")]
    [InlineData("2026-02-30")]
    [InlineData("25:00")]
    [InlineData("0d")]
    [InlineData("yesterday")]
    public void Anything_else_isnt_understood(string rule) => Assert.Null(Snooze.Parse(rule, Monday, Utc));

    [Fact]
    public void A_time_already_past_today_means_tomorrow_and_a_date_in_the_past_isnt_a_snooze()
    {
        Assert.Equal(At(6, 9), Snooze.Parse("9:00", Monday, Utc));
        Assert.Null(Snooze.Parse("2026-01-01", Monday, Utc));
    }

    [Fact]
    public void Local_time_follows_the_persons_time_zone()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        // 10:30 UTC is 5:30 in New York: "tonight" is 18:00 there (23:00 UTC).
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 23, 0, 0, TimeSpan.Zero), Snooze.Parse("tonight", Monday, newYork)!.Value.ToUniversalTime());
    }

    [Fact]
    public void Quick_capture_understands_the_same_rules_after_an_at_sign()
    {
        Assert.Equal(At(9, 9), QuickCaptureParser.Parse("Call the bank @fri", Monday, Utc).NextActionAt);
        Assert.Equal(At(19, 9), QuickCaptureParser.Parse("Review @2w", Monday, Utc).NextActionAt);
        Assert.Equal(At(10, 9), QuickCaptureParser.Parse("Clean @weekend", Monday, Utc).NextActionAt);
        Assert.Equal("Email @team", QuickCaptureParser.Parse("Email @team", Monday, Utc).Title);
    }

    [Theory]
    [InlineData(5, 17, 0, "today 17:00")]
    [InlineData(5, 9, 15, "today 9:15")]
    [InlineData(6, 9, 0, "tomorrow 9:00")]
    [InlineData(9, 14, 30, "Fri 14:30")]
    [InlineData(11, 9, 0, "Sun 9:00")]
    [InlineData(12, 9, 0, "Mon 12 Jan, 9:00")]
    public void Describe_says_when_like_the_web_app(int day, int hour, int minute, string expected)
    {
        Assert.Equal(expected, Snooze.Describe(new DateTimeOffset(2026, 1, day, hour, minute, 0, TimeSpan.Zero), Monday, Utc));
    }

    [Fact]
    public void Describe_adds_the_year_only_when_it_is_another_year_and_uses_the_local_day()
    {
        Assert.Equal("Mon 4 Jan 2027, 9:00", Snooze.Describe(new DateTimeOffset(2027, 1, 4, 9, 0, 0, TimeSpan.Zero), Monday, Utc));
        var newYork = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");
        // 2:00 UTC on Tuesday is still Monday evening (21:00) in New York.
        Assert.Equal("today 21:00", Snooze.Describe(new DateTimeOffset(2026, 1, 6, 2, 0, 0, TimeSpan.Zero), Monday, newYork));
    }
}
