import Foundation

/// A calendar day (no time, no zone): what a repeat rule counts in.
public struct LocalDay: Comparable, Hashable, CustomStringConvertible {
    public let year: Int
    public let month: Int
    public let day: Int

    public init(year: Int, month: Int, day: Int) {
        self.year = year
        self.month = month
        self.day = day
    }

    /// "2026-01-05".
    public init?(_ text: String) {
        let parts = text.split(separator: "-").map { Int($0) }
        guard parts.count == 3, let y = parts[0], let m = parts[1], let d = parts[2], (1...12).contains(m), d >= 1, d <= LocalDay.length(y, m) else { return nil }
        self.init(year: y, month: m, day: d)
    }

    /// Days since 1 Jan 1970 (that day is 0).
    public var epochDay: Int {
        // Howard Hinnant's days_from_civil.
        let y = month <= 2 ? year - 1 : year
        let era = (y >= 0 ? y : y - 399) / 400
        let yoe = y - era * 400
        let mp = (month + 9) % 12
        let doy = (153 * mp + 2) / 5 + day - 1
        let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy
        return era * 146_097 + doe - 719_468
    }

    public init(epochDay: Int) {
        // Howard Hinnant's civil_from_days.
        let z = epochDay + 719_468
        let era = (z >= 0 ? z : z - 146_096) / 146_097
        let doe = z - era * 146_097
        let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146_096) / 365
        let doy = doe - (365 * yoe + yoe / 4 - yoe / 100)
        let mp = (5 * doy + 2) / 153
        let d = doy - (153 * mp + 2) / 5 + 1
        let m = mp < 10 ? mp + 3 : mp - 9
        self.init(year: yoe + era * 400 + (m <= 2 ? 1 : 0), month: m, day: d)
    }

    /// 1 = Monday … 7 = Sunday.
    public var weekday: Int { Recurrence.floorMod(epochDay + 3, 7) + 1 }

    public var monthLength: Int { LocalDay.length(year, month) }

    public func adding(days: Int) -> LocalDay { LocalDay(epochDay: epochDay + days) }

    public var description: String { String(format: "%04d-%02d-%02d", year, month, day) }

    public static func < (a: LocalDay, b: LocalDay) -> Bool { a.epochDay < b.epochDay }

    static func length(_ year: Int, _ month: Int) -> Int {
        switch month {
        case 2: return (year % 4 == 0 && year % 100 != 0) || year % 400 == 0 ? 29 : 28
        case 4, 6, 9, 11: return 30
        default: return 31
        }
    }
}

public enum RepeatUnit: String {
    case day, week, month, year
}

/// How a task repeats, written like the Obsidian Tasks plugin's 🔁 rules ("every weekday", "every 2 weeks on Monday,
/// Thursday", "every month on the last Friday", "every year on March 5", + "when done", "until 2026-12-31",
/// "for 5 times"). Mirrors TodoTracker.Core.Recurrence (C#) and Recurrence.kt; tests/fixtures/repeat.json keeps them
/// the same. `description` is the canonical text.
public struct Recurrence: Equatable, CustomStringConvertible {
    public static let maxInterval = 999
    public static let maxCount = 9999
    static let workWeek = [1, 2, 3, 4, 5]
    static let dayNames = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"]
    static let monthNames = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"]

    public var unit: RepeatUnit
    public var interval = 1
    /// Weekly: the days it falls on (1 = Monday … 7 = Sunday), Monday first; none: the day it started on.
    public var weekdays: [Int] = []
    /// Monthly or yearly: the day of the month (1–31; a short month uses its last day), or -1 for the last day.
    public var monthDay: Int?
    /// Monthly: which `nthDay` of the month (1–4, or -1 for the last one).
    public var nth: Int?
    public var nthDay: Int?
    /// Yearly: the month (1–12), with `monthDay`.
    public var month: Int?
    public var whenDone = false
    public var until: LocalDay?
    /// Occurrences left, the current one included (1: this is the last time).
    public var count: Int?

    public struct RuleError: Error, LocalizedError {
        public let message: String
        public var errorDescription: String? { message }
    }

    public func countedDown() -> Recurrence {
        var copy = self
        if let count { copy.count = count - 1 }
        return copy
    }

    public func countedUp() -> Recurrence {
        var copy = self
        if let count { copy.count = min(count + 1, Recurrence.maxCount) }
        return copy
    }

    /// The first day after `after` it falls on, counting from `anchor`; nil when the rule has ended (`until`).
    public func next(anchor: LocalDay, after: LocalDay) -> LocalDay? {
        for offset in 1...limit {
            let day = after.adding(days: offset)
            if matches(day, anchor) {
                if let until, day > until { return nil }
                return day
            }
        }
        return nil
    }

    /// The last day before `before` it falls on (to undo an occurrence), counting from `anchor`.
    public func previous(anchor: LocalDay, before: LocalDay) -> LocalDay? {
        for offset in 1...limit {
            let day = before.adding(days: -offset)
            if matches(day, anchor) { return day }
        }
        return nil
    }

