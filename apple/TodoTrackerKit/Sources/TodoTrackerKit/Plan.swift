import Foundation

/// Planning: a block of time to work on a task (the Plan view's day and week grid). Same rules as TaskBoard.Plan.cs and
/// PlanDays.cs in the C# core (tests/fixtures/plan.json).
extension TaskBoard {
    public static let defaultPlanMinutes = 30
    public static let minPlanMinutes = 5
    public static let maxPlanMinutes = 12 * 60

    /// Plans `minutes` (nil: what it had, else 30) from `start` to work on a task.
    public func plan(_ id: UUID, start: Date, minutes: Int?, actor: Actor = .user, now: Date) throws {
        let item = try get(id)
        if item.isDone { throw BoardError.conflict("\"\(item.title)\" is already done.") }
        let length = minutes ?? item.plannedMinutes ?? Self.defaultPlanMinutes
        guard (Self.minPlanMinutes...Self.maxPlanMinutes).contains(length) else {
            throw BoardError.invalid("A planned block is \(Self.minPlanMinutes) minutes to \(Self.maxPlanMinutes / 60) hours.")
        }
        item.plannedAt = start
        item.plannedMinutes = length == Self.defaultPlanMinutes ? nil : length
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = timeZone
        formatter.dateFormat = "EEE d MMM HH:mm"
        log(now, id, "scheduled", "Planned \"\(item.title)\" \(formatter.string(from: start)), \(length) min", actor)
    }

    /// Takes a task off the plan (nothing happens when it isn't on it).
    public func unplan(_ id: UUID, actor: Actor = .user, now: Date) throws {
        let item = try get(id)
        guard item.plannedAt != nil else { return }
        item.plannedAt = nil
        item.plannedMinutes = nil
        log(now, id, "scheduled", "Took \"\(item.title)\" off the plan", actor)
    }
}

extension WorkItem {
    /// How long its planned block is, in minutes.
    public var plannedLength: Int { plannedMinutes ?? TaskBoard.defaultPlanMinutes }

    /// When its planned block ends (nil: not planned).
    public var plannedEnd: Date? { plannedAt.map { $0.addingTimeInterval(Double(plannedLength) * 60) } }
}

/// What the Plan view shows for some days (from a local date, in the user's time zone): the blocks planned in them, the
/// deadlines on them, the tasks to do that aren't planned yet (in Do now order), the ones put off (Later), and what each
/// day's blocks take in energy (every group). PlanDays.cs in the C# core.
public struct PlanDays {
    public static let maxDays = 31

    public let from: LocalDay
    public let days: Int
    public let blocks: [WorkItem]
    public let deadlines: [WorkItem]
    public let unplanned: [AgendaEntry]
    public let later: [AgendaEntry]
    public let energy: [LocalDay: Double]

    public static func build(_ board: TaskBoard, from first: LocalDay, days: Int, timeZone: TimeZone, now: Date, groupId: UUID? = nil) -> PlanDays {
        let count = min(max(days, 1), maxDays)
        let start = midnight(first, timeZone)
        let end = midnight(first.adding(days: count), timeZone)
        let items = board.allItems.filter { !$0.isArchived && (groupId == nil || $0.groupId == groupId) }

        // Blocks that overlap the days (one crossing midnight shows on both), and deadlines in them; stable by time.
        let blocks = stableSorted(items.filter { item in
            guard let at = item.plannedAt, let blockEnd = item.plannedEnd else { return false }
            return at < end && blockEnd > start
        }) { $0.plannedAt! < $1.plannedAt! }
        let deadlines = stableSorted(items.filter { item in
            guard let deadline = item.deadline else { return false }
            return deadline >= start && deadline < end
        }) { $0.deadline! < $1.deadline! }

        // Not planned: no block, or its block is over (planned again from here).
        let agenda = Agenda.build(board, now: now, recentNoteCount: 0, groupId: groupId)
        func notPlanned(_ e: AgendaEntry) -> Bool { e.item.plannedEnd.map { $0 <= now } ?? true }
        var energy: [LocalDay: Double] = [:]
        for i in 0..<count {
            let day = first.adding(days: i)
            energy[day] = Energy.planned(on: midnight(day, timeZone), in: timeZone, board: board)
        }
        return PlanDays(from: first, days: count, blocks: blocks, deadlines: deadlines,
                        unplanned: agenda.now.filter(notPlanned), later: agenda.waiting.filter(notPlanned), energy: energy)
    }

    /// Local midnight of `day` in `timeZone`.
    public static func midnight(_ day: LocalDay, _ timeZone: TimeZone) -> Date {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        return calendar.date(from: DateComponents(year: day.year, month: day.month, day: day.day))!
    }

    /// The local date `date` falls on in `timeZone`.
    public static func day(of date: Date, in timeZone: TimeZone) -> LocalDay {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        let c = calendar.dateComponents([.year, .month, .day], from: date)
        return LocalDay(year: c.year!, month: c.month!, day: c.day!)
    }
}

/// Sorts keeping the order of equal elements (as LINQ's OrderBy does).
func stableSorted<T>(_ items: [T], by less: (T, T) -> Bool) -> [T] {
    items.enumerated().sorted { a, b in
        if less(a.element, b.element) { return true }
        if less(b.element, a.element) { return false }
        return a.offset < b.offset
    }.map(\.element)
}
