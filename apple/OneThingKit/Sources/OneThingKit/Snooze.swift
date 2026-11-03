import Foundation

/// A snooze the apps offer: `id` is the same in every app (C# Snooze.Choices, the web app, Swift).
public struct SnoozeChoice: Equatable, Identifiable, Sendable {
    public let id: String
    public let label: String
    public let at: Date
}

/// When a snoozed task comes back: the quick choices every app offers, how a time is said, and a rule typed after @ in
/// quick capture, read like people talk: "3d", "2 weeks", "fri 14:30", "next week", "weekend", "tonight", "9am",
/// "2026-03-01 14:00". Days come back in the morning (9:00) unless a time is given.
/// Mirrors OneThing.Core.Snooze; tests/fixtures/snooze.json checks both.
public enum Snooze {
    public static let morningHour = 9
    public static let eveningHour = 18
    /// "This evening" is offered until this hour.
    static let eveningOfferedUntil = 17

    public static func choices(now: Date, timeZone: TimeZone) -> [SnoozeChoice] {
        let calendar = calendar(timeZone)
        let today = calendar.startOfDay(for: now)
        var choices = [
            SnoozeChoice(id: "15m", label: "In 15 minutes", at: now.addingTimeInterval(15 * 60)),
            SnoozeChoice(id: "1h", label: "In an hour", at: now.addingTimeInterval(3600)),
            SnoozeChoice(id: "3h", label: "In 3 hours", at: now.addingTimeInterval(3 * 3600)),
        ]
        if calendar.component(.hour, from: now) < eveningOfferedUntil {
            choices.append(SnoozeChoice(id: "evening", label: "This evening", at: at(today, hour: eveningHour, calendar)))
        }
        choices.append(SnoozeChoice(id: "tomorrow", label: "Tomorrow morning", at: QuickCaptureParser.tomorrowMorning(now: now, timeZone: timeZone)))
        choices.append(SnoozeChoice(id: "2d", label: "In 2 days", at: at(add(.day, 2, to: today, calendar), hour: morningHour, calendar)))
        choices.append(SnoozeChoice(id: "monday", label: "Next Monday", at: at(nextMonday(after: today, calendar), hour: morningHour, calendar)))
        choices.append(SnoozeChoice(id: "week", label: "In a week", at: at(add(.day, 7, to: today, calendar), hour: morningHour, calendar)))
        choices.append(SnoozeChoice(id: "month", label: "In a month", at: at(add(.month, 1, to: today, calendar), hour: morningHour, calendar)))
        return choices
    }

    /// "today 17:00", "tomorrow 9:00", "Fri 14:30", "Mon 12 Jan, 9:00" (and the year when it's another one).
    public static func describe(_ date: Date, now: Date, timeZone: TimeZone) -> String {
        let calendar = calendar(timeZone)
        let parts = calendar.dateComponents([.year, .month, .day, .hour, .minute, .weekday], from: date)
        let time = "\(parts.hour!):\(String(format: "%02d", parts.minute!))"
        let days = calendar.dateComponents([.day], from: calendar.startOfDay(for: now), to: calendar.startOfDay(for: date)).day ?? 0
        let weekday = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"][parts.weekday! - 1]
        switch days {
        case 0: return "today \(time)"
        case 1: return "tomorrow \(time)"
        case 2..<7: return "\(weekday) \(time)"
        default:
            let month = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"][parts.month! - 1]
            let year = parts.year == calendar.component(.year, from: now) ? "" : " \(parts.year!)"
            return "\(weekday) \(parts.day!) \(month)\(year), \(time)"
        }
    }

    // MARK: Typed rules

    /// The time a typed rule means, or nil when it isn't understood (or is already past).
    public static func parse(_ rule: String?, now: Date, timeZone: TimeZone) -> Date? {
        guard let at = read(rule, now: now, timeZone: timeZone), at > now else { return nil }
        return at
    }

