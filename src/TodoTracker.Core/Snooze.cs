using System.Globalization;
using System.Text.RegularExpressions;

namespace TodoTracker.Core;

/// <summary>A snooze the apps offer: <paramref name="Id"/> is the same everywhere (C#, Swift, Kotlin).</summary>
public sealed record SnoozeChoice(string Id, string Label, DateTimeOffset At);

/// <summary>
/// When a snoozed task comes back. The quick choices (minutes to a month) are the same in every app; a rule typed in
/// "Pick a time…" (or after @ in quick capture) reads like people talk: "3d", "2 weeks", "fri 14:30", "next week",
/// "weekend", "tonight", "9am", "2026-03-01 14:00". Days come back in the morning (9:00) unless a time is given.
/// </summary>
public static partial class Snooze
{
    public const int MorningHour = 9;
    public const int EveningHour = 18;

    /// <summary>"This evening" is offered until this hour.</summary>
    private const int EveningOfferedUntil = 17;

    public static IReadOnlyList<SnoozeChoice> Choices(DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var today = DateOnly.FromDateTime(local.DateTime);
        var choices = new List<SnoozeChoice>
        {
            new("15m", "In 15 minutes", now.AddMinutes(15)),
            new("1h", "In an hour", now.AddHours(1)),
            new("3h", "In 3 hours", now.AddHours(3)),
        };
        if (local.Hour < EveningOfferedUntil)
        {
            choices.Add(new("evening", "This evening", At(today, EveningHour, 0, zone)));
        }

        choices.Add(new("tomorrow", "Tomorrow morning", QuickCaptureParser.TomorrowMorning(now, zone)));
        choices.Add(new("2d", "In 2 days", At(today.AddDays(2), MorningHour, 0, zone)));
        choices.Add(new("monday", "Next Monday", At(Next(today, DayOfWeek.Monday), MorningHour, 0, zone)));
        choices.Add(new("week", "In a week", At(today.AddDays(7), MorningHour, 0, zone)));
        choices.Add(new("month", "In a month", At(today.AddMonths(1), MorningHour, 0, zone)));
        return choices;
    }

    /// <summary>When a snooze ends, the way people say it (same words as the web app's whenText): "today 17:00",
    /// "tomorrow 9:00", "Fri 14:30", "Mon 12 Jan, 9:00".</summary>
    public static string Describe(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var day = DateOnly.FromDateTime(local.DateTime);
        var time = local.ToString("H:mm", CultureInfo.InvariantCulture);
        var days = day.DayNumber - today.DayNumber;
        var weekday = local.ToString("ddd", CultureInfo.InvariantCulture);
        return days switch
        {
            0 => $"today {time}",
            1 => $"tomorrow {time}",
            > 1 and < 7 => $"{weekday} {time}",
            _ => $"{weekday} {local.Day} {local.ToString("MMM", CultureInfo.InvariantCulture)}{(day.Year == today.Year ? string.Empty : $" {day.Year}")}, {time}",
        };
    }

    /// <summary>The time a typed rule means, or null when it isn't understood (or is already past).</summary>
    public static DateTimeOffset? Parse(string? rule, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var text = Spaces().Replace((rule ?? string.Empty).Trim().ToLowerInvariant(), " ");
        if (text.StartsWith("in ", StringComparison.Ordinal))
        {
            text = text[3..];
        }

        if (text.Length == 0)
        {
            return null;
        }

        // Minutes and hours count from now.
        if (ShortSpan().Match(text) is { Success: true } span && int.Parse(span.Groups[1].Value, CultureInfo.InvariantCulture) is var amount and > 0 and < 10_000)
        {
            return span.Groups[2].Value[0] == 'h' ? now.AddHours(amount) : now.AddMinutes(amount);
        }

        // "<day> <time>", "<day>", or "<time>".
        string? dayPart = text;
        TimeOnly? time = null;
        var lastSpace = text.LastIndexOf(' ');
        if (ReadTime(text) is { } whole)
        {
            time = whole;
            dayPart = null;
        }
        else if (lastSpace > 0 && ReadTime(text[(lastSpace + 1)..]) is { } tail)
        {
            time = tail;
            dayPart = text[..lastSpace];
        }

        var local = TimeZoneInfo.ConvertTime(now, zone);
        var today = DateOnly.FromDateTime(local.DateTime);
        DateTimeOffset result;
        if (dayPart is null)
        {
            // Just a time: today, or tomorrow when it's already past.
            var t = time!.Value;
            result = At(today, t.Hour, t.Minute, zone);
            if (result <= now)
            {
                result = At(today.AddDays(1), t.Hour, t.Minute, zone);
            }

            return result;
        }

        if (Day(dayPart, today) is not { } day)
        {
            return null;
        }

        var hour = time?.Hour ?? (dayPart is "tonight" or "this evening" or "evening" ? EveningHour : MorningHour);
        result = At(day.Date, hour, time?.Minute ?? 0, zone);
        return result > now ? result : null;
    }

