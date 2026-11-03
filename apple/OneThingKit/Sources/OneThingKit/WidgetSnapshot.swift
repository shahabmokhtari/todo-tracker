import Foundation

/// A task as a home-screen widget shows it: its title and group, nothing else (no details, notes or tokens).
public struct WidgetItem: Equatable, Identifiable, Sendable {
    public let id: UUID
    public let title: String
    public let group: String?
    /// The group's color (#rrggbb).
    public let color: String?
    /// It has open subtasks: the widget opens it instead of marking it done.
    public let openSubtasks: Bool

    public init(id: UUID, title: String, group: String?, color: String?, openSubtasks: Bool) {
        self.id = id
        self.title = title
        self.group = group
        self.color = color
        self.openSubtasks = openSubtasks
    }
}

/// A task coming back later today, and when.
public struct WidgetLater: Equatable, Sendable {
    public let id: UUID
    public let at: Date

    public init(id: UUID, at: Date) {
        self.id = id
        self.at = at
    }
}

/// What the widget shows from `at` until the next frame: the Do now list (its first `WidgetSnapshot.maxItems` of
/// `nowCount`) and what comes back later today.
public struct WidgetFrame: Equatable, Sendable {
    public let at: Date
    public let now: [UUID]
    public let nowCount: Int
    public let later: [WidgetLater]
    public let laterCount: Int

    public init(at: Date, now: [UUID], nowCount: Int, later: [WidgetLater], laterCount: Int) {
        self.at = at
        self.now = now
        self.nowCount = nowCount
        self.later = later
        self.laterCount = laterCount
    }

    func sameList(_ other: WidgetFrame) -> Bool {
        nowCount == other.nowCount && laterCount == other.laterCount && now == other.now && later == other.later
    }
}

/// What a widget shows at one moment.
public struct WidgetView: Equatable, Sendable {
    /// The one thing to do now (nil: nothing).
    public let focus: WidgetItem?
    /// The next few after it.
    public let next: [WidgetItem]
    /// How many more today after `focus` and `next`: the rest to do now and what comes back later today.
    public let more: Int
    /// The Do now list (as far as the snapshot keeps it).
    public let today: [WidgetItem]
    /// What comes back later today, soonest first.
    public let later: [(item: WidgetItem, at: Date)]
    public let nowCount: Int
    public let laterCount: Int

    public init(focus: WidgetItem?, next: [WidgetItem], more: Int, today: [WidgetItem], later: [(item: WidgetItem, at: Date)], nowCount: Int, laterCount: Int) {
        self.focus = focus
        self.next = next
        self.more = more
        self.today = today
        self.later = later
        self.nowCount = nowCount
        self.laterCount = laterCount
    }

    public static let empty = WidgetView(focus: nil, next: [], more: 0, today: [], later: [], nowCount: 0, laterCount: 0)

    /// How many more today after showing the first `shown` of the Do now list.
    public func more(after shown: Int) -> Int { max(0, nowCount - shown) + laterCount }

    public static func == (a: WidgetView, b: WidgetView) -> Bool {
        a.focus == b.focus && a.next == b.next && a.more == b.more && a.today == b.today && a.nowCount == b.nowCount
            && a.laterCount == b.laterCount && a.later.map(\.item) == b.later.map(\.item) && a.later.map(\.at) == b.later.map(\.at)
    }
}

/// What the home-screen widgets show, worked out ahead by the app with the agenda's own rules (`Agenda.build`) at every
/// moment the list can change in the next day (a snooze ends, a reminder or a planned block starts, a deadline passes,
/// midnight), so the widget stays right with the app closed. Same rules as OneThing.Core.WidgetSnapshot
/// (tests/fixtures/widget.json checks C#, Swift and Kotlin).
public struct WidgetSnapshot: Equatable, Sendable {
    public let at: Date
    public let items: [WidgetItem]
    public let frames: [WidgetFrame]

    /// How far ahead frames are worked out.
    public static let horizon: TimeInterval = 24 * 3600
    /// At most this many frames (the first one is now).
    public static let maxFrames = 24
    /// At most this many tasks of each list in a frame.
    public static let maxItems = 8
    /// How many tasks after the focus a medium widget lists.
    public static let nextCount = 3

    public init(at: Date, items: [WidgetItem], frames: [WidgetFrame]) {
        self.at = at
        self.items = items
        self.frames = frames
    }

    public static func build(_ board: TaskBoard, now: Date, timeZone: TimeZone) -> WidgetSnapshot {
        let until = now.addingTimeInterval(horizon)
        var points = Set<Date>()
        func add(_ at: Date?) {
            if let at, at > now, at <= until { points.insert(at) }
        }
        for item in board.allItems where !item.isDone && !item.isArchived {
            add(item.nextActionAt)
            add(item.deadline)
            add(item.plannedAt)
            add(item.plannedEnd)
            for reminder in item.reminders where reminder.dismissedAt == nil { add(reminder.dueAt) }
        }
        var midnight = nextMidnight(after: now, in: timeZone)
        while midnight <= until {
            add(midnight)
            midnight = nextMidnight(after: midnight, in: timeZone)
        }

        var frames: [WidgetFrame] = []
        for at in [now] + points.sorted().prefix(maxFrames - 1) {
            let dashboard = Agenda.build(board, now: at, recentNoteCount: 0)
            let endOfDay = nextMidnight(after: at, in: timeZone)
            let later = dashboard.waiting.filter { ($0.wakeAt ?? .distantFuture) < endOfDay }
            let frame = WidgetFrame(
                at: at,
                now: dashboard.now.prefix(maxItems).map(\.item.id),
                nowCount: dashboard.now.count,
                later: later.prefix(maxItems).map { WidgetLater(id: $0.item.id, at: $0.wakeAt!) },
                laterCount: later.count)
            if let last = frames.last, last.sameList(frame) { continue }
            frames.append(frame)
        }

        var items: [WidgetItem] = []
        var seen = Set<UUID>()
        for id in frames.flatMap({ $0.now + $0.later.map(\.id) }) where seen.insert(id).inserted {
            guard let item = board.find(id) else { continue }
            let group = board.groups.first { $0.id == item.groupId }
            items.append(WidgetItem(id: id, title: item.title, group: group?.name, color: group?.color, openSubtasks: item.hasOpenChildren))
        }
        return WidgetSnapshot(at: now, items: items, frames: frames)
    }