    /// Why a rule can't be used: it's already past, or it isn't understood (with examples).
    public static func hint(_ rule: String?, now: Date, timeZone: TimeZone) -> String {
        let text = (rule ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        if let at = read(rule, now: now, timeZone: timeZone), at <= now {
            return "\"\(text)\" is already past. Pick a later time (e.g. tomorrow, or next week)."
        }
        return "Couldn't tell when \"\(text)\" is. Try 3d, 2 weeks, fri 14:00, next week, weekend, tonight, 9am or 2026-03-01."
    }

    private static let shortUnits: Set<String> = ["m", "min", "mins", "minute", "minutes", "h", "hr", "hrs", "hour", "hours"]
    private static let longUnits: Set<String> = ["d", "day", "days", "w", "week", "weeks", "mo", "month", "months"]

    /// What a rule says, even when that's already past (nil: not understood).
    private static func read(_ rule: String?, now: Date, timeZone: TimeZone) -> Date? {
        var text = (rule ?? "").lowercased().split(whereSeparator: { $0.isWhitespace }).joined(separator: " ")
        if text.hasPrefix("in ") { text = String(text.dropFirst(3)) }
        let leadingAt = text.hasPrefix("at ")
        if leadingAt { text = String(text.dropFirst(3)) }
        if text.isEmpty { return nil }

        // Spans count from now (as @3d always has): 45m, 2h, 3d, 2 weeks, 1 month.
        if case let (amount, unit)? = span(text, maxDigits: 4, units: shortUnits), amount > 0, amount < 10_000 {
            return now.addingTimeInterval(TimeInterval(amount) * (unit.hasPrefix("h") ? 3600 : 60))
        }
        if case let (count, unit)? = span(text, maxDigits: 3, units: longUnits), count > 0, count < 1000 {
            switch unit {
            case "d", "day", "days": return now.addingTimeInterval(TimeInterval(count) * 86_400)
            case "w", "week", "weeks": return now.addingTimeInterval(TimeInterval(7 * count) * 86_400)
            default:
                // As C#'s DateTimeOffset.AddMonths on the clock now is read in (UTC).
                var utc = Calendar(identifier: .gregorian)
                utc.timeZone = TimeZone(identifier: "UTC")!
                return utc.date(byAdding: .month, value: count, to: now)
            }
        }

        // "<day> <time>", "<day> at <time>", "<day>", or "<time>".
        var dayPart: String? = text
        var time: (hour: Int, minute: Int)?
        let atWord = text.range(of: " at ", options: .backwards)
        let lastSpace = text.lastIndex(of: " ")
        if let whole = leadingAt ? atTime(text, evening: false) : readTime(text) {
            time = whole
            dayPart = nil
        } else if let atWord, atWord.lowerBound > text.startIndex {
            let day = String(text[..<atWord.lowerBound])
            dayPart = day
            time = atTime(String(text[atWord.upperBound...]), evening: day == "tonight" || day == "this evening" || day == "evening")
            if time == nil { return nil }
        } else if let lastSpace, lastSpace > text.startIndex, let tail = readTime(String(text[text.index(after: lastSpace)...])) {
            time = tail
            dayPart = String(text[..<lastSpace])
        }

        let calendar = calendar(timeZone)
        let parts = calendar.dateComponents([.year, .month, .day], from: now)
        let today = LocalDay(year: parts.year!, month: parts.month!, day: parts.day!)
        guard let dayPart else {
            // Just a time: today, or tomorrow when it's already past.
            let t = time!
            let result = at(today, t.hour, t.minute, calendar)
            return result <= now ? at(today.adding(days: 1), t.hour, t.minute, calendar) : result
        }
        guard let day = day(dayPart, today: today) else { return nil }

        // "mon 14:00" on a Monday morning means this afternoon (just "mon" means next week's).
        if let given = time, day.weekday, day.date == today.adding(days: 7), at(today, given.hour, given.minute, calendar) > now {
            return at(today, given.hour, given.minute, calendar)
        }
        return at(day.date, time?.hour ?? (day.evening ? eveningHour : morningHour), time?.minute ?? 0, calendar)
    }

    /// "45m", "2 weeks": the number (1 to `maxDigits` ASCII digits) and the unit after it (one space allowed between).
    private static func span(_ text: String, maxDigits: Int, units: Set<String>) -> (Int, String)? {
        let digits = text.prefix { $0.isASCII && $0.isNumber }
        guard !digits.isEmpty, digits.count <= maxDigits, let amount = Int(digits) else { return nil }
        var unit = text.dropFirst(digits.count)
        if unit.hasPrefix(" ") { unit = unit.dropFirst() }
        return units.contains(String(unit)) ? (amount, String(unit)) : nil
    }

    /// A time after "at", the way people say it: a bare hour is a time ("at 9"); without am/pm, 1 to 7 o'clock is the
    /// afternoon ("tomorrow at 3" isn't 3 in the night), and in the evening every hour is ("tonight at 9" is 21:00).
    private static func atTime(_ text: String, evening: Bool) -> (hour: Int, minute: Int)? {
        guard let time = readTime(text, bareHour: true) else { return nil }
        if text.hasSuffix("am") || text.hasSuffix("pm") { return time }
        let afternoon = evening ? (1...11).contains(time.hour) : !text.contains(":") && (1...7).contains(time.hour)
        return afternoon ? (hour: time.hour + 12, minute: time.minute) : time
    }

    /// The day a word means, whether it's "tonight", and whether it's a weekday's name; relative to `today`.
    private static func day(_ text: String, today: LocalDay) -> (date: LocalDay, evening: Bool, weekday: Bool)? {
        switch text {
        case "today": return (today, false, false)
        case "tonight", "this evening", "evening": return (today, true, false)
        case "tomorrow", "tmrw": return (today.adding(days: 1), false, false)
        case "weekend", "this weekend", "the weekend": return (next(today, weekday: 6), false, false)
        case "next week": return (next(today, weekday: 1), false, false)
        case "next month":
            return (today.month == 12 ? LocalDay(year: today.year + 1, month: 1, day: 1) : LocalDay(year: today.year, month: today.month + 1, day: 1), false, false)
        default: break
        }
        let name = text.hasPrefix("next ") ? String(text.dropFirst(5)) : text
        if let weekday = weekday(name) { return (next(today, weekday: weekday), false, name == text) }
        guard let date = isoDay(text) else { return nil }
        return (date, false, false)
    }

    private static let weekdayNames = ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"]

    /// 1 = Monday … 7 = Sunday, from a day's name or its first three letters or more.
    private static func weekday(_ name: String) -> Int? {
        for (index, full) in weekdayNames.enumerated() where name == full || (name.count >= 3 && full.hasPrefix(name)) {
            return index + 1
        }
        return nil
    }

    /// "2026-03-01" exactly (four, two and two digits), a real day.
    private static func isoDay(_ text: String) -> LocalDay? {
        let chars = Array(text)
        guard chars.count == 10, chars[4] == "-", chars[7] == "-",
              chars.enumerated().allSatisfy({ $0.offset == 4 || $0.offset == 7 || ($0.element.isASCII && $0.element.isNumber) }),
              let day = LocalDay(text), day.year >= 1 else { return nil }
        return day
    }

    /// "14:30", "9:00", "2pm", "9am", "9:30pm" (and with `bareHour`, "9").
    private static func readTime(_ text: String, bareHour: Bool = false) -> (hour: Int, minute: Int)? {
        var rest = Substring(text)
        let hourDigits = rest.prefix { $0.isASCII && $0.isNumber }
        guard !hourDigits.isEmpty, hourDigits.count <= 2, var hour = Int(hourDigits) else { return nil }
        rest = rest.dropFirst(hourDigits.count)
        var minute: Int?
        if rest.hasPrefix(":") {
            let minuteDigits = rest.dropFirst().prefix(2)
            guard minuteDigits.count == 2, minuteDigits.allSatisfy({ $0.isASCII && $0.isNumber }) else { return nil }
            minute = Int(minuteDigits)
            rest = rest.dropFirst(3)
        }
        if rest.hasPrefix(" ") { rest = rest.dropFirst() }
        let half = String(rest)
        guard half.isEmpty || half == "am" || half == "pm" else { return nil }
        if !half.isEmpty {
            guard (1...12).contains(hour) else { return nil }
            hour = hour % 12 + (half == "pm" ? 12 : 0)
        } else if minute == nil && !bareHour {
            // A bare number isn't a time ("3" could be anything).
            return nil
        }
        let m = minute ?? 0
        guard hour < 24, m < 60 else { return nil }
        return (hour: hour, minute: m)
    }

    /// The next such weekday (1 = Monday … 7 = Sunday) after today (a week from today when today is that day).
    private static func next(_ today: LocalDay, weekday: Int) -> LocalDay {
        let ahead = (weekday - today.weekday + 7) % 7
        return today.adding(days: ahead == 0 ? 7 : ahead)
    }

    /// That day at a wall-clock time in the calendar's time zone.
    private static func at(_ day: LocalDay, _ hour: Int, _ minute: Int, _ calendar: Calendar) -> Date {
        calendar.date(from: DateComponents(year: day.year, month: day.month, day: day.day, hour: hour, minute: minute))!
    }

    private static func calendar(_ timeZone: TimeZone) -> Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        return calendar
    }

    private static func add(_ component: Calendar.Component, _ value: Int, to day: Date, _ calendar: Calendar) -> Date {
        calendar.date(byAdding: component, value: value, to: day)!
    }

    /// The next Monday (a week from today when today is Monday).
    private static func nextMonday(after today: Date, _ calendar: Calendar) -> Date {
        let weekday = calendar.component(.weekday, from: today) // 1 = Sunday
        let ahead = (2 - weekday + 7) % 7
        return add(.day, ahead == 0 ? 7 : ahead, to: today, calendar)
    }

    /// That day at a wall-clock hour in the calendar's time zone.
    private static func at(_ day: Date, hour: Int, _ calendar: Calendar) -> Date {
        var parts = calendar.dateComponents([.year, .month, .day], from: day)
        parts.hour = hour
        parts.minute = 0
        return calendar.date(from: parts)!
    }
}
