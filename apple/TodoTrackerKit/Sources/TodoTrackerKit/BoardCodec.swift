import Foundation

/// JSON schema v1, identical to TodoTracker.Core.BoardSerializer (camelCase, UTC ISO-8601 with milliseconds).
public enum BoardCodec {
    public static func decode(_ data: Data) throws -> TaskBoard {
        let doc: BoardDocument
        do {
            doc = try decoder.decode(BoardDocument.self, from: data)
        } catch {
            throw BoardError.invalid("Board file is not valid: \(error.localizedDescription)")
        }
        if doc.schemaVersion > TaskBoard.currentSchemaVersion {
            throw BoardError.unsupportedSchema("Board schema v\(doc.schemaVersion) is newer than this app supports (v\(TaskBoard.currentSchemaVersion)). Please update the app.")
        }
        return try doc.toBoard()
    }

    public static func encode(_ board: TaskBoard) throws -> Data {
        try encoder.encode(BoardDocument(board))
    }

    static let decoder: JSONDecoder = {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .custom { decoder in
            let text = try decoder.singleValueContainer().decode(String.self)
            if let date = parseDate(text) { return date }
            throw DecodingError.dataCorrupted(.init(codingPath: decoder.codingPath, debugDescription: "Invalid timestamp \(text)"))
        }
        return d
    }()

    static let encoder: JSONEncoder = {
        let e = JSONEncoder()
        e.outputFormatting = [.prettyPrinted, .sortedKeys]
        e.dateEncodingStrategy = .custom { date, encoder in
            var c = encoder.singleValueContainer()
            try c.encode(formatDate(date))
        }
        return e
    }()

    static func formatDate(_ date: Date) -> String {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        f.timeZone = TimeZone(identifier: "UTC")
        return f.string(from: date)
    }

    static func parseDate(_ text: String) -> Date? {
        let withFraction = ISO8601DateFormatter()
        withFraction.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let d = withFraction.date(from: text) { return d }
        let plain = ISO8601DateFormatter()
        plain.formatOptions = [.withInternetDateTime]
        if let d = plain.date(from: text) { return d }
        // .NET can emit 7 fractional digits; trim to milliseconds and retry.
        if let dot = text.firstIndex(of: "."), let end = text[dot...].firstIndex(where: { !$0.isNumber && $0 != "." }) {
            let digits = text[text.index(after: dot)..<end]
            let trimmed = String(text[..<dot]) + "." + String(digits.prefix(3)).padding(toLength: 3, withPad: "0", startingAt: 0) + String(text[end...])
            return withFraction.date(from: trimmed)
        }
        return nil
    }
}

struct BoardDocument: Codable {
    var schemaVersion: Int = 1
    var groups: [GroupDocument]?
    var items: [ItemDocument]?
    var activity: [ActivityDocument]?
    var pomodoro: PomodoroDocument?

    init(_ board: TaskBoard) {
        schemaVersion = TaskBoard.currentSchemaVersion
        groups = board.groups.map { GroupDocument(id: $0.id, name: $0.name, color: $0.color) }
        items = board.items.map(ItemDocument.init)
        activity = board.activity.map { ActivityDocument(at: $0.at, itemId: $0.itemId, kind: $0.kind, summary: $0.summary, actor: $0.actor) }
        let p = board.pomodoro
        pomodoro = PomodoroDocument(
            settings: p.settings,
            phase: p.phase,
            endsAt: p.endsAt,
            pausedRemainingSeconds: p.pausedRemaining.map { Int($0.rounded()) },
            itemId: p.itemId,
            completedFocusCount: p.completedFocusCount)
    }

    func toBoard() throws -> TaskBoard {
        let board = TaskBoard(seedDefaultGroups: false)
        for g in groups ?? [] where !g.name.trimmingCharacters(in: .whitespaces).isEmpty && !board.groups.contains(where: { $0.id == g.id }) {
            board.appendLoaded(group: TaskGroup(id: g.id, name: g.name.trimmingCharacters(in: .whitespaces), color: g.color))
        }
        if board.groups.isEmpty { board.seedGroups() }
        for item in items ?? [] { try load(item, into: board, parent: nil) }
        for a in activity ?? [] {
            board.appendLoaded(activity: ActivityEntry(at: a.at, itemId: a.itemId, kind: a.kind, summary: a.summary ?? "", actor: a.actor ?? .user))
        }
        if let p = pomodoro {
            var timer = PomodoroTimer(settings: p.settings ?? PomodoroSettings())
            timer.restore(phase: p.phase ?? .idle, endsAt: p.endsAt, pausedRemaining: p.pausedRemainingSeconds.map(TimeInterval.init), itemId: p.itemId, completedFocusCount: p.completedFocusCount ?? 0)
            board.pomodoro = timer
        }
        return board
    }