    /// The same lists as `other`, whenever each was worked out (only the clock moved between them).
    public func sameLists(as other: WidgetSnapshot) -> Bool {
        guard items == other.items, frames.count == other.frames.count, let a = frames.first, let b = other.frames.first else { return items == other.items && frames == other.frames }
        return a.sameList(b) && Array(frames.dropFirst()) == Array(other.frames.dropFirst())
    }

    /// What to show at `at`, leaving out `hidden` (marked done from the widget, not saved yet).
    public func view(at: Date, hiding hidden: Set<UUID> = []) -> WidgetView {
        guard let frame = frame(at: at) else { return .empty }
        let byId = Dictionary(items.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        let today = frame.now.filter { !hidden.contains($0) }.compactMap { byId[$0] }
        let later = frame.later.filter { !hidden.contains($0.id) }.compactMap { l in byId[l.id].map { (item: $0, at: l.at) } }
        let nowCount = frame.nowCount - (frame.now.count - today.count)
        let laterCount = frame.laterCount - (frame.later.count - later.count)
        let next = Array(today.dropFirst().prefix(Self.nextCount))
        let shown = today.isEmpty ? 0 : 1 + next.count
        return WidgetView(focus: today.first, next: next, more: max(0, nowCount - shown) + laterCount, today: today, later: later,
                          nowCount: nowCount, laterCount: laterCount)
    }

    /// The frame on at `at`: the last one started by then (the first one before it starts).
    public func frame(at: Date) -> WidgetFrame? { frames.last { $0.at <= at } ?? frames.first }

    /// The start of the day after the one `at` is in.
    public static func nextMidnight(after at: Date, in timeZone: TimeZone) -> Date {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        return calendar.date(byAdding: .day, value: 1, to: calendar.startOfDay(for: at)) ?? at.addingTimeInterval(24 * 3600)
    }
}

// MARK: The file the app writes and the widgets read

extension WidgetSnapshot {
    /// The snapshot as JSON (the format in tests/fixtures/widget.json): titles, groups, ids and times only.
    public func encoded() throws -> Data {
        func text(_ id: UUID) -> String { id.uuidString.lowercased() }
        let json: [String: Any] = [
            "at": BoardCodec.formatDate(at),
            "items": items.map { i -> [String: Any] in
                ["id": text(i.id), "title": i.title, "group": i.group as Any? ?? NSNull(), "color": i.color as Any? ?? NSNull(), "openSubtasks": i.openSubtasks]
            },
            "frames": frames.map { f -> [String: Any] in
                ["at": BoardCodec.formatDate(f.at), "now": f.now.map(text), "nowCount": f.nowCount,
                 "later": f.later.map { ["id": text($0.id), "at": BoardCodec.formatDate($0.at)] }, "laterCount": f.laterCount]
            },
        ]
        return try JSONSerialization.data(withJSONObject: json, options: [.sortedKeys])
    }

    /// Reads a snapshot (nil when it isn't one: the widget then asks to open the app).
    public static func decode(_ data: Data) -> WidgetSnapshot? {
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        return decode(json)
    }

    public static func decode(_ json: [String: Any]) -> WidgetSnapshot? {
        func date(_ value: Any?) -> Date? { (value as? String).flatMap(BoardCodec.parseDate) }
        func uuid(_ value: Any?) -> UUID? { (value as? String).flatMap(UUID.init(uuidString:)) }
        func text(_ value: Any?) -> String? { value as? String }
        guard let at = date(json["at"]), let rawItems = json["items"] as? [[String: Any]], let rawFrames = json["frames"] as? [[String: Any]] else { return nil }
        var items: [WidgetItem] = []
        for i in rawItems {
            guard let id = uuid(i["id"]), let title = text(i["title"]) else { return nil }
            items.append(WidgetItem(id: id, title: title, group: text(i["group"]), color: text(i["color"]), openSubtasks: i["openSubtasks"] as? Bool ?? false))
        }
        var frames: [WidgetFrame] = []
        for f in rawFrames {
            guard let fAt = date(f["at"]), let now = f["now"] as? [String], let later = f["later"] as? [[String: Any]] else { return nil }
            let laterItems = later.compactMap { l in uuid(l["id"]).flatMap { id in date(l["at"]).map { WidgetLater(id: id, at: $0) } } }
            frames.append(WidgetFrame(at: fAt, now: now.compactMap(UUID.init(uuidString:)), nowCount: f["nowCount"] as? Int ?? now.count,
                                      later: laterItems, laterCount: f["laterCount"] as? Int ?? laterItems.count))
        }
        return WidgetSnapshot(at: at, items: items, frames: frames)
    }
}
