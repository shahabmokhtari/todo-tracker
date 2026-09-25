import Foundation

/// Aggregate root: tasks, groups (tabs), activity timeline, and the focus timer.
/// Mirrors TodoTracker.Core.TaskBoard: validate first, then mutate, and log every change.
public final class TaskBoard {
    public static let currentSchemaVersion = 1
    public static let maxTitleLength = 300
    /// Activity entries not tied to a task (e.g. group changes) use the empty id, like Guid.Empty in C#.
    public static let boardScopeId = UUID(uuidString: "00000000-0000-0000-0000-000000000000")!

    public internal(set) var items: [WorkItem] = []
    public internal(set) var activity: [ActivityEntry] = []
    public internal(set) var groups: [TaskGroup] = []
    public internal(set) var pomodoro = PomodoroTimer()
    private var index: [UUID: WorkItem] = [:]

    public init(seedDefaultGroups: Bool = true) {
        if seedDefaultGroups { seedGroups() }
    }

    public var defaultGroupId: UUID { groups[0].id }

    public var allItems: [WorkItem] { items.flatMap { $0.selfAndDescendants } }

    public func find(_ id: UUID) -> WorkItem? { index[id] }

    public func get(_ id: UUID) throws -> WorkItem {
        guard let item = index[id] else { throw BoardError.notFound("Task \(id) was not found.") }
        return item
    }

    public func group(_ id: UUID) throws -> TaskGroup {
        guard let group = groups.first(where: { $0.id == id }) else { throw BoardError.notFound("Group \(id) was not found.") }
        return group
    }

    // MARK: Tasks

    @discardableResult
    public func addTask(_ spec: NewTask, actor: Actor = .user, now: Date) throws -> WorkItem {
        let title = try Self.requireText(spec.title, "A task title is required.")
        try Self.validate(stepDelay: spec.stepDelay)
        let parent = try spec.parentId.map { try get($0) }
        let groupId: UUID
        if let parent {
            groupId = parent.groupId
        } else if let requested = spec.groupId {
            groupId = try group(requested).id
        } else {
            groupId = defaultGroupId
        }
        let item = WorkItem(title: title, priority: spec.priority, createdAt: now, groupId: groupId)
        item.details = Self.optionalText(spec.details)
        item.deadline = spec.deadline
        item.sequential = spec.sequential
        item.stepDelay = spec.stepDelay
        try attach(item, to: parent)
        log(now, item.id, "created", parent.map { "Added \"\(title)\" to \"\($0.title)\"" } ?? "Created \"\(title)\"", actor)
        return item
    }

