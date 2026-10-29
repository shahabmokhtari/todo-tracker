import Foundation

// Swift port of TodoTracker.Core. Keep behavior in sync with the C# core; the shared scenarios in
// tests/fixtures/scenarios are run by both test suites.

public enum Priority: String, Codable, CaseIterable, Comparable, Sendable {
    case low, normal, high, critical

    public var rank: Int {
        switch self {
        case .low: return 0
        case .normal: return 1
        case .high: return 2
        case .critical: return 3
        }
    }

    public var label: String { rawValue.prefix(1).uppercased() + rawValue.dropFirst() }

    /// Same palette as the Windows sidebar and web dashboard.
    public var hexColor: String {
        switch self {
        case .low: return "#94a3b8"
        case .normal: return "#3b82f6"
        case .high: return "#f97316"
        case .critical: return "#e11d48"
        }
    }

    public static func < (lhs: Priority, rhs: Priority) -> Bool { lhs.rank < rhs.rank }
}

public enum ActorKind: String, Codable, Sendable {
    case user, agent, browser, teams, system
    /// Edited in the markdown files (Obsidian, an editor); a connected app (Notion, Microsoft To Do).
    case vault, connector

    /// A kind from a newer version reads as `system` rather than failing the whole board.
    public init(from decoder: Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        self = ActorKind(rawValue: raw) ?? .system
    }
}

public struct Actor: Codable, Equatable, Sendable {
    public var kind: ActorKind
    public var name: String?

    public init(kind: ActorKind, name: String? = nil) {
        self.kind = kind
        self.name = name
    }

    public static let user = Actor(kind: .user)
    public static let system = Actor(kind: .system)

    public static func agent(_ name: String) -> Actor {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        return Actor(kind: .agent, name: trimmed.isEmpty ? "agent" : trimmed)
    }

    public var displayName: String {
        if kind == .user { return "You" }
        let kindName = kind.rawValue.prefix(1).uppercased() + kind.rawValue.dropFirst()
        if let name, !name.isEmpty { return "\(kindName): \(name)" }
        return kindName
    }
}

public enum ItemState: String, Sendable {
    case actionable, waiting, locked, container, done
}

public enum ReminderKind: String, Codable, Sendable {
    case manual, nextAction
}

public enum BoardError: Error, LocalizedError, Equatable {
    case notFound(String)
    case invalid(String)
    case conflict(String)
    /// The file was written by a newer app version; never fall back or overwrite it.
    case unsupportedSchema(String)

    public var errorDescription: String? {
        switch self {
        case .notFound(let m), .invalid(let m), .conflict(let m), .unsupportedSchema(let m): return m
        }
    }
}

public final class Reminder: Identifiable {
    public let id: UUID
    public let dueAt: Date
    public let message: String
    public let kind: ReminderKind
    public internal(set) var notifiedAt: Date?
    public internal(set) var dismissedAt: Date?

    init(id: UUID = UUID(), dueAt: Date, message: String, kind: ReminderKind, notifiedAt: Date? = nil, dismissedAt: Date? = nil) {
        self.id = id
        self.dueAt = dueAt
        self.message = message
        self.kind = kind
        self.notifiedAt = notifiedAt
        self.dismissedAt = dismissedAt
    }

    public var isPending: Bool { dismissedAt == nil }

    public func isDue(_ now: Date) -> Bool { dismissedAt == nil && dueAt <= now }
}

public struct Note: Identifiable, Equatable {
    public let id: UUID
    public let at: Date
    public let text: String
    public let author: Actor
    public let sourceUrl: String?
    public let sourceTitle: String?
}

public struct ActivityEntry: Equatable {
    public let at: Date
    public let itemId: UUID
    /// Raw activity kind (e.g. "noteAdded"); kept as a string so newer kinds from other platforms round-trip.
    public let kind: String
    public let summary: String
    public let actor: Actor
}