    private func load(_ doc: ItemDocument, into board: TaskBoard, parent: WorkItem?) throws {
        let title = try TaskBoard.requireText(doc.title, "Task \(doc.id) has no title.")
        let group = doc.groupId.flatMap { id in board.groups.first(where: { $0.id == id })?.id } ?? board.defaultGroupId
        let item = WorkItem(id: doc.id, title: title, priority: doc.priority ?? .normal, createdAt: doc.createdAt, groupId: group)
        item.details = doc.details
        item.completedAt = doc.completedAt
        item.deadline = doc.deadline
        item.nextActionAt = doc.nextActionAt
        item.sequential = doc.sequential ?? false
        item.stepDelay = doc.stepDelayMinutes.flatMap { $0 > 0 ? TimeInterval($0 * 60) : nil }
        item.reminders = (doc.reminders ?? []).map {
            Reminder(id: $0.id, dueAt: $0.dueAt, message: $0.message ?? "", kind: $0.kind ?? .manual, notifiedAt: $0.notifiedAt, dismissedAt: $0.dismissedAt)
        }
        item.notes = (doc.notes ?? []).map {
            Note(id: $0.id, at: $0.at, text: $0.text ?? "", author: $0.author ?? .user, sourceUrl: $0.sourceUrl, sourceTitle: $0.sourceTitle)
        }
        try board.attach(item, to: parent)
        for child in doc.children ?? [] { try load(child, into: board, parent: item) }
    }
}

struct GroupDocument: Codable {
    var id: UUID
    var name: String
    var color: String?
}

struct ItemDocument: Codable {
    var id: UUID
    var title: String
    var details: String?
    var priority: Priority?
    var createdAt: Date
    var completedAt: Date?
    var deadline: Date?
    var nextActionAt: Date?
    var sequential: Bool?
    var stepDelayMinutes: Int?
    var groupId: UUID?
    var reminders: [ReminderDocument]?
    var notes: [NoteDocument]?
    var children: [ItemDocument]?

    init(_ item: WorkItem) {
        id = item.id
        title = item.title
        details = item.details
        priority = item.priority
        createdAt = item.createdAt
        completedAt = item.completedAt
        deadline = item.deadline
        nextActionAt = item.nextActionAt
        sequential = item.sequential ? true : nil
        stepDelayMinutes = item.stepDelay.map { Int(($0 / 60).rounded()) }
        groupId = item.parent == nil ? item.ownGroupId : nil
        reminders = item.reminders.isEmpty ? nil : item.reminders.map {
            ReminderDocument(id: $0.id, dueAt: $0.dueAt, message: $0.message, kind: $0.kind, notifiedAt: $0.notifiedAt, dismissedAt: $0.dismissedAt)
        }
        notes = item.notes.isEmpty ? nil : item.notes.map {
            NoteDocument(id: $0.id, at: $0.at, text: $0.text, author: $0.author, sourceUrl: $0.sourceUrl, sourceTitle: $0.sourceTitle)
        }
        children = item.children.isEmpty ? nil : item.children.map(ItemDocument.init)
    }
}

struct ReminderDocument: Codable {
    var id: UUID
    var dueAt: Date
    var message: String?
    var kind: ReminderKind?
    var notifiedAt: Date?
    var dismissedAt: Date?
}

struct NoteDocument: Codable {
    var id: UUID
    var at: Date
    var text: String?
    var author: Actor?
    var sourceUrl: String?
    var sourceTitle: String?
}

struct ActivityDocument: Codable {
    var at: Date
    var itemId: UUID
    var kind: String
    var summary: String?
    var actor: Actor?
}

struct PomodoroDocument: Codable {
    var settings: PomodoroSettings?
    var phase: PomodoroPhase?
    var endsAt: Date?
    var pausedRemainingSeconds: Int?
    var itemId: UUID?
    var completedFocusCount: Int?
}

/// Local-first JSON persistence with atomic writes and a `.bak` of the previous version.
public final class BoardFileStore {
    public let url: URL

    public init(url: URL) {
        self.url = url
    }

    public static func defaultURL() throws -> URL {
        let base = try FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
        return base.appendingPathComponent("TodoTracker", isDirectory: true).appendingPathComponent("board.json")
    }

    /// Loads the board. A newer-schema file is never replaced by a backup; a corrupt file is set aside
    /// (`board.json.corrupt-<timestamp>`) and the backup restored, mirroring the C# store.
    public func load() throws -> TaskBoard {
        let fm = FileManager.default
        guard fm.fileExists(atPath: url.path) else { return TaskBoard() }
        do {
            return try BoardCodec.decode(Data(contentsOf: url))
        } catch let error as BoardError {
            if case .unsupportedSchema(_) = error { throw error }
            return try recoverFromBackup(original: error)
        } catch {
            return try recoverFromBackup(original: error)
        }
    }

    private func recoverFromBackup(original: Error) throws -> TaskBoard {
        let fm = FileManager.default
        let backup = url.appendingPathExtension("bak")
        guard fm.fileExists(atPath: backup.path) else { throw original }
        let board = try BoardCodec.decode(Data(contentsOf: backup))
        let corrupt = url.appendingPathExtension("corrupt-\(Int(Date().timeIntervalSince1970))")
        try fm.moveItem(at: url, to: corrupt)
        try fm.copyItem(at: backup, to: url)
        return board
    }

    public func save(_ board: TaskBoard) throws {
        let data = try BoardCodec.encode(board)
        let fm = FileManager.default
        try fm.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let backup = url.appendingPathExtension("bak")
        // Only a readable board may become the backup, so a bad file can never overwrite the last good copy.
        if let current = try? Data(contentsOf: url), (try? BoardCodec.decode(current)) != nil {
            try? fm.removeItem(at: backup)
            try? fm.copyItem(at: url, to: backup)
        }
        try data.write(to: url, options: [.atomic])
    }
}
