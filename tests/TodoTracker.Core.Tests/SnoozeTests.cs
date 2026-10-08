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
    [InlineData("fri", 9, 9, 0)]
    [InlineData("Friday 14:30", 9, 14, 30)]
    [InlineData("fri at 14:30", 9, 14, 30)]
    [InlineData("tomorrow at 9", 6, 9, 0)]
    [InlineData("tomorrow at 3", 6, 15, 0)]
    [InlineData("fri at 5", 9, 17, 0)]
    [InlineData("tomorrow at 7am", 6, 7, 0)]
    [InlineData("tonight at 9", 5, 21, 0)]
    [InlineData("at 9", 6, 9, 0)]
    [InlineData("at 3", 5, 15, 0)]
    [InlineData("tomorrow at 2pm", 6, 14, 0)]
    [InlineData("next week", 12, 9, 0)]
    [InlineData("weekend", 10, 9, 0)]
    [InlineData("tomorrow 2pm", 6, 14, 0)]
    [InlineData("tonight", 5, 18, 0)]
    [InlineData("14:00", 5, 14, 0)]
    [InlineData("at 14:00", 5, 14, 0)]
    [InlineData("9am", 6, 9, 0)]
    [InlineData("2026-01-20", 20, 9, 0)]
    [InlineData("2026-01-20 16:45", 20, 16, 45)]
    [InlineData("mon", 12, 9, 0)]
    [InlineData("mon 14:00", 5, 14, 0)]
    [InlineData("mon 9:00", 12, 9, 0)]
    public void Day_words_are_read_in_the_local_time_zone(string rule, int day, int hour, int minute)
    {
        Assert.Equal(At(day, hour, minute), Snooze.Parse(rule, Monday, Utc));
    }

    [Theory]
    [InlineData("90m", 90)]
    [InlineData("in 2 hours", 120)]
    [InlineData("45 min", 45)]
    [InlineData("3d", 3 * 24 * 60)]
    [InlineData("in 3 days", 3 * 24 * 60)]
    [InlineData("2w", 14 * 24 * 60)]
    [InlineData("2 weeks", 14 * 24 * 60)]
    public void Spans_count_from_now_like_at_3d_in_quick_capture(string rule, int minutes)
    {
        Assert.Equal(Monday.AddMinutes(minutes), Snooze.Parse(rule, Monday, Utc));
        Assert.Equal(Monday.AddMinutes(minutes), QuickCaptureParser.Parse($"x @{rule.Replace(' ', '-')}", Monday, Utc).NextActionAt);
    }

    [Fact]
    public void A_month_is_the_same_time_next_month()
    {
        Assert.Equal(new DateTimeOffset(2026, 2, 5, 10, 30, 0, TimeSpan.Zero), Snooze.Parse("1 month", Monday, Utc));
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
    public void The_hint_says_when_a_rule_is_already_past_or_not_understood()
    {
        var evening = new DateTimeOffset(2026, 1, 5, 19, 0, 0, TimeSpan.Zero);

        Assert.Null(Snooze.Parse("tonight", evening, Utc));
        Assert.Contains("already past", Snooze.Hint("tonight", evening, Utc), StringComparison.Ordinal);
        Assert.Contains("already past", Snooze.Hint("2026-01-01", evening, Utc), StringComparison.Ordinal);
        Assert.Contains("Couldn't tell when \"someday\" is", Snooze.Hint("someday", evening, Utc), StringComparison.Ordinal);
        Assert.Contains("next week", Snooze.Hint("someday", evening, Utc), StringComparison.Ordinal);
    }

    [Fact]
    public void Local_time_follows_the_persons_time_zone()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        // 10:30 UTC is 5:30 in New York: "tonight" is 18:00 there (23:00 UTC).
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 23, 0, 0, TimeSpan.Zero), Snooze.Parse("tonight", Monday, newYork)!.Value.ToUniversalTime());
    }

    [Fact]
    public void A_choice_picked_from_a_menu_opened_a_moment_ago_still_resolves()
    {
        var justAfter = new DateTimeOffset(2026, 1, 5, 17, 1, 0, TimeSpan.Zero);

        Assert.DoesNotContain(Snooze.Choices(justAfter, Utc), c => c.Id == "evening");
        Assert.Equal(At(5, 18), Snooze.Resolve("evening", justAfter, Utc));
        Assert.Null(Snooze.Resolve("evening", new DateTimeOffset(2026, 1, 5, 18, 30, 0, TimeSpan.Zero), Utc));
        Assert.Equal(At(12, 9), Snooze.Resolve("monday", justAfter, Utc));
        Assert.Null(Snooze.Resolve("fortnight", justAfter, Utc));
    }

    [Fact]
    public void Quick_capture_understands_the_same_rules_after_an_at_sign()
    {
        Assert.Equal(At(9, 9), QuickCaptureParser.Parse("Call the bank @fri", Monday, Utc).NextActionAt);
        Assert.Equal(Monday.AddDays(14), QuickCaptureParser.Parse("Review @2w", Monday, Utc).NextActionAt);
        Assert.Equal(At(10, 9), QuickCaptureParser.Parse("Clean @weekend", Monday, Utc).NextActionAt);
        Assert.Equal("Email @team", QuickCaptureParser.Parse("Email @team", Monday, Utc).Title);
        // Words with a space are written with a hyphen after @; dates keep theirs.
        Assert.Equal(At(12, 9), QuickCaptureParser.Parse("Plan @next-week", Monday, Utc).NextActionAt);
        Assert.Equal(new DateTimeOffset(2026, 1, 6, 14, 0, 0, TimeSpan.Zero), QuickCaptureParser.Parse("Plan @tomorrow-14:00", Monday, Utc).NextActionAt);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero), QuickCaptureParser.Parse("Plan @2026-03-01", Monday, Utc).NextActionAt);
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
