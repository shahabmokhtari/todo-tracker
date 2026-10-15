using System.Globalization;

namespace TodoTracker.Core;

public enum RepeatUnit
{
    Day,
    Week,
    Month,
    Year,
}

/// <summary>
/// How a task repeats, written the way the Obsidian Tasks plugin writes 🔁 rules: "every day", "every 3 days",
/// "every weekday", "every 2 weeks on Monday, Thursday", "every month on the 15th", "every month on the last Friday",
/// "every year on March 5", optionally followed by "when done" (counted from when it's done), "until 2026-12-31" or
/// "for 5 times" (occurrences left, this one included). <see cref="ToString"/> is the canonical text; it parses back
/// to the same rule. Dates are calendar days where the user lives; the same rules run in Swift and Kotlin
/// (tests/fixtures/repeat.json).
/// </summary>
public sealed class Recurrence
{
    public const int MaxInterval = 999;
    public const int MaxCount = 9999;

    private static readonly DayOfWeek[] WorkWeek = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    private static readonly string[] MonthNames = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames[..12];

    private Recurrence()
    {
    }

    public RepeatUnit Unit { get; private init; }

    public int Interval { get; private init; } = 1;

    /// <summary>Weekly: the days it falls on, Monday first (none: the day of the week it started on).</summary>
    public IReadOnlyList<DayOfWeek> Weekdays { get; private init; } = [];

    /// <summary>Monthly or yearly: the day of the month (1–31; a short month uses its last day), or -1 for the last day.</summary>
    public int? MonthDay { get; private init; }

    /// <summary>Monthly: which <see cref="NthDay"/> of the month (1–4, or -1 for the last one).</summary>
    public int? Nth { get; private init; }

    public DayOfWeek? NthDay { get; private init; }

    /// <summary>Yearly: the month (1–12), with <see cref="MonthDay"/>.</summary>
    public int? Month { get; private init; }

    /// <summary>The next time is counted from when it's done, not from when it was due.</summary>
    public bool WhenDone { get; private init; }

    /// <summary>No occurrences after this day.</summary>
    public DateOnly? Until { get; private init; }

    /// <summary>Occurrences left, the current one included (1: this is the last time).</summary>
    public int? Count { get; private init; }

    public static Recurrence Parse(string? text) =>
        TryParse(text, out var rule, out var problem) ? rule : throw new ArgumentException(problem, nameof(text));

    public static bool TryParse(string? text, out Recurrence rule, out string problem)
    {
        rule = new Recurrence();
        problem = string.Empty;
        try
        {
            rule = Read(text);
            return true;
        }
        catch (FormatException e)
        {
            problem = $"{e.Message} Try \"every day\", \"every weekday\", \"every 2 weeks on Monday, Thursday\", \"every month on the last Friday\" or \"every year on March 5\" (add \"when done\", \"until 2026-12-31\" or \"for 5 times\").";
            return false;
        }
    }

    /// <summary>The same rule with one occurrence fewer left.</summary>
    public Recurrence CountedDown() => Count is { } n ? With(count: n - 1) : this;

    /// <summary>The same rule with one more occurrence left (an occurrence undone).</summary>
    public Recurrence CountedUp() => Count is { } n ? With(count: Math.Min(n + 1, MaxCount)) : this;

    /// <summary>
    /// The rule with the day it falls on written out, taken from <paramref name="first"/> (its first time): "every month"
    /// due Jan 31 is "every month on the 31st" (so after February's 28th it's back on the 31st, and a snooze doesn't move
    /// it); "every week" started on a Friday is "every week on Friday"; "every year" is "every year on March 5". Rules
    /// counted from when they're done follow the completion, and are left as they are.
    /// </summary>
    public Recurrence PinnedTo(DateOnly first)
    {
        if (WhenDone)
        {
            return this;
        }

        return Unit switch
        {
            RepeatUnit.Week when Weekdays.Count == 0 => With(weekdays: [first.DayOfWeek]),
            RepeatUnit.Month when MonthDay is null && Nth is null => With(monthDay: first.Day),
            RepeatUnit.Year when Month is null => With(month: first.Month, monthDay: MonthDay ?? first.Day),
            _ => this,
        };
    }

