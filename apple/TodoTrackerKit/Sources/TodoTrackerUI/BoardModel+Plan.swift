#if canImport(SwiftUI)
import Foundation
import TodoTrackerKit

/// Planning and reports. A plan change goes where every change goes: the board on this device, the Mac's server, or
/// the computer the phone is connected to (POST /api/items/{id}/plan). Reports are worked out here from the board
/// (its time entries and activity), the same numbers as the server's (tests/fixtures/reports.json), so they work the
/// same in every mode, offline too.
extension BoardModel {
    /// Plans a block of work on a task from `start` (minutes nil: what it had, else 30).
    @discardableResult
    public func plan(_ id: UUID, at start: Date, minutes: Int? = nil) -> Bool {
        let title = board.find(id)?.title ?? "it"
        let when = PlanWords.when(start, now: Date())
        return change("Planned “\(title)” \(when)", server: .plan(id, start: start, minutes: minutes)) {
            try board.plan(id, start: start, minutes: minutes, now: $0)
        }
    }

    /// Takes a task off the plan.
    public func unplan(_ id: UUID) {
        let title = board.find(id)?.title ?? "it"
        change("Took “\(title)” off the plan", server: .unplan(id)) { try board.unplan(id, now: $0) }
    }

    /// What the Plan view shows for some days, in this device's time zone.
    public func planDays(from first: LocalDay, days: Int, groupId: UUID?) -> PlanDays {
        PlanDays.build(board, from: first, days: days, timeZone: .current, now: Date(), groupId: groupId)
    }

    /// Where the time went between two local dates (inclusive).
    public func report(from: LocalDay, to: LocalDay, groupId: UUID?) throws -> TimeReport {
        try TimeReport.build(board, from: from, to: to, timeZone: .current, now: Date(), groupId: groupId)
    }

    /// Plan settings for a group's Plan view (each group keeps its own, on this device).
    public func planSettings(for groupId: UUID?) -> PlanSettings {
        guard let data = UserDefaults.standard.data(forKey: Self.planKey(groupId)),
              let settings = try? JSONDecoder().decode(PlanSettings.self, from: data), settings.problem == nil else { return PlanSettings() }
        return settings
    }

    public func setPlanSettings(_ settings: PlanSettings, for groupId: UUID?) {
        guard settings.problem == nil, let data = try? JSONEncoder().encode(settings) else { return }
        UserDefaults.standard.set(data, forKey: Self.planKey(groupId))
        objectWillChange.send()
    }

    private static func planKey(_ groupId: UUID?) -> String { "plan.settings.\(groupId?.uuidString.lowercased() ?? "all")" }
}

/// Times in words for the plan ("today 14:30", "Thu 29 Oct 09:00").
enum PlanWords {
    static func when(_ date: Date, now: Date) -> String {
        let time = date.formatted(date: .omitted, time: .shortened)
        if Calendar.current.isDate(date, inSameDayAs: now) { return "today \(time)" }
        if let tomorrow = Calendar.current.date(byAdding: .day, value: 1, to: now), Calendar.current.isDate(date, inSameDayAs: tomorrow) { return "tomorrow \(time)" }
        return "\(date.formatted(.dateTime.weekday(.abbreviated).day().month(.abbreviated))) \(time)"
    }
}
#endif
