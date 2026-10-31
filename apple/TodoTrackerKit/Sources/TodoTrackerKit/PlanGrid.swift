import Foundation

/// How the Plan view looks: a day or a week, work hours or the whole day, and what work hours and work days are
/// (minutes after midnight; days 1 = Monday … 7 = Sunday). PlanSettings in the server (PlanEndpoints.cs), same checks.
public struct PlanSettings: Codable, Equatable, Sendable {
    public enum View: String, Codable, Sendable { case day, week }
    public enum Hours: String, Codable, Sendable { case work, full }

    public var view: View = .week
    public var hours: Hours = .work
    public var workStart = 9 * 60
    public var workEnd = 17 * 60
    public var workDays = [1, 2, 3, 4, 5]
    /// 1: Monday, 7: Sunday.
    public var weekStart = 1

    public init() {}

    /// Why these work hours and days can't be kept (nil: they can).
    public var problem: String? {
        if workStart < 0 || workStart >= 24 * 60 || workEnd <= 0 || workEnd > 24 * 60 || workEnd - workStart < 60 {
            return "Work hours are within a day and at least an hour long."
        }
        if workDays.isEmpty || workDays.contains(where: { !(1...7).contains($0) }) { return "Pick at least one work day." }
        return weekStart == 1 || weekStart == 7 ? nil : "A week starts on Monday or Sunday."
    }
}

/// A part of a block on one day: a block crossing midnight goes on until the end of its day and carries on the next.
public struct PlanSegment: Equatable, Sendable {
    public let day: LocalDay
    /// Minutes after that day's midnight.
    public let from: Int
    public let to: Int
    /// It started on an earlier day.
    public let continues: Bool
    /// The block ends on this day (where its length is changed).
    public let isLast: Bool
}

/// The Plan view's grid: the days shown, which hours, where blocks go and how dragging snaps. The same as the web app's
/// Plan view (wwwroot/js/views/plan.js).
public enum PlanGrid {
    /// Times snap to a quarter of an hour.
    public static let snap = 15

    /// The first day shown: the day itself (day view), or the start of its week (Monday or Sunday).
    public static func firstDay(_ day: LocalDay, view: PlanSettings.View, weekStart: Int = 1) -> LocalDay {
        guard view == .week else { return day }
        let iso = day.weekday
        return day.adding(days: -(weekStart == 7 ? iso % 7 : iso - 1))
    }

    /// The days shown: in a week with work hours, only the work days (all of them if none is).
    public static func shownDays(from first: LocalDay, settings: PlanSettings) -> [LocalDay] {
        let all = (0..<(settings.view == .week ? 7 : 1)).map { first.adding(days: $0) }
        guard all.count > 1, settings.hours == .work else { return all }
        let kept = all.filter { settings.workDays.contains($0.weekday) }
        return kept.isEmpty ? all : kept
    }

    /// Minutes after local midnight, snapped to the grid (and kept within the day).
    public static func snapped(_ minutes: Double, step: Int = snap) -> Int {
        let rounded = Int((minutes / Double(step) + 0.5).rounded(.down)) * step
        return max(0, min(24 * 60 - step, rounded))
    }

    /// A block's new length after its bottom edge moved by `delta` minutes: snapped, a quarter of an hour to 12 hours.
    public static func resized(_ minutes: Int, by delta: Double, step: Int = snap) -> Int {
        let rounded = Int(((Double(minutes) + delta) / Double(step) + 0.5).rounded(.down)) * step
        return min(TaskBoard.maxPlanMinutes, max(step, rounded))
    }

    /// Where a block moved by `minutes` (and `days`) starts: snapped to the grid on its new day.
    public static func moved(_ start: Date, byMinutes minutes: Double, days: Int, timeZone: TimeZone) -> Date {
        let day = PlanDays.day(of: start, in: timeZone)
        let midnight = PlanDays.midnight(day, timeZone)
        let at = start.timeIntervalSince(midnight) / 60 + minutes
        return time(snapped(at), on: day.adding(days: days), timeZone: timeZone)
    }

    /// The moment `minutes` after midnight on `day` (wall-clock time there).
    public static func time(_ minutes: Int, on day: LocalDay, timeZone: TimeZone) -> Date {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        return calendar.date(from: DateComponents(year: day.year, month: day.month, day: day.day, hour: minutes / 60, minute: minutes % 60))!
    }

    /// Minutes after midnight on its own day.
    public static func minutes(of date: Date, timeZone: TimeZone) -> Int {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        let c = calendar.dateComponents([.hour, .minute], from: date)
        return c.hour! * 60 + c.minute!
    }

    /// A block, cut at midnights into a part per day.
    public static func segments(start: Date, minutes: Int, timeZone: TimeZone) -> [PlanSegment] {
        let end = start.addingTimeInterval(Double(minutes) * 60)
        var parts: [PlanSegment] = []
        var day = PlanDays.day(of: start, in: timeZone)
        var midnight = PlanDays.midnight(day, timeZone)
        while midnight < end {
            let next = PlanDays.midnight(day.adding(days: 1), timeZone)
            let from = max(start, midnight)
            let to = min(end, next)
            parts.append(PlanSegment(day: day, from: Int((from.timeIntervalSince(midnight) / 60).rounded()), to: Int((to.timeIntervalSince(midnight) / 60).rounded()),
                                     continues: start < midnight, isLast: end <= next))
            day = day.adding(days: 1)
            midnight = next
        }
        return parts
    }

    /// The hours shown (minutes after midnight): the whole day, or work hours stretched to show any block outside them on
    /// the days shown.
    public static func hours(settings: PlanSettings, segments: [PlanSegment], days: [LocalDay]) -> (from: Int, to: Int) {
        guard settings.hours == .work else { return (0, 24 * 60) }
        let shown = Set(days)
        var from = settings.workStart
        var to = settings.workEnd
        for segment in segments where shown.contains(segment.day) {
            from = min(from, segment.from / 60 * 60)
            to = max(to, min(24 * 60, (segment.to + 59) / 60 * 60))
        }
        return (from, to)
    }
}