    /// <summary>The day a word means (and whether it's "tonight"): relative to <paramref name="today"/>.</summary>
    private static (DateOnly Date, bool Evening)? Day(string text, DateOnly today)
    {
        switch (text)
        {
            case "today":
                return (today, false);
            case "tonight" or "this evening" or "evening":
                return (today, true);
            case "tomorrow" or "tmrw":
                return (today.AddDays(1), false);
            case "weekend" or "this weekend" or "the weekend":
                return (Next(today, DayOfWeek.Saturday), false);
            case "next week":
                return (Next(today, DayOfWeek.Monday), false);
            case "next month":
                return (new DateOnly(today.Year, today.Month, 1).AddMonths(1), false);
        }

        var name = text.StartsWith("next ", StringComparison.Ordinal) ? text[5..] : text;
        if (Weekday(name) is { } weekday)
        {
            return (Next(today, weekday), false);
        }

        if (LongSpan().Match(text) is { Success: true } span && int.Parse(span.Groups[1].Value, CultureInfo.InvariantCulture) is var amount and > 0 and < 1000)
        {
            return span.Groups[2].Value switch
            {
                "d" or "day" or "days" => (today.AddDays(amount), false),
                "w" or "week" or "weeks" => (today.AddDays(7 * amount), false),
                _ => (today.AddMonths(amount), false),
            };
        }

        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? (date, false) : null;
    }

    private static DayOfWeek? Weekday(string name)
    {
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            var full = day.ToString().ToLowerInvariant();
            if (name == full || (name.Length >= 3 && full.StartsWith(name, StringComparison.Ordinal)))
            {
                return day;
            }
        }

        return null;
    }

    /// <summary>"14:30", "9:00", "2pm", "9am", "9:30pm".</summary>
    private static TimeOnly? ReadTime(string text)
    {
        if (Clock().Match(text) is not { Success: true } m)
        {
            return null;
        }

        var hour = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var minute = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        var half = m.Groups[3].Value;
        if (half.Length > 0)
        {
            if (hour is < 1 or > 12)
            {
                return null;
            }

            hour = hour % 12 + (half == "pm" ? 12 : 0);
        }
        else if (!m.Groups[2].Success)
        {
            // A bare number isn't a time ("3" could be anything).
            return null;
        }

        return hour < 24 && minute < 60 ? new TimeOnly(hour, minute) : null;
    }

    /// <summary>The next such weekday after today (a week from today when today is that day).</summary>
    private static DateOnly Next(DateOnly today, DayOfWeek day)
    {
        var ahead = ((int)day - (int)today.DayOfWeek + 7) % 7;
        return today.AddDays(ahead == 0 ? 7 : ahead);
    }

    private static DateTimeOffset At(DateOnly date, int hour, int minute, TimeZoneInfo zone)
    {
        var wall = date.ToDateTime(new TimeOnly(hour, minute));
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex("^([0-9]{1,4}) ?(m|min|mins|minute|minutes|h|hr|hrs|hour|hours)$")]
    private static partial Regex ShortSpan();

    [GeneratedRegex("^([0-9]{1,3}) ?(d|day|days|w|week|weeks|mo|month|months)$")]
    private static partial Regex LongSpan();

    [GeneratedRegex("^([0-9]{1,2})(?::([0-9]{2}))? ?(am|pm)?$")]
    private static partial Regex Clock();
}