    @discardableResult
    public func addSteps(parentId: UUID, titles: [String], stepDelay: TimeInterval?, actor: Actor = .user, now: Date) throws -> [WorkItem] {
        let parent = try get(parentId)
        try Self.validate(stepDelay: stepDelay)
        let clean = titles.map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }.filter { !$0.isEmpty }
        guard !clean.isEmpty else { throw BoardError.invalid("At least one step title is required.") }
        parent.sequential = true
        parent.stepDelay = stepDelay ?? parent.stepDelay
        return try clean.map { try addTask(NewTask($0, parentId: parent.id), actor: actor, now: now) }
    }

    public func delete(_ id: UUID, actor: Actor = .user, now: Date) throws {
        let item = try get(id)
        if let parent = item.parent {
            parent.children.removeAll { $0 === item }
        } else {
            items.removeAll { $0 === item }
        }
        for removed in item.selfAndDescendants { index[removed.id] = nil }
        if let focused = pomodoro.itemId, index[focused] == nil { pomodoro.detachItem() }
        log(now, id, "deleted", "Deleted \"\(item.title)\"", actor)
    }

    @discardableResult
    public func addNote(_ id: UUID, text: String, actor: Actor = .user, now: Date, sourceUrl: String? = nil, sourceTitle: String? = nil) throws -> Note {
        let item = try get(id)
        let clean = try Self.requireText(text, "A note cannot be empty.", maxLength: 10_000)
        if let sourceUrl {
            guard let url = URL(string: sourceUrl), let scheme = url.scheme?.lowercased(), scheme == "http" || scheme == "https" else {
                throw BoardError.invalid("Source URLs must be absolute http(s) URLs.")
            }
        }
        let note = Note(id: UUID(), at: now, text: clean, author: actor, sourceUrl: sourceUrl, sourceTitle: Self.optionalText(sourceTitle))
        item.notes.append(note)
        log(now, id, "noteAdded", clean, actor)
        return note
    }

    public func complete(_ id: UUID, actor: Actor = .user, now: Date) throws {
        let item = try get(id)
        if item.isDone { return }
        if let blocker = Self.findBlockingStep(item) {
            throw BoardError.conflict("Finish \"\(blocker.title)\" before \"\(item.title)\".")
        }
        for d in item.selfAndDescendants where !d.isDone { d.completedAt = now }
        log(now, id, "completed", "Completed \"\(item.title)\"", actor)
        advanceSequence(after: item, actor: actor, now: now)
    }

    public func reopen(_ id: UUID, actor: Actor = .user, now: Date) throws {
        let item = try get(id)
        guard let completedAt = item.completedAt else { return }
        item.completedAt = nil
        // Undo the cascade from completing a parent: children finished by that same action reopen too.
        for d in item.selfAndDescendants.dropFirst() where d.completedAt == completedAt { d.completedAt = nil }
        for a in item.ancestors where a.isDone { a.completedAt = nil }
        log(now, id, "reopened", "Reopened \"\(item.title)\"", actor)
    }

    /// Defers the item until `at`; with `notify`, a reminder fires at that time.
    public func scheduleNextAction(_ id: UUID, at: Date, notify: Bool = false, message: String? = nil, actor: Actor = .user, now: Date) throws {
        let item = try get(id)
        item.nextActionAt = at
        dismissPendingScheduleReminders(item, now: now)
        if notify {
            item.reminders.append(Reminder(dueAt: at, message: Self.optionalText(message) ?? Self.defaultReminderMessage(item), kind: .nextAction))
        }
        log(now, id, "scheduled", "Next action \(ISO8601DateFormatter().string(from: at))\(notify ? " (with reminder)" : "")", actor)
    }

    public func clearNextAction(_ id: UUID, actor: Actor = .user, now: Date) throws {
        let item = try get(id)
        item.nextActionAt = nil
        dismissPendingScheduleReminders(item, now: now)
        log(now, id, "scheduled", "Cleared next action; back to Now", actor)
    }

    @discardableResult
    public func addReminder(_ id: UUID, dueAt: Date, message: String?, actor: Actor = .user, now: Date) throws -> Reminder {
        let item = try get(id)
        let reminder = Reminder(dueAt: dueAt, message: Self.optionalText(message) ?? Self.defaultReminderMessage(item), kind: .manual)
        item.reminders.append(reminder)
        log(now, id, "reminderAdded", "Reminder: \(reminder.message)", actor)
        return reminder
    }

    public func dismissReminder(_ id: UUID, reminderId: UUID, actor: Actor = .user, now: Date) throws {
        let item = try get(id)
        guard let reminder = item.reminders.first(where: { $0.id == reminderId }) else {
            throw BoardError.notFound("Reminder \(reminderId) was not found.")
        }
        guard reminder.dismissedAt == nil else { return }
        reminder.dismissedAt = now
        log(now, id, "reminderDismissed", "Dismissed reminder: \(reminder.message)", actor)
    }

    /// Marks a due reminder as delivered (local notification shown) so it is not delivered again.
    public func markNotified(_ itemId: UUID, reminderId: UUID, now: Date) {
        guard let item = index[itemId], let reminder = item.reminders.first(where: { $0.id == reminderId }), reminder.notifiedAt == nil else { return }
        reminder.notifiedAt = now
        log(now, itemId, "reminderFired", "Reminder: \(reminder.message)", .system)
    }

    public func timeline(_ id: UUID? = nil) throws -> [ActivityEntry] {
        var entries = activity.enumerated().map { ($0.offset, $0.element) }
        if let id {
            let scope = Set(try get(id).selfAndDescendants.map(\.id))
            entries = entries.filter { scope.contains($0.1.itemId) }
        }
        return entries.sorted { $0.1.at != $1.1.at ? $0.1.at > $1.1.at : $0.0 > $1.0 }.map(\.1)
    }

    /// Edits fields; `deadline: .some(nil)` clears the deadline, `nil` leaves it unchanged.
    public func update(_ id: UUID, title: String? = nil, details: String? = nil, priority: Priority? = nil, deadline: Date?? = nil, actor: Actor = .user, now: Date) throws {
        let item = try get(id)
        let newTitle = try title.map { try Self.requireText($0, "A task title is required.") } ?? item.title
        item.title = newTitle
        if let details { item.details = Self.optionalText(details) }
        if let priority { item.priority = priority }
        if let deadline { item.deadline = deadline }
        log(now, id, "updated", "Updated \"\(newTitle)\"", actor)
    }

    // MARK: Groups

    @discardableResult
    public func addGroup(name: String, color: String? = nil, actor: Actor = .user, now: Date) throws -> TaskGroup {
        let clean = try validateGroup(name: name, color: color, except: nil)
        let group = TaskGroup(name: clean, color: color)
        groups.append(group)
        log(now, TaskBoard.boardScopeId, "groupChanged", "Added group \"\(clean)\"", actor)
        return group
    }

    public func moveToGroup(_ id: UUID, groupId: UUID, actor: Actor = .user, now: Date) throws {
        let item = try get(id)
        let target = try group(groupId)
        guard item.parent == nil else { throw BoardError.conflict("Only top-level tasks can change group; subtasks follow their parent.") }
        item.ownGroupId = target.id
        log(now, id, "moved", "Moved \"\(item.title)\" to \(target.name)", actor)
    }

    // MARK: Focus timer

    public func startFocus(_ itemId: UUID?, actor: Actor = .user, now: Date) throws {
        let item = try itemId.map { try get($0) }
        pomodoro.startFocus(now: now, itemId: item?.id)
        if let item { log(now, item.id, "focusStarted", "Focus started on \"\(item.title)\"", actor) }
    }

    public func pauseFocus(now: Date) { pomodoro.pause(now: now) }

    public func resumeFocus(now: Date) { pomodoro.resume(now: now) }

    public func skipFocus(now: Date) { pomodoro.skip(now: now) }

    public func resetFocus() { pomodoro.reset() }

    @discardableResult
    public func tickPomodoro(now: Date) -> [PomodoroEvent] {
        var events: [PomodoroEvent] = []
        while let evt = pomodoro.tick(now: now) {
            events.append(evt)
            if evt.kind == .focusCompleted, let id = evt.itemId, let item = index[id] {
                log(evt.at, id, "focusCompleted", "Focus session completed on \"\(item.title)\"", .system)
            }
        }
        return events
    }

    // MARK: Internals

    func attach(_ item: WorkItem, to parent: WorkItem?) throws {
        guard index[item.id] == nil else { throw BoardError.invalid("Duplicate task id \(item.id).") }
        index[item.id] = item
        item.parent = parent
        if let parent { parent.children.append(item) } else { items.append(item) }
    }

    func seedGroups() {
        groups.append(TaskGroup(name: "Work", color: "#3b82f6"))
        groups.append(TaskGroup(name: "Personal", color: "#22c55e"))
    }

    func appendLoaded(group: TaskGroup) { groups.append(group) }

    func appendLoaded(activity entry: ActivityEntry) { activity.append(entry) }

    /// The first unfinished earlier step blocking this item or any of its ancestors.
    static func findBlockingStep(_ item: WorkItem) -> WorkItem? {
        var current: WorkItem? = item
        while let c = current {
            if let blocker = blockingStep(c) { return blocker }
            current = c.parent
        }
        return nil
    }

    /// The earlier open sibling that must be finished first when the parent is sequential.
    static func blockingStep(_ item: WorkItem) -> WorkItem? {
        guard let parent = item.parent, parent.sequential, let firstOpen = parent.children.first(where: { !$0.isDone }) else { return nil }
        return firstOpen === item ? nil : firstOpen
    }

    private func advanceSequence(after completed: WorkItem, actor: Actor, now: Date) {
        guard let parent = completed.parent, parent.sequential else { return }
        guard let next = parent.children.first(where: { !$0.isDone }) else {
            parent.completedAt = now
            log(now, parent.id, "completed", "Completed \"\(parent.title)\" (all steps done)", .system)
            advanceSequence(after: parent, actor: actor, now: now)
            return
        }
        if let delay = parent.stepDelay {
            let gate = now.addingTimeInterval(delay)
            if next.nextActionAt == nil || next.nextActionAt! < gate {
                next.nextActionAt = gate
                log(now, next.id, "scheduled", "Unlocks after the step delay", actor)
            }
        }
    }

    private func dismissPendingScheduleReminders(_ item: WorkItem, now: Date) {
        // Replace earlier schedule reminders and silence ones already due (even if delivered), so "Later" really defers.
        for r in item.reminders where r.dismissedAt == nil && ((r.kind == .nextAction && r.notifiedAt == nil) || r.dueAt <= now) {
            r.dismissedAt = now
        }
    }

    private func log(_ at: Date, _ itemId: UUID, _ kind: String, _ summary: String, _ actor: Actor) {
        activity.append(ActivityEntry(at: at, itemId: itemId, kind: kind, summary: summary, actor: actor))
    }

    private func validateGroup(name: String, color: String?, except: TaskGroup?) throws -> String {
        let clean = try Self.requireText(name, "A group name is required.", maxLength: 60)
        if let color {
            let hex = color.dropFirst()
            guard color.hasPrefix("#"), hex.count == 6, hex.allSatisfy({ $0.isHexDigit }) else {
                throw BoardError.invalid("Group colors must be #rrggbb.")
            }
        }
        if groups.contains(where: { $0 !== except && $0.name.caseInsensitiveCompare(clean) == .orderedSame }) {
            throw BoardError.invalid("A group named \"\(clean)\" already exists.")
        }
        return clean
    }

    private static func defaultReminderMessage(_ item: WorkItem) -> String { "Time to act on: \(item.title)" }

    private static func validate(stepDelay: TimeInterval?) throws {
        if let d = stepDelay, d < 60 { throw BoardError.invalid("Step delay must be at least one minute.") }
    }

    static func requireText(_ value: String?, _ message: String, maxLength: Int = maxTitleLength) throws -> String {
        let trimmed = (value ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { throw BoardError.invalid(message) }
        // Count UTF-16 units like .NET so both platforms accept exactly the same titles.
        guard trimmed.utf16.count <= maxLength else { throw BoardError.invalid("Must be at most \(maxLength) characters.") }
        return trimmed
    }

    static func optionalText(_ value: String?) -> String? {
        let trimmed = (value ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }
}
