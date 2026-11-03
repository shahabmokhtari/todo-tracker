import Foundation

/// One day in a report: time tracked (and how much of it was focus sessions), tasks finished and created.
public struct ReportDay: Equatable {
    public let date: LocalDay
    public let trackedSeconds: Int64
    public let focusSeconds: Int64
    public let completed: Int
    public let created: Int
}

public struct ReportGroup: Equatable {
    public let groupId: UUID
    public let name: String
    public let color: String?
    public let trackedSeconds: Int64
}

public struct ReportTask: Equatable {
    public let id: UUID
    public let title: String
    public let groupId: UUID
    public let done: Bool
    public let trackedSeconds: Int64
}

public struct ReportSpan: Equatable {
    public let start: Date
    public let end: Date
    public let source: TimeSource
}

/// A task on the timeline: when it was created and finished (or is due), and when it was worked on.
public struct ReportRow: Equatable, Identifiable {
    public let id: UUID
    public let title: String
    public let groupId: UUID
    public let createdAt: Date
    public let completedAt: Date?
    public let deadline: Date?
    public let work: [ReportSpan]
}

/// Where the time went between two local dates (inclusive): per day, group, task, and weekday-hour, what got done, and a
/// timeline of the tasks worked on. Time is the tracked time entries (a running timer counts until now), split at local
/// midnights; archived tasks count too (the work happened). TimeReport.cs in the C# core, the same numbers
/// (tests/fixtures/reports.json).
public struct TimeReport: Equatable {
    public static let maxDays = 732
    public static let maxTasks = 15
    public static let maxTimelineRows = 40

    public let from: LocalDay
    public let to: LocalDay
    public let trackedSeconds: Int64
    public let focusSessions: Int
    public let completed: Int
    public let created: Int
    public let activeDays: Int
    public let longestStreak: Int
    public let days: [ReportDay]
    public let groups: [ReportGroup]
    public let tasks: [ReportTask]
    /// [weekday: 0 = Sunday … 6 = Saturday][hour 0…23], seconds.
    public let hourGrid: [[Int64]]
    public let timeline: [ReportRow]

    // Time is summed in ticks (100 ns, as .NET counts), whole seconds at the end: the days add up to the total.
    private static let ticksPerSecond: Int64 = 10_000_000
    private static func ticks(_ date: Date) -> Int64 { Int64((date.timeIntervalSince1970 * 1e7).rounded()) }
    private static func seconds(_ ticks: Int64) -> Int64 { ticks / ticksPerSecond }

