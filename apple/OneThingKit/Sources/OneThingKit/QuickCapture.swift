import Foundation

public struct QuickCapture: Equatable {
    public let title: String
    public let priority: Priority
    public let nextActionAt: Date?
    public let deadline: Date?
}

/// One-line capture, same syntax as the Windows sidebar and web: `!`/`!!`/`!low` priority,
/// `@15m`/`@2h`/`@1d`/`@tomorrow` defer, `due:today`/`due:tomorrow`/`due:3d`/`due:2026-02-01` deadline.
public enum QuickCaptureParser {
    static let morningHour = 9
    static let endOfDayHour = 17

    public static func parse(_ input: String, now: Date, timeZone: TimeZone = .current) throws -> QuickCapture {
        var priority = Priority.normal
        var nextAction: Date?
        var deadline: Date?
        var words: [String] = []
        for token in input.split(whereSeparator: { $0 == " " }).map(String.init) where !token.isEmpty {
            if let p = parsePriority(token) {
                priority = p
            } else if let at = parseDefer(token, now: now, timeZone: timeZone) {
                nextAction = at
            } else if let due = parseDue(token, now: now, timeZone: timeZone) {
                deadline = due
            } else {
                words.append(token)
            }
        }
        guard !words.isEmpty else { throw BoardError.invalid("Type a task title.") }
        return QuickCapture(title: words.joined(separator: " "), priority: priority, nextActionAt: nextAction, deadline: deadline)
    }

    static func parsePriority(_ token: String) -> Priority? {
        switch token.lowercased() {
        case "!", "!high": return .high
        case "!!", "!!!", "!critical", "!urgent": return .critical
        case "!low": return .low
        case "!normal": return .normal
        default: return nil
        }
    }

    static func parseDefer(_ token: String, now: Date, timeZone: TimeZone) -> Date? {
        guard token.hasPrefix("@") else { return nil }
        let value = token.dropFirst().lowercased()
        if value == "tomorrow" { return tomorrowMorning(now: now, timeZone: timeZone) }
        return parseSpan(value).map { now.addingTimeInterval($0) }
    }

    /// Next 9:00. Before 4:00 people still mean "when I wake up", i.e. this morning (same as C#).
    public static func tomorrowMorning(now: Date, timeZone: TimeZone) -> Date {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        let hour = calendar.component(.hour, from: now)
        return localTime(now: now, timeZone: timeZone, addDays: hour < 4 ? 0 : 1, hour: morningHour)
    }

    static func parseDue(_ token: String, now: Date, timeZone: TimeZone) -> Date? {
        guard token.lowercased().hasPrefix("due:") else { return nil }
        let value = String(token.dropFirst(4)).lowercased()
        switch value {
        case "today":
            let endOfDay = localTime(now: now, timeZone: timeZone, addDays: 0, hour: endOfDayHour)
            return endOfDay > now ? endOfDay : localTime(now: now, timeZone: timeZone, addDays: 0, hour: 23).addingTimeInterval(59 * 60)
        case "tomorrow": return localTime(now: now, timeZone: timeZone, addDays: 1, hour: endOfDayHour)
        default: break
        }
        if value.hasSuffix("d"), let days = strictInt(value.dropLast()), days > 0, days < 1000 {
            return localTime(now: now, timeZone: timeZone, addDays: days, hour: endOfDayHour)
        }
        let parts = value.split(separator: "-")
        if parts.count == 3, parts[0].count == 4, parts[1].count == 2, parts[2].count == 2,
           let y = digitsInt(parts[0]), let m = digitsInt(parts[1]), let d = digitsInt(parts[2]) {
            var calendar = Calendar(identifier: .gregorian)
            calendar.timeZone = timeZone
            let components = DateComponents(year: y, month: m, day: d, hour: endOfDayHour)
            // Reject dates that Calendar would roll over (e.g. month 13 or Feb 30), matching C#.
            guard let date = calendar.date(from: components),
                  calendar.component(.year, from: date) == y, calendar.component(.month, from: date) == m, calendar.component(.day, from: date) == d
            else { return nil }
            return date
        }
        return nil
    }

    /// Digits only: no sign, no leading zero (C# uses the same pattern).
    static func strictInt<S: StringProtocol>(_ text: S) -> Int? {
        guard let first = text.first, first != "0" else { return nil }
        return digitsInt(text)
    }

    /// ASCII digits only (leading zeros allowed, e.g. "02" in dates); rejects "+5" and "-1".
    static func digitsInt<S: StringProtocol>(_ text: S) -> Int? {
        guard !text.isEmpty, text.allSatisfy({ $0.isASCII && $0.isNumber }) else { return nil }
        return Int(text)
    }

    static func parseSpan(_ value: String) -> TimeInterval? {
        guard let unit = value.last, "mhd".contains(unit), let amount = strictInt(value.dropLast()), amount < 10_000 else { return nil }
        switch unit {
        case "m": return TimeInterval(amount * 60)
        case "h": return TimeInterval(amount * 3600)
        default: return TimeInterval(amount * 86_400)
        }
    }

    static func localTime(now: Date, timeZone: TimeZone, addDays: Int, hour: Int) -> Date {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        let startOfDay = calendar.startOfDay(for: now)
        let day = calendar.date(byAdding: .day, value: addDays, to: startOfDay)!
        return calendar.date(bySettingHour: hour, minute: 0, second: 0, of: day)!
    }
}

public enum RelativeTime {
    private static let months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"]

    /// Compact relative time: "in 5m", "in 1h 30m", "3d ago", or "Feb 20" (same rules as C# and JS).
    public static func format(_ target: Date, now: Date) -> String {
        let delta = target.timeIntervalSince(now)
        let minutes = Int((abs(delta) / 60).rounded(.toNearestOrAwayFromZero))
        if minutes < 1 { return "now" }
        let text: String
        if minutes < 60 {
            text = "\(minutes)m"
        } else if minutes < 180 {
            text = minutes % 60 == 0 ? "\(minutes / 60)h" : "\(minutes / 60)h \(minutes % 60)m"
        } else if minutes < 24 * 60 {
            text = "\(minutes / 60)h"
        } else if minutes < 7 * 24 * 60 {
            text = "\(minutes / (24 * 60))d"
        } else {
            var calendar = Calendar(identifier: .gregorian)
            calendar.timeZone = TimeZone(identifier: "UTC")!
            let c = calendar.dateComponents([.month, .day], from: target)
            return "\(months[(c.month ?? 1) - 1]) \(c.day ?? 1)"
        }
        return delta > 0 ? "in \(text)" : "\(text) ago"
    }
}
