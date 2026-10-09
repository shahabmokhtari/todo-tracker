import Foundation

/// Where a stretch of time came from: a timer started by hand (or typed in), or a focus session.
public enum TimeSource: String, Codable, Sendable {
    case manual, focus

    public init(from decoder: Decoder) throws {
        // A kind from a newer version reads as manual time.
        self = TimeSource(rawValue: (try? decoder.singleValueContainer().decode(String.self)) ?? "") ?? .manual
    }
}

/// A stretch of time spent on a task; a running one has no end. Same as TodoTracker.Core.TimeEntry.
public final class TimeEntry: Identifiable {
    public let id: UUID
    public let start: Date
    public internal(set) var end: Date?
    public let source: TimeSource
    /// The device that timed it (a timer it left running is its to end).
    public let device: String?

    init(id: UUID = UUID(), start: Date, end: Date? = nil, source: TimeSource, device: String?) {
        self.id = id
        self.start = start
        self.end = end
        self.source = source
        self.device = device
    }

    public var isRunning: Bool { end == nil }

    /// How long it lasted (a running one: until `now`, and at most `TaskBoard.forgottenAfter`).
    public func duration(_ now: Date) -> TimeInterval {
        let until = end ?? min(now, start.addingTimeInterval(TaskBoard.forgottenAfter))
        return max(0, until.timeIntervalSince(start))
    }
}

extension WorkItem {
    /// Time spent on it (with its subtasks, unless asked not to).
    public func timeSpent(_ now: Date, includeSubtasks: Bool = true) -> TimeInterval {
        (includeSubtasks ? selfAndDescendants : [self]).flatMap(\.timeEntries).reduce(0) { $0 + $1.duration(now) }
    }
}

/// Timers: one runs at a time; a focus session on a task times it. Same rules as TodoTracker.Core (TaskBoard.Time.cs).
extension TaskBoard {
    /// A timer running longer than this was forgotten (or its device is gone): it counts this long at most.
    public static let forgottenAfter: TimeInterval = 12 * 3600

    /// This device, as time entries name it.
    public static var thisDevice: String { ProcessInfo.processInfo.hostName }

    /// The timer that runs (the newest, if sync brought two together), or nil. A forgotten one doesn't count.
    public func runningTimer(_ now: Date? = nil) -> (item: WorkItem, entry: TimeEntry)? {
        var newest: (item: WorkItem, entry: TimeEntry)?
        for item in allItems {
            for entry in item.timeEntries where entry.isRunning {
                if let now, now.timeIntervalSince(entry.start) > Self.forgottenAfter { continue }
                if newest == nil || entry.start > newest!.entry.start { newest = (item, entry) }
            }
        }
        return newest
    }

    /// Starts timing a task; any other timer stops (one runs at a time).
    @discardableResult
    public func startTimer(_ id: UUID, actor: Actor = .user, now: Date) throws -> TimeEntry {
        let item = try get(id)
        if item.isDone { throw BoardError.conflict("\"\(item.title)\" is already done.") }
        if let running = runningTimer(), running.item === item { return running.entry }
        // A focus session on another task no longer counts for it (the session itself goes on).
        if pomodoro.phase == .focus, let focused = pomodoro.itemId, focused != item.id { pomodoro.detachItem() }
        let entry = startTimerCore(item, source: .manual, now: now)
        log(now, item.id, "timeLogged", "Started the timer on \"\(item.title)\"", actor)
        return entry
    }

    /// Stops the timer (every running one, if sync brought two together); the newest is returned.
    @discardableResult
    public func stopTimer(now: Date) -> (item: WorkItem, entry: TimeEntry)? {
        let newest = runningTimer()
        stopTimers(allItems, now: now)
        return newest
    }

    /// Ends the running timers among `items`: each at the start of a newer one, the newest now.
    func stopTimers(_ items: [WorkItem], now: Date) {
        let running = items.flatMap(\.timeEntries).filter(\.isRunning).sorted { $0.start < $1.start }
        for (i, entry) in running.enumerated() {
            var end = i < running.count - 1 ? running[i + 1].start : now
            if end < entry.start { end = entry.start }
            // A forgotten timer ends at the most it counts, not hours (or days) later.
            entry.end = end.timeIntervalSince(entry.start) > Self.forgottenAfter ? entry.start.addingTimeInterval(Self.forgottenAfter) : end
        }
    }

    func stopFocusTimer(_ at: Date) {
        stopTimers(allItems.filter { $0.timeEntries.contains { $0.isRunning && $0.source == .focus } }, now: at)
    }

    @discardableResult
    func startTimerCore(_ item: WorkItem, source: TimeSource, now: Date) -> TimeEntry {
        stopTimers(allItems, now: now)
        let entry = TimeEntry(start: now, source: source, device: Self.thisDevice)
        item.timeEntries.append(entry)
        return entry
    }
}