    private var limit: Int {
        switch unit {
        case .day: return interval
        case .week: return 7 * interval + 7
        case .month: return 31 * interval + 31
        case .year: return 366 * interval + 366
        }
    }

    static func floorMod(_ value: Int, _ by: Int) -> Int { ((value % by) + by) % by }

    static func floorDiv(_ value: Int, _ by: Int) -> Int { (value - floorMod(value, by)) / by }

    private func matches(_ day: LocalDay, _ anchor: LocalDay) -> Bool {
        switch unit {
        case .day:
            return Recurrence.floorMod(day.epochDay - anchor.epochDay, interval) == 0
        case .week:
            let days = weekdays.isEmpty ? [anchor.weekday] : weekdays
            // Epoch day 0 (1 Jan 1970) is a Thursday: +3 makes weeks start on Monday.
            let weeks = Recurrence.floorDiv(day.epochDay + 3, 7) - Recurrence.floorDiv(anchor.epochDay + 3, 7)
            return days.contains(day.weekday) && Recurrence.floorMod(weeks, interval) == 0
        case .month:
            return Recurrence.floorMod((day.year * 12 + day.month) - (anchor.year * 12 + anchor.month), interval) == 0 && dayMatches(day, anchor)
        case .year:
            return Recurrence.floorMod(day.year - anchor.year, interval) == 0 && day.month == (month ?? anchor.month) && dayMatches(day, anchor)
        }
    }

    private func dayMatches(_ day: LocalDay, _ anchor: LocalDay) -> Bool {
        let length = day.monthLength
        if let nth, let nthDay {
            return day.weekday == nthDay && (nth == -1 ? day.day + 7 > length : (day.day - 1) / 7 + 1 == nth)
        }
        let wanted = monthDay ?? anchor.day
        return day.day == (wanted == -1 ? length : min(wanted, length))
    }

    public var description: String {
        let isWeekday = unit == .week && interval == 1 && weekdays == Recurrence.workWeek
        var text = isWeekday ? "every weekday" : interval == 1 ? "every \(unit.rawValue)" : "every \(interval) \(unit.rawValue)s"
        if unit == .week && !weekdays.isEmpty && !isWeekday {
            text += " on " + weekdays.map { Recurrence.dayNames[$0 - 1] }.joined(separator: ", ")
        } else if let nth, let nthDay {
            text += " on the \(nth == -1 ? "last" : Recurrence.ordinal(nth)) \(Recurrence.dayNames[nthDay - 1])"
        } else if unit == .year, let month {
            text += " on \(Recurrence.monthNames[month - 1]) \(monthDay == -1 ? "last day" : String(monthDay ?? 1))"
        } else if let monthDay {
            text += monthDay == -1 ? " on the last day" : " on the \(Recurrence.ordinal(monthDay))"
        }
        if whenDone { text += " when done" }
        if let until { text += " until \(until)" }
        if let count { text += count == 1 ? " for 1 time" : " for \(count) times" }
        return text
    }

    static func ordinal(_ n: Int) -> String {
        if (11...13).contains(n % 100) { return "\(n)th" }
        switch n % 10 {
        case 1: return "\(n)st"
        case 2: return "\(n)nd"
        case 3: return "\(n)rd"
        default: return "\(n)th"
        }
    }

    // MARK: Reading

    /// The rule `text` means; throws `RuleError` (with examples) when it isn't understood.
    public static func parse(_ text: String?) throws -> Recurrence {
        do {
            return try read(text)
        } catch let error as RuleError {
            throw RuleError(message: "\(error.message) Try \"every day\", \"every weekday\", \"every 2 weeks on Monday, Thursday\", \"every month on the last Friday\" or \"every year on March 5\" (add \"when done\", \"until 2026-12-31\" or \"for 5 times\").")
        }
    }

    /// A rule written by hand (or by another app) that isn't understood is left out.
    public static func parseOrNil(_ text: String?) -> Recurrence? { try? read(text) }

    private static func number(_ word: String) -> Int? {
        guard !word.isEmpty, word.allSatisfy({ ("0"..."9").contains($0) }) else { return nil }
        return Int(word)
    }

    private static func read(_ text: String?) throws -> Recurrence {
        let clean = (text ?? "").replacingOccurrences(of: "🔁", with: " ").replacingOccurrences(of: "\u{FE0F}", with: "")
            .replacingOccurrences(of: ",", with: " ").lowercased()
        var words = clean.split(whereSeparator: { $0 == " " || $0 == "\t" || $0 == "\n" || $0 == "\r" }).map(String.init).filter { $0 != "and" && $0 != "&" }
        guard !words.isEmpty else { throw RuleError(message: "Say how it repeats.") }
        var whenDone = false
        var until: LocalDay?
        var count: Int?
        while words.count > 1 {
            let last = words[words.count - 1]
            let before = words[words.count - 2]
            if before == "when" && last == "done" {
                whenDone = true
                words.removeLast(2)
            } else if words.count > 2 && before == "until" {
                guard let day = LocalDay(last), last.count == 10 else { throw RuleError(message: "\"\(last)\" isn't a date (write it like 2026-12-31).") }
                until = day
                words.removeLast(2)
            } else if (last == "times" || last == "time"), let n = number(before) {
                guard (1...maxCount).contains(n) else { throw RuleError(message: "It can repeat 1 to \(maxCount) times.") }
                count = n
                words.removeLast(2)
                if words.count > 1 && words[words.count - 1] == "for" { words.removeLast() }
            } else {
                break
            }
        }
        var rule = try readRule(words)
        rule.whenDone = whenDone
        rule.until = until
        rule.count = count
        return rule
    }

