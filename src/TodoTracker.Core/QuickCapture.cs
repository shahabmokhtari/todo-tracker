using System.Globalization;
using System.Text.RegularExpressions;

namespace TodoTracker.Core;

public sealed record QuickCapture(string Title, Priority Priority, DateTimeOffset? NextActionAt, DateTimeOffset? Deadline);

/// <summary>
/// Parses one-line quick capture so a task can be added without opening a form:
/// <c>!</c>/<c>!!</c>/<c>!low</c> set priority, <c>@2h</c>/<c>@tomorrow</c> defer, <c>due:3d</c>/<c>due:2026-02-01</c> set a deadline.
/// </summary>
public static partial class QuickCaptureParser
{
    private const int MorningHour = 9;
    private const int EndOfDayHour = 17;
    private const int LateNightCutoffHour = 4;

    public static QuickCapture Parse(string input, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var priority = Priority.Normal;
        DateTimeOffset? nextAction = null;
        DateTimeOffset? deadline = null;
        var words = new List<string>();

        foreach (var token in (input ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ParsePriority(token) is { } p)
            {
                priority = p;
            }
            else if (ParseDefer(token, now, zone) is { } at)
            {
                nextAction = at;
            }
            else if (ParseDue(token, now, zone) is { } due)
            {
                deadline = due;
            }
            else
            {
                words.Add(token);
            }
        }

        if (words.Count == 0)
        {
            throw new ArgumentException("Type a task title.", nameof(input));
        }

        return new QuickCapture(string.Join(' ', words), priority, nextAction, deadline);
    }

    private static Priority? ParsePriority(string token) => token.ToLowerInvariant() switch
    {
        "!" or "!high" => Priority.High,
        "!!" or "!!!" or "!critical" or "!urgent" => Priority.Critical,
        "!low" => Priority.Low,
        "!normal" => Priority.Normal,
        _ => null,
    };

    private static DateTimeOffset? ParseDefer(string token, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (!token.StartsWith('@'))
        {
            return null;
        }

        var value = token[1..].ToLowerInvariant();
        if (value == "tomorrow")
        {
            return TomorrowMorning(now, zone);
        }

        return ParseSpan(value) is { } span ? now + span : null;
    }

    /// <summary>Next 9:00. Before 4:00 people still mean "when I wake up", i.e. this morning.</summary>
    public static DateTimeOffset TomorrowMorning(DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var localHour = TimeZoneInfo.ConvertTime(now, zone).Hour;
        return LocalTimeOn(now, zone, localHour < LateNightCutoffHour ? 0 : 1, MorningHour);
    }

    private static DateTimeOffset? ParseDue(string token, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (!token.StartsWith("due:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var value = token[4..].ToLowerInvariant();
        switch (value)
        {
            case "today":
                var endOfDay = LocalTimeOn(now, zone, 0, EndOfDayHour);
                return endOfDay > now ? endOfDay : LocalTimeOn(now, zone, 0, 23).AddMinutes(59);
            case "tomorrow":
                return LocalTimeOn(now, zone, 1, EndOfDayHour);
        }

        if (DaysPattern().Match(value) is { Success: true } m)
        {
            return LocalTimeOn(now, zone, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), EndOfDayHour);
        }

        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return AtLocal(date, EndOfDayHour, zone);
        }

        return null;
    }

    private static TimeSpan? ParseSpan(string value)
    {
        var m = SpanPattern().Match(value);
        if (!m.Success)
        {
            return null;
        }

        var amount = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value switch
        {
            "m" => TimeSpan.FromMinutes(amount),
            "h" => TimeSpan.FromHours(amount),
            _ => TimeSpan.FromDays(amount),
        };
    }

    private static DateTimeOffset LocalTimeOn(DateTimeOffset now, TimeZoneInfo zone, int addDays, int hour)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        return AtLocal(DateOnly.FromDateTime(local.DateTime).AddDays(addDays), hour, zone);
    }

    private static DateTimeOffset AtLocal(DateOnly date, int hour, TimeZoneInfo zone)
    {
        var wall = date.ToDateTime(new TimeOnly(hour, 0));
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }

    [GeneratedRegex("^([1-9][0-9]{0,3})([mhd])$")]
    private static partial Regex SpanPattern();

    [GeneratedRegex("^([1-9][0-9]{0,2})d$")]
    private static partial Regex DaysPattern();
}

public static class RelativeTime
{
    /// <summary>Compact relative time such as "in 5m", "in 1h 30m", "3d ago", or "Feb 20".</summary>
    public static string Format(DateTimeOffset target, DateTimeOffset now)
    {
        var delta = target - now;
        var minutes = (int)Math.Round(Math.Abs(delta.TotalMinutes), MidpointRounding.AwayFromZero);
        if (minutes < 1)
        {
            return "now";
        }

        string text;
        if (minutes < 60)
        {
            text = $"{minutes}m";
        }
        else if (minutes < 180)
        {
            text = minutes % 60 == 0 ? $"{minutes / 60}h" : $"{minutes / 60}h {minutes % 60}m";
        }
        else if (minutes < 24 * 60)
        {
            text = $"{minutes / 60}h";
        }
        else if (minutes < 7 * 24 * 60)
        {
            text = $"{minutes / (24 * 60)}d";
        }
        else
        {
            return target.ToString("MMM d", CultureInfo.InvariantCulture);
        }

        return delta > TimeSpan.Zero ? $"in {text}" : $"{text} ago";
    }
}
