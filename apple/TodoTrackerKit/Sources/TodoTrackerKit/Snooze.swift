import Foundation

/// A snooze the apps offer: `id` is the same in every app (C# Snooze.Choices, the web app, Swift).
public struct SnoozeChoice: Equatable, Identifiable, Sendable {
    public let id: String
    public let label: String
    public let at: Date
}

/// When a snoozed task comes back: the quick choices every app offers, and how a time is said.
/// Mirrors TodoTracker.Core.Snooze; tests/fixtures/snooze.json checks both. (Typed rules like "fri 14:30" are read by
/// the server; here a date picker does that job.)
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