    private static func readRule(_ words: [String]) throws -> Recurrence {
        var at = 1
        func peek(_ offset: Int = 0) -> String { at + offset < words.count ? words[at + offset] : "" }
        var interval = 1
        var weekdays: [Int] = []
        let unit: RepeatUnit
        switch words[0] {
        case "daily": unit = .day
        case "weekly": unit = .week
        case "monthly": unit = .month
        case "yearly", "annually": unit = .year
        default:
            guard words[0] == "every" else { throw RuleError(message: "\"\(words.joined(separator: " "))\" isn't a repeat rule.") }
            if peek() == "other" {
                interval = 2
                at += 1
            } else if let n = number(peek()) {
                guard (1...maxInterval).contains(n) else { throw RuleError(message: "Repeat every 1 to \(maxInterval).") }
                interval = n
                at += 1
            }
            let word = peek()
            at += 1
            if (word == "weekday" || word == "weekdays") && interval == 1 {
                unit = .week
                weekdays = workWeek
            } else if let first = weekday(word) {
                // "every Monday", "every Tuesday and Friday"
                unit = .week
                weekdays.append(first)
                while let more = weekday(peek()) {
                    weekdays.append(more)
                    at += 1
                }
            } else {
                var singular = word
                while singular.hasSuffix("s") { singular.removeLast() }
                switch singular {
                case "day": unit = .day
                case "week": unit = .week
                case "month": unit = .month
                case "year": unit = .year
                default: throw RuleError(message: word.isEmpty ? "Every what?" : "\"\(word)\" isn't a day, week, month or year.")
                }
            }
        }

        var monthDay: Int?
        var nth: Int?
        var nthDay: Int?
        var month: Int?
        if peek() == "on" && weekdays.isEmpty {
            at += 1
            if peek() == "the" { at += 1 }
            switch unit {
            case .week:
                while let day = weekday(peek()) {
                    weekdays.append(day)
                    at += 1
                }
                if weekdays.isEmpty { throw RuleError(message: "On which days of the week?") }
            case .month:
                let which = peek() == "last" ? -1 : dayNumber(peek())
                at += 1
                guard let which else { throw RuleError(message: "On which day of the month (\"the 15th\", \"the last day\", \"the 2nd Tuesday\")?") }
                if let day = weekday(peek()) {
                    guard which == -1 || (1...4).contains(which) else { throw RuleError(message: "Say the 1st to 4th or the last weekday of the month.") }
                    nth = which
                    nthDay = day
                    at += 1
                } else {
                    if peek() == "day" { at += 1 }
                    guard which == -1 || (1...31).contains(which) else { throw RuleError(message: "A day of the month is 1 to 31.") }
                    monthDay = which
                }
            case .year:
                // "March 5", "Mar 5th", "5 March", "the 5th of March"
                let of = peek(1) == "of"
                let named = monthOf(peek())
                let m = named ?? monthOf(of ? peek(2) : peek(1))
                let d = named != nil ? dayNumber(peek(1)) : dayNumber(peek())
                guard let m, let d, d >= 1, d <= LocalDay.length(2024, m) else { throw RuleError(message: "On which day of the year (\"March 5\")?") }
                at += of ? 3 : 2
                month = m
                monthDay = d
            case .day:
                throw RuleError(message: "\"every day\" has no \"on\".")
            }
        }
        if at < words.count { throw RuleError(message: "\"\(words[at...].joined(separator: " "))\" isn't understood.") }
        var unique: [Int] = []
        for day in weekdays where !unique.contains(day) { unique.append(day) }
        return Recurrence(unit: unit, interval: interval, weekdays: unique.sorted(), monthDay: monthDay, nth: nth, nthDay: nthDay, month: month)
    }

    private static func weekday(_ word: String) -> Int? {
        var w = word
        while w.hasSuffix("s") { w.removeLast() }
        guard w.count >= 2 else { return nil }
        for (i, name) in dayNames.enumerated() {
            let lower = name.lowercased()
            if w == lower || (w.count >= 3 && lower.hasPrefix(w)) || ((w == "tu" || w == "th") && lower.hasPrefix(w)) { return i + 1 }
        }
        return nil
    }

    private static func monthOf(_ word: String) -> Int? {
        guard word.count >= 3 else { return nil }
        return monthNames.firstIndex(where: { $0.lowercased().hasPrefix(word) }).map { $0 + 1 }
    }

    /// "15", "15th", "1st", "2nd", "3rd".
    private static func dayNumber(_ word: String) -> Int? {
        let digits = word.count > 2 && ["st", "nd", "rd", "th"].contains(String(word.suffix(2))) ? String(word.dropLast(2)) : word
        return number(digits)
    }
}
