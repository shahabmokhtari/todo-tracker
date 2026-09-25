using TodoTracker.Core;

namespace TodoTracker.Core.Tests;

public class QuickCaptureTests
{
    // Monday 2026-01-05 14:30 UTC.
    private static readonly DateTimeOffset Now = new(2026, 1, 5, 14, 30, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.CreateCustomTimeZone("test-8", TimeSpan.FromHours(-8), "UTC-8", "UTC-8");

    [Fact]
    public void Plain_text_is_a_normal_task()
    {
        var capture = QuickCaptureParser.Parse("  Deploy feature X ", Now, TimeZoneInfo.Utc);

        Assert.Equal("Deploy feature X", capture.Title);
        Assert.Equal(Priority.Normal, capture.Priority);
        Assert.Null(capture.NextActionAt);
        Assert.Null(capture.Deadline);
    }

    [Theory]
    [InlineData("Fix bug !", Priority.High)]
    [InlineData("Fix bug !!", Priority.Critical)]
    [InlineData("Fix bug !!!", Priority.Critical)]
    [InlineData("Fix bug !low", Priority.Low)]
    [InlineData("Fix bug !HIGH", Priority.High)]
    [InlineData("!urgent Fix bug", Priority.Critical)]
    [InlineData("Fix bug !normal", Priority.Normal)]
    public void Bang_tokens_set_priority(string input, Priority expected)
    {
        var capture = QuickCaptureParser.Parse(input, Now, TimeZoneInfo.Utc);
        Assert.Equal(expected, capture.Priority);
        Assert.Equal("Fix bug", capture.Title);
    }

    [Theory]
    [InlineData("@15m", 15)]
    [InlineData("@2h", 120)]
    [InlineData("@1d", 1440)]
    public void At_tokens_defer_relative(string token, int minutes)
    {
        var capture = QuickCaptureParser.Parse($"Check metrics {token}", Now, TimeZoneInfo.Utc);
        Assert.Equal(Now.AddMinutes(minutes), capture.NextActionAt);
        Assert.Equal("Check metrics", capture.Title);
    }

    [Fact]
    public void At_tomorrow_means_9am_local_next_day()
    {
        var capture = QuickCaptureParser.Parse("Call Bob @tomorrow", Now, Pacific);

        Assert.Equal(new DateTimeOffset(2026, 1, 6, 9, 0, 0, TimeSpan.FromHours(-8)), capture.NextActionAt);
    }

    [Theory]
    [InlineData("due:today", 2026, 1, 5)]
    [InlineData("due:tomorrow", 2026, 1, 6)]
    [InlineData("due:3d", 2026, 1, 8)]
    [InlineData("due:2026-02-01", 2026, 2, 1)]
    public void Due_tokens_set_end_of_day_deadline(string token, int y, int m, int d)
    {
        var capture = QuickCaptureParser.Parse($"Ship it {token}", Now, TimeZoneInfo.Utc);
        Assert.Equal(new DateTimeOffset(y, m, d, 17, 0, 0, TimeSpan.Zero), capture.Deadline);
    }

    [Theory]
    [InlineData("Email @someone about it")]
    [InlineData("Wow! great")]
    [InlineData("due:someday cleanup")]
    public void Unrecognized_tokens_stay_in_the_title(string input)
    {
        var capture = QuickCaptureParser.Parse(input, Now, TimeZoneInfo.Utc);
        Assert.Equal(input, capture.Title);
    }

    [Fact]
    public void At_tomorrow_just_after_midnight_means_this_morning()
    {
        // Review finding: at 00:30, "tomorrow" meant ~32 hours later.
        var lateNight = new DateTimeOffset(2026, 1, 6, 0, 30, 0, TimeSpan.Zero);

        var capture = QuickCaptureParser.Parse("Call Bob @tomorrow", lateNight, TimeZoneInfo.Utc);

        Assert.Equal(new DateTimeOffset(2026, 1, 6, 9, 0, 0, TimeSpan.Zero), capture.NextActionAt);
    }

    [Fact]
    public void Due_today_after_end_of_day_means_end_of_today()
    {
        var evening = new DateTimeOffset(2026, 1, 5, 19, 0, 0, TimeSpan.Zero);

        var capture = QuickCaptureParser.Parse("Ship it due:today", evening, TimeZoneInfo.Utc);

        Assert.Equal(new DateTimeOffset(2026, 1, 5, 23, 59, 0, TimeSpan.Zero), capture.Deadline);
    }

    [Theory]
    [InlineData("due:2026-13-01")]
    [InlineData("@+5m")]
    public void Malformed_time_tokens_stay_in_the_title(string token)
    {
        Assert.Equal($"Ship {token}", QuickCaptureParser.Parse($"Ship {token}", Now, TimeZoneInfo.Utc).Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("!! @2h")]
    public void Input_without_a_title_is_rejected(string input)
    {
        Assert.Throws<ArgumentException>(() => QuickCaptureParser.Parse(input, Now, TimeZoneInfo.Utc));
    }
}

public class RelativeTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "now")]
    [InlineData(0.4, "now")]
    [InlineData(5, "in 5m")]
    [InlineData(59, "in 59m")]
    [InlineData(90, "in 1h 30m")]
    [InlineData(120, "in 2h")]
    [InlineData(23 * 60 + 50, "in 23h")]
    [InlineData(24 * 60, "in 1d")]
    [InlineData(-5, "5m ago")]
    [InlineData(-3 * 24 * 60, "3d ago")]
    public void Formats_compact_relative_time(double minutes, string expected)
    {
        Assert.Equal(expected, RelativeTime.Format(Now.AddMinutes(minutes), Now));
    }

    [Fact]
    public void Far_dates_use_calendar_format()
    {
        Assert.Equal("Feb 20", RelativeTime.Format(new DateTimeOffset(2026, 2, 20, 0, 0, 0, TimeSpan.Zero), Now));
    }
}