    /// <summary>
    /// The first day after <paramref name="after"/> it falls on, counting from <paramref name="anchor"/> (its first
    /// day: every 2 weeks means the weeks of the anchor, the 2nd one after, …). Null when the rule has ended (after
    /// <see cref="Until"/>).
    /// </summary>
    public DateOnly? Next(DateOnly anchor, DateOnly after)
    {
        var limit = Unit switch
        {
            RepeatUnit.Day => Interval,
            RepeatUnit.Week => (7 * Interval) + 7,
            RepeatUnit.Month => (31 * Interval) + 31,
            _ => (366 * Interval) + 366,
        };
        for (var day = after.AddDays(1); day <= after.AddDays(limit); day = day.AddDays(1))
        {
            if (Matches(day, anchor))
            {
                return Until is { } until && day > until ? null : day;
            }
        }

        return null;
    }

    /// <summary>The last day before <paramref name="before"/> it falls on (to undo an occurrence), counting from <paramref name="anchor"/>.</summary>
    public DateOnly? Previous(DateOnly anchor, DateOnly before)
    {
        var limit = Unit switch
        {
            RepeatUnit.Day => Interval,
            RepeatUnit.Week => (7 * Interval) + 7,
            RepeatUnit.Month => (31 * Interval) + 31,
            _ => (366 * Interval) + 366,
        };
        for (var day = before.AddDays(-1); day >= before.AddDays(-limit) && day > DateOnly.MinValue; day = day.AddDays(-1))
        {
            if (Matches(day, anchor))
            {
                return day;
            }
        }

        return null;
    }

    private bool Matches(DateOnly day, DateOnly anchor)
    {
        switch (Unit)
        {
            case RepeatUnit.Day:
                return FloorMod(day.DayNumber - anchor.DayNumber, Interval) == 0;
            case RepeatUnit.Week:
                var days = Weekdays.Count > 0 ? Weekdays : [anchor.DayOfWeek];
                // DayNumber 0 (1 Jan 0001) is a Monday: weeks start on Monday.
                return days.Contains(day.DayOfWeek) && FloorMod((day.DayNumber / 7) - (anchor.DayNumber / 7), Interval) == 0;
            case RepeatUnit.Month:
                return FloorMod(((day.Year * 12) + day.Month) - ((anchor.Year * 12) + anchor.Month), Interval) == 0 && DayMatches(day, anchor);
            default:
                return FloorMod(day.Year - anchor.Year, Interval) == 0 && day.Month == (Month ?? anchor.Month) && DayMatches(day, anchor);
        }
    }

    private bool DayMatches(DateOnly day, DateOnly anchor)
    {
        var length = DateTime.DaysInMonth(day.Year, day.Month);
        if (Nth is { } nth && NthDay is { } weekday)
        {
            return day.DayOfWeek == weekday && (nth == -1 ? day.Day + 7 > length : ((day.Day - 1) / 7) + 1 == nth);
        }

        var wanted = MonthDay ?? anchor.Day;
        return day.Day == (wanted == -1 ? length : Math.Min(wanted, length));
    }

    private static int FloorMod(int value, int by) => ((value % by) + by) % by;