public final class TaskGroup: Identifiable {
    public let id: UUID
    public internal(set) var name: String
    public internal(set) var color: String?
    /// How it repeats: completing it moves it on to its next time instead of finishing it.
    public internal(set) var repeatRule: Recurrence?
    /// Its deadline and wait before it last moved on, so "not done after all" puts them back.
    public internal(set) var repeatUndo: RepeatUndo?
    /// When it's planned to be worked on (a block in the Plan view), and for how long (nil: 30 minutes).
    public internal(set) var plannedAt: Date?
    public internal(set) var plannedMinutes: Int?

    /// Whether its planned block is on at `now`.
    public func isPlanned(at now: Date) -> Bool {
        guard let start = plannedAt else { return false }
        return start <= now && now < start.addingTimeInterval(Double(plannedMinutes ?? 30) * 60)
    }

    init(id: UUID = UUID(), name: String, color: String?) {
        self.id = id
        self.name = name
        self.color = color
    }
}

public final class WorkItem: Identifiable {
    /// Changes only while a task file is read (a copied file's ids are made its own), before it's on a board.
    public internal(set) var id: UUID
    public internal(set) var title: String
    public internal(set) var details: String?
    public internal(set) var priority: Priority
    public let createdAt: Date
    public internal(set) var completedAt: Date?
    /// Put away (a finished top-level task, archived on another device): out of every list.
    public internal(set) var archivedAt: Date?
    /// An emoji shown before the title (set in the other apps; kept here).
    public internal(set) var icon: String?
    /// The task's own color as #rrggbb (set in the other apps; kept here).
    public internal(set) var color: String?
    /// How it repeats: completing it moves it on to its next time instead of finishing it.
    public internal(set) var repeatRule: Recurrence?
    /// Its deadline and wait before it last moved on, so "not done after all" puts them back.
    public internal(set) var repeatUndo: RepeatUndo?
    /// When it's planned to be worked on (a block in the Plan view), and for how long (nil: 30 minutes).
    public internal(set) var plannedAt: Date?
    public internal(set) var plannedMinutes: Int?
    /// Energy it takes per hour, 1 to 3 (nil: not said; counts as 1). See `Energy`.
    public internal(set) var energy: Int?

    /// Whether its planned block is on at `now`.
    public func isPlanned(at now: Date) -> Bool {
        guard let start = plannedAt else { return false }
        return start <= now && now < start.addingTimeInterval(Double(plannedMinutes ?? 30) * 60)
    }
    public var isArchived: Bool { root.archivedAt != nil }
    public internal(set) var deadline: Date?
    public internal(set) var nextActionAt: Date?
    /// Snoozed until this task is done ("after task X"); see `waitingFor`.
    public internal(set) var afterId: UUID?
    /// The board it's on (set when it's added), to look up what it waits for.
    weak var board: TaskBoard?
    public internal(set) var sequential: Bool = false
    public internal(set) var stepDelay: TimeInterval?
    var ownGroupId: UUID
    public internal(set) weak var parent: WorkItem?
    public internal(set) var children: [WorkItem] = []
    public internal(set) var reminders: [Reminder] = []
    public internal(set) var notes: [Note] = []
    /// Time spent on it (TimeTracking.swift).
    public internal(set) var timeEntries: [TimeEntry] = []
    /// Free-form tags without the leading `#` (nested tags use `/`), as the other apps keep them.
    public internal(set) var tags: [String] = []
    /// Names of the board's labels on it.
    public internal(set) var labels: [String] = []
    /// Files attached to it (in the tasks folder).
    public internal(set) var attachments: [Attachment] = []
    /// Where a top-level task is on the board (subtasks follow their task).
    public internal(set) var stage: Stage = .next

    init(id: UUID = UUID(), title: String, priority: Priority, createdAt: Date, groupId: UUID) {
        self.id = id
        self.title = title
        self.priority = priority
        self.createdAt = createdAt
        self.ownGroupId = groupId
    }

    public var groupId: UUID { parent?.groupId ?? ownGroupId }
    public var isDone: Bool { completedAt != nil }

