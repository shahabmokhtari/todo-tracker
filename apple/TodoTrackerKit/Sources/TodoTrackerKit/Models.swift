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

    init(id: UUID = UUID(), name: String, color: String?) {
        self.id = id
        self.name = name
        self.color = color
    }
}

public final class WorkItem: Identifiable {
    public let id: UUID
    public internal(set) var title: String
    public internal(set) var details: String?
    public internal(set) var priority: Priority
    public let createdAt: Date
    public internal(set) var completedAt: Date?
    public internal(set) var deadline: Date?
    public internal(set) var nextActionAt: Date?
    public internal(set) var sequential: Bool = false
    public internal(set) var stepDelay: TimeInterval?
    var ownGroupId: UUID
    public internal(set) weak var parent: WorkItem?
    public internal(set) var children: [WorkItem] = []
    public internal(set) var reminders: [Reminder] = []
    public internal(set) var notes: [Note] = []

    init(id: UUID = UUID(), title: String, priority: Priority, createdAt: Date, groupId: UUID) {
        self.id = id
        self.title = title
        self.priority = priority
        self.createdAt = createdAt
        self.ownGroupId = groupId
    }

    public var groupId: UUID { parent?.groupId ?? ownGroupId }
    public var isDone: Bool { completedAt != nil }
    public var hasOpenChildren: Bool { children.contains { !$0.isDone } }

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
