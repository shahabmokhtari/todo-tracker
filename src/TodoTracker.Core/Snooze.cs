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

    public static IReadOnlyList<SnoozeChoice> Choices(DateTimeOffset now, TimeZoneInfo zone) => Choices(now, zone, all: false);

    /// <summary>
    /// When a quick choice ends if picked now; null for an unknown id or a time already past. A menu opened a moment
    /// ago still works: "evening" picked at 17:01 is 18:00.
    /// </summary>
    public static DateTimeOffset? Resolve(string id, DateTimeOffset now, TimeZoneInfo zone) =>
        Choices(now, zone, all: true).FirstOrDefault(c => c.Id == id) is { } choice && choice.At > now ? choice.At : null;

    private static List<SnoozeChoice> Choices(DateTimeOffset now, TimeZoneInfo zone, bool all)
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
        if (all || local.Hour < EveningOfferedUntil)
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
    public static DateTimeOffset? Parse(string? rule, DateTimeOffset now, TimeZoneInfo zone) =>
        Read(rule, now, zone) is { } at && at > now ? at : null;

    /// <summary>Why a rule can't be used: it's already past, or it isn't understood (with examples).</summary>
    public static string Hint(string? rule, DateTimeOffset now, TimeZoneInfo zone)
    {
        var text = (rule ?? string.Empty).Trim();
        return Read(rule, now, zone) is { } at && at <= now
            ? $"\"{text}\" is already past. Pick a later time (e.g. tomorrow, or next week)."
            : $"Couldn't tell when \"{text}\" is. Try 3d, 2 weeks, fri 14:00, next week, weekend, tonight, 9am or 2026-03-01.";
    }

    /// <summary>What a rule says, even when that's already past (null: not understood).</summary>
    private static DateTimeOffset? Read(string? rule, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var text = Spaces().Replace((rule ?? string.Empty).Trim().ToLowerInvariant(), " ");
        foreach (var lead in (string[])["in ", "at "])
        {
            if (text.StartsWith(lead, StringComparison.Ordinal))
            {
                text = text[lead.Length..];
            }
        }

        if (text.Length == 0)
        {
            return null;
        }

        // Spans count from now (as @3d always has): 45m, 2h, 3d, 2 weeks, 1 month.
        if (ShortSpan().Match(text) is { Success: true } span && int.Parse(span.Groups[1].Value, CultureInfo.InvariantCulture) is var amount and > 0 and < 10_000)
        {
            return span.Groups[2].Value[0] == 'h' ? now.AddHours(amount) : now.AddMinutes(amount);
        }

        if (LongSpan().Match(text) is { Success: true } longSpan && int.Parse(longSpan.Groups[1].Value, CultureInfo.InvariantCulture) is var count and > 0 and < 1000)
        {
            return longSpan.Groups[2].Value switch
            {
                "d" or "day" or "days" => now.AddDays(count),
                "w" or "week" or "weeks" => now.AddDays(7 * count),
                _ => now.AddMonths(count),
            };
        }

        // "<day> <time>", "<day> at <time>", "<day>", or "<time>".
        string? dayPart = text;
        TimeOnly? time = null;
        var atWord = text.LastIndexOf(" at ", StringComparison.Ordinal);
        var lastSpace = text.LastIndexOf(' ');
        if (ReadTime(text) is { } whole)
        {
            time = whole;
            dayPart = null;
        }
        else if (atWord > 0)
        {
            // After "at" a bare hour is a time too ("tomorrow at 9").
            time = ReadTime(text[(atWord + 4)..], bareHour: true);
            if (time is null)
            {
                return null;
            }

            dayPart = text[..atWord];
        }
        else if (lastSpace > 0 && ReadTime(text[(lastSpace + 1)..]) is { } tail)
        {
            time = tail;
            dayPart = text[..lastSpace];
        }

        var local = TimeZoneInfo.ConvertTime(now, zone);
        var today = DateOnly.FromDateTime(local.DateTime);
        if (dayPart is null)
        {
            // Just a time: today, or tomorrow when it's already past.
            var t = time!.Value;
            var result = At(today, t.Hour, t.Minute, zone);
            return result <= now ? At(today.AddDays(1), t.Hour, t.Minute, zone) : result;
        }

        if (Day(dayPart, today) is not { } day)
        {
            return null;
        }

        // "mon 14:00" on a Monday morning means this afternoon (just "mon" means next week's).
        if (time is { } given && day.Weekday && day.Date == today.AddDays(7) && At(today, given.Hour, given.Minute, zone) > now)
        {
            return At(today, given.Hour, given.Minute, zone);
        }

        return At(day.Date, time?.Hour ?? (day.Evening ? EveningHour : MorningHour), time?.Minute ?? 0, zone);
    }

    /// <summary>The day a word means, whether it's "tonight", and whether it's a weekday's name; relative to <paramref name="today"/>.</summary>
    private static (DateOnly Date, bool Evening, bool Weekday)? Day(string text, DateOnly today)
    {
        switch (text)
        {
            case "today":
                return (today, false, false);
            case "tonight" or "this evening" or "evening":
                return (today, true, false);
            case "tomorrow" or "tmrw":
                return (today.AddDays(1), false, false);
            case "weekend" or "this weekend" or "the weekend":
                return (Next(today, DayOfWeek.Saturday), false, false);
            case "next week":
                return (Next(today, DayOfWeek.Monday), false, false);
            case "next month":
                return (new DateOnly(today.Year, today.Month, 1).AddMonths(1), false, false);
        }

        var name = text.StartsWith("next ", StringComparison.Ordinal) ? text[5..] : text;
        if (Weekday(name) is { } weekday)
        {
            return (Next(today, weekday), false, name == text);
        }

        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? (date, false, false) : null;
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
    private static TimeOnly? ReadTime(string text, bool bareHour = false)
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
        else if (!m.Groups[2].Success && !bareHour)
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