    /// The open task this one waits for (nil: none, it's done, or it's gone).
    public var waitingFor: WorkItem? {
        guard let afterId, let other = board?.find(afterId), !other.isDone else { return nil }
        return other
    }
    public var hasOpenChildren: Bool { children.contains { !$0.isDone } }

    /// The top-level task this one belongs to (itself when it is one).
    public var root: WorkItem { ancestors.last ?? self }

    public var ancestors: [WorkItem] {
        var result: [WorkItem] = []
        var current = parent
        while let c = current {
            result.append(c)
            current = c.parent
        }
        return result
    }

    public var selfAndDescendants: [WorkItem] {
        [self] + children.flatMap { $0.selfAndDescendants }
    }

    /// Titles from the root down to and including this item.
    public var path: [String] { ancestors.reversed().map(\.title) + [title] }
}

/// Where a top-level task is on the board (Inbox, Next, Doing). Done is `isDone`, not a stage.
public enum Stage: String, Codable, Sendable {
    case inbox, next, doing

    /// A stage from a newer version reads as Next rather than failing the whole board.
    public init(from decoder: Decoder) throws {
        self = Stage(rawValue: (try? decoder.singleValueContainer().decode(String.self)) ?? "") ?? .next
    }
}

/// A file attached to a task; `path` is relative to the tasks folder, with forward slashes.
public final class Attachment: Identifiable {
    public let id: UUID
    public let fileName: String
    public internal(set) var path: String
    public let size: Int64
    public let addedAt: Date
    public let addedBy: Actor

    init(id: UUID, fileName: String, path: String, size: Int64, addedAt: Date, addedBy: Actor) {
        self.id = id
        self.fileName = fileName
        self.path = path
        self.size = size
        self.addedAt = addedAt
        self.addedBy = addedBy
    }
}

/// A curated, colored label (tags are free-form; labels are the few picked from the board's list).
public final class LabelDefinition {
    public internal(set) var name: String
    /// `#rrggbb`.
    public internal(set) var color: String

    init(name: String, color: String) {
        self.name = name
        self.color = color
    }
}

/// A repeating task's deadline and wait before it moved on (see `WorkItem.repeatUndo`).
public struct RepeatUndo: Equatable, Codable {
    public var deadline: Date?
    public var nextActionAt: Date?
    public var plannedAt: Date?
}

public struct NewTask {
    public var title: String
    public var parentId: UUID?
    public var groupId: UUID?
    public var priority: Priority = .normal
    public var details: String?
    public var deadline: Date?
    public var sequential = false
    public var stepDelay: TimeInterval?

    public init(_ title: String, parentId: UUID? = nil, groupId: UUID? = nil, priority: Priority = .normal,
                details: String? = nil, deadline: Date? = nil, sequential: Bool = false, stepDelay: TimeInterval? = nil) {
        self.title = title
        self.parentId = parentId
        self.groupId = groupId
        self.priority = priority
        self.details = details
        self.deadline = deadline
        self.sequential = sequential
        self.stepDelay = stepDelay
    }
}

/// Changes to a task from its details (TaskChanges in C#). A field left nil stays as it is; for `deadline` and
/// `stepDelay`, `.some(nil)` clears it. `details` or `repeatRule` blank clears them (a blank rule stops it repeating).
public struct TaskChanges: Equatable, Sendable {
    public var title: String?
    public var details: String?
    public var priority: Priority?
    public var deadline: Date??
    public var sequential: Bool?
    public var stepDelay: TimeInterval??
    /// A repeat rule in words ("every weekday"); blank: it doesn't repeat any more.
    public var repeatRule: String?

    public init(title: String? = nil, details: String? = nil, priority: Priority? = nil, deadline: Date?? = nil,
                sequential: Bool? = nil, stepDelay: TimeInterval?? = nil, repeatRule: String? = nil) {
        self.title = title
        self.details = details
        self.priority = priority
        self.deadline = deadline
        self.sequential = sequential
        self.stepDelay = stepDelay
        self.repeatRule = repeatRule
    }

    public var isEmpty: Bool { self == TaskChanges() }
}