    public override string ToString()
    {
        var unit = Unit.ToString().ToLowerInvariant();
        var text = Unit == RepeatUnit.Week && Interval == 1 && Weekdays.SequenceEqual(WorkWeek)
            ? "every weekday"
            : Interval == 1 ? $"every {unit}" : $"every {Interval} {unit}s";
        if (Unit == RepeatUnit.Week && Weekdays.Count > 0 && text != "every weekday")
        {
            text += " on " + string.Join(", ", Weekdays.Select(d => d.ToString()));
        }
        else if (Nth is { } nth && NthDay is { } weekday)
        {
            text += $" on the {(nth == -1 ? "last" : Ordinal(nth))} {weekday}";
        }
        else if (Unit == RepeatUnit.Year && Month is { } month)
        {
            text += $" on {MonthNames[month - 1]} {(MonthDay == -1 ? "last day" : MonthDay?.ToString(CultureInfo.InvariantCulture))}";
        }
        else if (MonthDay is { } monthDay)
        {
            text += monthDay == -1 ? " on the last day" : $" on the {Ordinal(monthDay)}";
        }

        if (WhenDone)
        {
            text += " when done";
        }

        if (Until is { } until)
        {
            text += " until " + until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (Count is { } count)
        {
            text += count == 1 ? " for 1 time" : $" for {count} times";
        }

        return text;
    }

    private static string Ordinal(int n) => n + ((n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch
    {
        1 => "st",
        2 => "nd",
        3 => "rd",
        _ => "th",
    });

    private Recurrence With(int? count = null, IReadOnlyList<DayOfWeek>? weekdays = null, int? monthDay = null, int? month = null) => new()
    {
        Unit = Unit,
        Interval = Interval,
        Weekdays = weekdays ?? Weekdays,
        MonthDay = monthDay ?? MonthDay,
        Nth = Nth,
        NthDay = NthDay,
        Month = month ?? Month,
        WhenDone = WhenDone,
        Until = Until,
        Count = count ?? Count,
    };

    // ---- Reading -------------------------------------------------------------------------

    private static Recurrence Read(string? text)
    {
        var clean = (text ?? string.Empty).Replace("🔁", " ", StringComparison.Ordinal).Replace("\uFE0F", string.Empty, StringComparison.Ordinal)
            .Replace(",", " ", StringComparison.Ordinal).Trim().ToLowerInvariant();
        var words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w is not "and" and not "&").ToList();
        if (words.Count == 0)
        {
            throw new FormatException("Say how it repeats.");
        }

        bool whenDone = false;
        DateOnly? until = null;
        int? count = null;
        while (words.Count > 1)
        {
            if (words[^2] == "when" && words[^1] == "done")
            {
                whenDone = true;
                words.RemoveRange(words.Count - 2, 2);
            }
            else if (words.Count > 2 && words[^2] == "until")
            {
                until = DateOnly.TryParseExact(words[^1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var u)
                    ? u
                    : throw new FormatException($"\"{words[^1]}\" isn't a date (write it like 2026-12-31).");
                words.RemoveRange(words.Count - 2, 2);
            }
            else if (words[^1] is "times" or "time" && int.TryParse(words[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                count = n is >= 1 and <= MaxCount ? n : throw new FormatException($"It can repeat 1 to {MaxCount} times.");
                words.RemoveRange(words.Count - 2, 2);
                if (words.Count > 1 && words[^1] == "for")
                {
                    words.RemoveAt(words.Count - 1);
                }
            }
            else
            {
                break;
            }
        }

        var rule = ReadRule(words);
        return new Recurrence
        {
            Unit = rule.Unit,
            Interval = rule.Interval,
            Weekdays = rule.Weekdays,
            MonthDay = rule.MonthDay,
            Nth = rule.Nth,
            NthDay = rule.NthDay,
            Month = rule.Month,
            WhenDone = whenDone,
            Until = until,
            Count = count,
        };
    }

    private static Recurrence ReadRule(List<string> words)
    {
        var at = 0;
        string Peek() => at < words.Count ? words[at] : string.Empty;
        var interval = 1;
        RepeatUnit? unit = words[0] switch
        {
            "daily" => RepeatUnit.Day,
            "weekly" => RepeatUnit.Week,
            "monthly" => RepeatUnit.Month,
            "yearly" or "annually" => RepeatUnit.Year,
            _ => null,
        };
        var weekdays = new List<DayOfWeek>();
        if (unit is not null)
        {
            at = 1;
        }
        else
        {
            if (words[0] != "every")
            {
                throw new FormatException($"\"{string.Join(' ', words)}\" isn't a repeat rule.");
            }

            at = 1;
            if (Peek() == "other")
            {
                interval = 2;
                at++;
            }
            else if (int.TryParse(Peek(), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                interval = n is >= 1 and <= MaxInterval ? n : throw new FormatException($"Repeat every 1 to {MaxInterval}.");
                at++;
            }

            var word = Peek();
            at++;
            if (word is "weekday" or "weekdays" && interval == 1)
            {
                unit = RepeatUnit.Week;
                weekdays.AddRange(WorkWeek);
            }
            else if (Weekday(word) is { } first)
            {
                // "every Monday", "every Tuesday and Friday"
                unit = RepeatUnit.Week;
                weekdays.Add(first);
                while (Weekday(Peek()) is { } more)
                {
                    weekdays.Add(more);
                    at++;
                }
            }
            else
            {
                unit = word.TrimEnd('s') switch
                {
                    "day" => RepeatUnit.Day,
                    "week" => RepeatUnit.Week,
                    "month" => RepeatUnit.Month,
                    "year" => RepeatUnit.Year,
                    _ => throw new FormatException(word.Length == 0 ? "Every what?" : $"\"{word}\" isn't a day, week, month or year."),
                };
            }
        }

        int? monthDay = null, nth = null, month = null;
        DayOfWeek? nthDay = null;
        if (Peek() == "on" && weekdays.Count == 0)
        {
            at++;
            if (Peek() == "the")
            {
                at++;
            }

            switch (unit)
            {
                case RepeatUnit.Week:
                    while (Weekday(Peek()) is { } day)
                    {
                        weekdays.Add(day);
                        at++;
                    }

                    if (weekdays.Count == 0)
                    {
                        throw new FormatException("On which days of the week?");
                    }

                    break;
                case RepeatUnit.Month:
                    var which = Peek() == "last" ? -1 : DayNumber(Peek());
                    at++;
                    if (which is null)
                    {
                        throw new FormatException("On which day of the month (\"the 15th\", \"the last day\", \"the 2nd Tuesday\")?");
                    }

                    if (Weekday(Peek()) is { } weekday)
                    {
                        nth = which == -1 || which is >= 1 and <= 4 ? which : throw new FormatException("Say the 1st to 4th or the last weekday of the month.");
                        nthDay = weekday;
                        at++;
                    }
                    else
                    {
                        if (Peek() == "day")
                        {
                            at++;
                        }

                        monthDay = which is -1 or (>= 1 and <= 31) ? which : throw new FormatException("A day of the month is 1 to 31.");
                    }

                    break;
                case RepeatUnit.Year:
                    // "March 5", "Mar 5th", "5 March", "the 5th of March"
                    var (m, d) = MonthOf(Peek()) is { } m1 ? (m1, DayNumber(words.ElementAtOrDefault(at + 1) ?? string.Empty))
                        : (MonthOf(words.ElementAtOrDefault(at + 1) == "of" ? words.ElementAtOrDefault(at + 2) ?? string.Empty : words.ElementAtOrDefault(at + 1) ?? string.Empty), DayNumber(Peek()));
                    if (m is null || d is null or < 1 || d > DateTime.DaysInMonth(2024, m.Value))
                    {
                        throw new FormatException("On which day of the year (\"March 5\")?");
                    }

                    at += words.ElementAtOrDefault(at + 1) == "of" ? 3 : 2;
                    month = m;
                    monthDay = d;
                    break;
                default:
                    throw new FormatException("\"every day\" has no \"on\".");
            }
        }

        if (at < words.Count)
        {
            throw new FormatException($"\"{string.Join(' ', words.Skip(at))}\" isn't understood.");
        }

        return new Recurrence
        {
            Unit = unit!.Value,
            Interval = interval,
            Weekdays = weekdays.Distinct().OrderBy(d => ((int)d + 6) % 7).ToList(),
            MonthDay = monthDay,
            Nth = nth,
            NthDay = nthDay,
            Month = month,
        };
    }

    private static DayOfWeek? Weekday(string word)
    {
        var w = word.TrimEnd('s');
        if (w.Length < 2)
        {
            return null;
        }

        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            var name = day.ToString().ToLowerInvariant();
            if (w == name || (w.Length >= 3 && name.StartsWith(w, StringComparison.Ordinal) && w.Length <= name.Length) || (w is "tu" or "th") && name.StartsWith(w, StringComparison.Ordinal))
            {
                return day;
            }
        }

        return null;
    }

    private static int? MonthOf(string word)
    {
        if (word.Length < 3)
        {
            return null;
        }

        for (var i = 0; i < 12; i++)
        {
            if (MonthNames[i].StartsWith(word, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return null;
    }

    /// <summary>"15", "15th", "1st", "2nd", "3rd".</summary>
    private static int? DayNumber(string word)
    {
        var digits = word.Length > 2 && word[^2..] is "st" or "nd" or "rd" or "th" ? word[..^2] : word;
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }
}