    public static func build(_ board: TaskBoard, from: LocalDay, to: LocalDay, timeZone: TimeZone, now: Date, groupId: UUID? = nil) throws -> TimeReport {
        guard to >= from else { throw BoardError.invalid("The end date must be on or after the start date.") }
        let dayCount = to.epochDay - from.epochDay + 1
        guard dayCount <= maxDays else { throw BoardError.invalid("A report can cover at most two years.") }

        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        let rangeStart = PlanDays.midnight(from, timeZone)
        let rangeEnd = PlanDays.midnight(to.adding(days: 1), timeZone)
        var tracked = [Int64](repeating: 0, count: dayCount)
        var focus = [Int64](repeating: 0, count: dayCount)
        var completed = [Int](repeating: 0, count: dayCount)
        var created = [Int](repeating: 0, count: dayCount)
        var grid = [[Int64]](repeating: [Int64](repeating: 0, count: 24), count: 7)
        var byGroup: [UUID: Int64] = [:]
        // By task, in the order first seen (ties keep it, as in C#).
        var byTask: [UUID: Int64] = [:]
        var taskOrder: [UUID] = []
        var rows: [(row: ReportRow, workTicks: Int64)] = []

        func dayIndex(_ at: Date) -> Int { PlanDays.day(of: at, in: timeZone).epochDay - from.epochDay }
        func inRange(_ at: Date) -> Bool { at >= rangeStart && at < rangeEnd }

        /// Adds a stretch of time to the days and weekday-hours it covers (split at local hours).
        func spread(_ start: Date, _ end: Date, _ source: TimeSource) {
            var cursor = ticks(start)
            let stopAt = ticks(end)
            while cursor < stopAt {
                let at = Date(timeIntervalSince1970: Double(cursor) / 1e7)
                let local = calendar.dateComponents([.weekday, .hour, .minute, .second, .nanosecond], from: at)
                let millis = Int64((local.nanosecond ?? 0) / 1_000_000)
                let nextHour = cursor + Int64(60 - local.minute!) * 60 * ticksPerSecond - Int64(local.second!) * ticksPerSecond - millis * 10_000
                let stop = min(nextHour, stopAt)
                let part = stop - cursor
                let index = dayIndex(at)
                if index >= 0 && index < dayCount {
                    tracked[index] += part
                    if source == .focus { focus[index] += part }
                }
                grid[local.weekday! - 1][local.hour!] += part
                cursor = stop
            }
        }

        for root in board.items where groupId == nil || root.groupId == groupId {
            var work: [ReportSpan] = []
            for item in root.selfAndDescendants {
                if let done = item.completedAt, inRange(done) { completed[dayIndex(done)] += 1 }
                if inRange(item.createdAt) { created[dayIndex(item.createdAt)] += 1 }
                for entry in item.timeEntries {
                    let start = max(entry.start, rangeStart)
                    let end = min(entry.end ?? now, rangeEnd)
                    if end <= start { continue }
                    work.append(ReportSpan(start: start, end: end, source: entry.source))
                    let part = ticks(end) - ticks(start)
                    byGroup[root.groupId, default: 0] += part
                    if byTask[root.id] == nil { taskOrder.append(root.id) }
                    byTask[root.id, default: 0] += part
                    spread(start, end, entry.source)
                }
            }
            let touched = !work.isEmpty || root.completedAt.map(inRange) == true || inRange(root.createdAt)
            if touched {
                let sorted = stableSorted(work) { $0.start < $1.start }
                rows.append((ReportRow(id: root.id, title: root.title, groupId: root.groupId, createdAt: root.createdAt, completedAt: root.completedAt, deadline: root.deadline, work: sorted),
                             sorted.reduce(0) { $0 + ticks($1.end) - ticks($1.start) }))
            }
        }

        let reportDays = (0..<dayCount).map {
            ReportDay(date: from.adding(days: $0), trackedSeconds: seconds(tracked[$0]), focusSeconds: seconds(focus[$0]), completed: completed[$0], created: created[$0])
        }
        let active = reportDays.map { $0.trackedSeconds > 0 || $0.completed > 0 }
        var streak = 0
        var longest = 0
        for on in active {
            streak = on ? streak + 1 : 0
            longest = max(longest, streak)
        }

        let focusSessions = board.activity.filter { a in
            a.kind == "focusCompleted" && inRange(a.at) && (groupId == nil || board.find(a.itemId)?.groupId == groupId)
        }.count

        let groups = stableSorted(board.groups.filter { byGroup[$0.id] != nil }.map {
            ReportGroup(groupId: $0.id, name: $0.name, color: $0.color, trackedSeconds: seconds(byGroup[$0.id]!))
        }) { $0.trackedSeconds > $1.trackedSeconds }
        let tasks = stableSorted(taskOrder) { byTask[$0]! > byTask[$1]! }.prefix(maxTasks).compactMap { id -> ReportTask? in
            guard let r = board.find(id) else { return nil }
            return ReportTask(id: r.id, title: r.title, groupId: r.groupId, done: r.isDone, trackedSeconds: seconds(byTask[id]!))
        }

        // The most worked rows (then the latest finished or made), shown by when their work (else finish or start) began.
        let busiest = stableSorted(rows) { a, b in
            if a.workTicks != b.workTicks { return a.workTicks > b.workTicks }
            return (a.row.completedAt ?? a.row.createdAt) > (b.row.completedAt ?? b.row.createdAt)
        }.prefix(maxTimelineRows).map(\.row)
        let timeline = stableSorted(Array(busiest)) { a, b in
            (a.work.first?.start ?? a.completedAt ?? a.createdAt) < (b.work.first?.start ?? b.completedAt ?? b.createdAt)
        }

        return TimeReport(
            from: from, to: to,
            trackedSeconds: seconds(byTask.values.reduce(0, +)),
            focusSessions: focusSessions,
            completed: reportDays.reduce(0) { $0 + $1.completed },
            created: reportDays.reduce(0) { $0 + $1.created },
            activeDays: active.filter { $0 }.count,
            longestStreak: longest,
            days: reportDays, groups: groups, tasks: Array(tasks),
            hourGrid: grid.map { $0.map(seconds) },
            timeline: timeline)
    }
}
