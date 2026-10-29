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
    /// The Do now order the person arranged (tasks not listed slot in by the automatic rules).
    public internal(set) var nowOrder: [UUID] = []
    /// When Do now was last arranged: a reminder going off after it brings its task back to the top.
    public internal(set) var nowArrangedAt: Date?
    /// How much energy there is in a day (nil: `Energy.defaultPerDay`); kept with the board, so every app uses it.
    public internal(set) var energyPerDay: Int?
    public var dailyEnergy: Int { energyPerDay ?? Energy.defaultPerDay }
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
        // A timer on what's deleted ends now. A subtask's time stays with its task (the work happened).
        stopTimers(item.selfAndDescendants, now: now)
        if let parent = item.parent {
            parent.children.removeAll { $0 === item }
            parent.timeEntries.append(contentsOf: item.selfAndDescendants.flatMap(\.timeEntries))
            parent.timeEntries.sort { $0.start < $1.start }
        } else {
            items.removeAll { $0 === item }
        }
        for removed in item.selfAndDescendants { index[removed.id] = nil }
        // Tasks waiting for what's gone come back, like when it's done.
        releaseWaiters(now: now, deleted: Dictionary(item.selfAndDescendants.map { ($0.id, $0.title) }, uniquingKeysWith: { a, _ in a }))
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
        if rollForward(item, actor: actor, now: now) {
            // What waited for this time of it comes back.
            releaseWaiters(now: now, finished: item.id)
            return
        }
        for d in item.selfAndDescendants where !d.isDone { d.completedAt = now }
        stopTimers(item.selfAndDescendants, now: now)
        log(now, id, "completed", "Completed \"\(item.title)\"", actor)
        advanceSequence(after: item, actor: actor, now: now)
        releaseWaiters(now: now)
    }

    /// Snoozes a task until another one is done ("after task X"); then it comes back with a reminder. Replaces a
    /// snooze until a time. Same rules as TodoTracker.Core.TaskBoard.WaitFor.
    public func waitFor(_ id: UUID, after afterId: UUID, actor: Actor = .user, now: Date) throws {
        let item = try get(id)
        let other = try get(afterId)
        if other === item { throw BoardError.invalid("A task can't wait for itself.") }
        if let problem = Self.waitProblem(item, other) { throw BoardError.conflict(problem) }
        item.afterId = other.id
        item.nextActionAt = nil
        dismissPendingScheduleReminders(item, now: now)
        log(now, id, "scheduled", "Waits until \"\(other.title)\" is done", actor)
    }

    /// Whether `id` may wait for `afterId` (what the pickers offer).
    public func canWaitFor(_ id: UUID, after afterId: UUID) -> Bool {
        guard let item = find(id), let other = find(afterId), other !== item else { return false }
        return Self.waitProblem(item, other) == nil
    }

    /// Why a task can't wait for another, or nil when it can.
    static func waitProblem(_ item: WorkItem, _ other: WorkItem) -> String? {
        if item.isDone { return "\"\(item.title)\" is already done." }
        if other.isDone { return "\"\(other.title)\" is already done." }
        // Its own subtasks or parents would never get done while it waits.
        let family = Set((item.selfAndDescendants + item.ancestors).map(\.id))
        if family.contains(other.id) { return "\"\(item.title)\" can't wait for \"\(other.title)\", which is part of it." }
        // Nor would a circle: follow what holds "other" up (what it, its parents and its open subtasks wait for, and its
        // open subtasks themselves); reaching this task's family means they'd wait for each other.
        var seen = Set<UUID>()
        var queue = [other]
        while !queue.isEmpty {
            let next = queue.removeFirst()
            guard seen.insert(next.id).inserted else { continue }
            if family.contains(next.id) { return "\"\(other.title)\" is already waiting for \"\(item.title)\": they'd wait for each other." }
            queue += ([next] + next.ancestors).compactMap(\.waitingFor) + next.children.filter { !$0.isDone }
        }
        return nil
    }

    /// Tasks whose wait ended (what they waited for is done, or was just deleted) come back, with a reminder saying why.
    private func releaseWaiters(now: Date, deleted: [UUID: String] = [:], finished: UUID? = nil) {
        for waiter in allItems {
            guard let afterId = waiter.afterId else { continue }
            let why: String
            if let other = index[afterId] {
                guard other.isDone || afterId == finished else { continue }
                why = "\"\(other.title)\" is done"
            } else {
                // Only one deleted just now: a task merely missing (not synced yet) doesn't end a wait.
                guard let title = deleted[afterId] else { continue }
                why = "\"\(title)\" was deleted"
            }
            waiter.afterId = nil
            if !waiter.isDone {
                waiter.reminders.append(Reminder(dueAt: now, message: "\(why): back to \"\(waiter.title)\"", kind: .nextAction))
                log(now, waiter.id, "scheduled", "Back: \(why)", .system)
            }
        }
    }

    public func reopen(_ id: UUID, actor: Actor = .user, now: Date) throws {
        let item = try get(id)
        guard let completedAt = item.completedAt else {
            if wasRolledForward(item) {
                undoRollForward(item, actor: actor, now: now)
            } else if let rolled = rolledBy(item) {
                // The last step of a repeating task moved the whole task on (and opened the step again): "not done after
                // all" on that step puts the task back as it was, with this step open.
                undoRollForward(rolled, actor: actor, now: now, keepOpen: item)
            }
            return
        }
        item.completedAt = nil
        item.root.archivedAt = nil
        var reopened = [item]
        // Undo the cascade from completing a parent: children finished by that same action reopen too.
        for d in item.selfAndDescendants.dropFirst() where d.completedAt == completedAt { d.completedAt = nil; reopened.append(d) }
        for a in item.ancestors where a.isDone { a.completedAt = nil; reopened.append(a) }
        for back in reopened { bringBack(back, now: now) }
        log(now, id, "reopened", "Reopened \"\(item.title)\"", actor)
    }

    /// Reopening means "I want to do this again": back in Do now, not hidden behind a snooze or a wait set before it was
    /// done (it looked as if Reopen did nothing). Only a snooze or wait still in effect is cleared, with its pending
    /// snooze reminder; reminders set by hand stay.
    private func bringBack(_ item: WorkItem, now: Date) {
        let snoozed = item.nextActionAt.map { $0 > now } ?? false
        guard snoozed || item.afterId != nil else { return }
        let hidden = snoozed || item.waitingFor != nil
        item.nextActionAt = nil
        item.afterId = nil
        guard hidden else { return }
        for r in item.reminders where r.kind == .nextAction && r.dismissedAt == nil { r.dismissedAt = now }
    }

    // MARK: Repeating tasks (same rules as TodoTracker.Core.TaskBoard.Repeat.cs)

    /// Where the user lives: a repeating task's next time is a calendar day there.
    public var timeZone: TimeZone = .current

    private var calendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        return calendar
    }

    private func localDay(_ date: Date) -> LocalDay {
        let c = calendar.dateComponents([.year, .month, .day], from: date)
        return LocalDay(year: c.year!, month: c.month!, day: c.day!)
    }

    private func localTime(_ date: Date) -> DateComponents { calendar.dateComponents([.hour, .minute, .second], from: date) }

    private func at(_ day: LocalDay, _ time: DateComponents) -> Date {
        calendar.date(from: DateComponents(year: day.year, month: day.month, day: day.day, hour: time.hour ?? 0, minute: time.minute ?? 0, second: time.second ?? 0))!
    }

    /// Done with this time of a repeating task: it moves on to its next time instead of finishing (deadline to that day
    /// at the same time; it waits until then, the same days before the deadline and at the same time as it waited
    /// before, else from the start of that day), its subtasks open again and this time's reminders and timer end.
    /// False when it doesn't repeat (any more): then it's simply done.
    private func rollForward(_ item: WorkItem, actor: Actor, now: Date) -> Bool {
        // A step of an in-order task doesn't repeat on its own (it would hold up the steps after it for good).
        guard let repeatRule = item.repeatRule, repeatRule.count != 1, item.parent?.sequential != true else { return false }
        let today = localDay(now)
        let scheduled = item.deadline ?? item.nextActionAt
        let anchor = repeatRule.whenDone || scheduled == nil ? today : localDay(scheduled!)
        // Its day is fixed from its first time on (month ends, leap days and snoozes don't move it).
        let rule = repeatRule.pinned(to: anchor)
        guard let next = rule.next(anchor: anchor, after: max(anchor, today)) else { return false }

        item.repeatUndo = RepeatUndo(deadline: item.deadline, nextActionAt: item.nextActionAt, plannedAt: item.plannedAt)
        let waitedUntil = item.nextActionAt
        let reminded = waitedUntil.map { w in item.reminders.contains { $0.kind == .nextAction && $0.dueAt == w } } ?? false
        var lead = 0
        if let due = item.deadline, let waited = waitedUntil { lead = max(0, localDay(due).epochDay - localDay(waited).epochDay) }
        // A planned block moves along too (as many days from the deadline, at the same time).
        if let planned = item.plannedAt {
            item.plannedAt = at(next.adding(days: localDay(planned).epochDay - anchor.epochDay), localTime(planned))
        }
        if let due = item.deadline { item.deadline = at(next, localTime(due)) }
        let back = waitedUntil.map { at(next.adding(days: -lead), localTime($0)) } ?? at(next, DateComponents(hour: 0, minute: 0, second: 0))
        item.nextActionAt = back
        item.afterId = nil
        dismissPendingScheduleReminders(item, now: now)
        for r in item.reminders where r.dismissedAt == nil && r.dueAt < back { r.dismissedAt = now }
        if reminded && back > now { item.reminders.append(Reminder(dueAt: back, message: Self.defaultReminderMessage(item), kind: .nextAction)) }
        for d in item.selfAndDescendants.dropFirst() { d.completedAt = nil }
        stopTimers(item.selfAndDescendants, now: now)
        item.repeatRule = rule.countedDown()
        let names = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]
        let months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"]
        log(now, item.id, "completed", "Completed \"\(item.title)\" (repeats: next \(names[next.weekday - 1]) \(next.day) \(months[next.month - 1]))", actor)
        return true
    }

    /// Whether this repeating task's last change was moving on to its next time (so "not done after all" can undo it).
    public func wasRolledForward(_ item: WorkItem) -> Bool {
        guard item.repeatRule != nil, !item.isDone else { return false }
        return activity.last(where: { $0.itemId == item.id && ($0.kind == "completed" || $0.kind == "reopened") })?.kind == "completed"
    }

    /// The repeating task that completing `step` (its last step) moved on to its next time, or nil.
    private func rolledBy(_ step: WorkItem) -> WorkItem? {
        guard let done = activity.last(where: { $0.itemId == step.id && ($0.kind == "completed" || $0.kind == "reopened") }),
              done.kind == "completed" else { return nil }
        return step.ancestors.first { ancestor in
            wasRolledForward(ancestor) && activity.last(where: { $0.itemId == ancestor.id && $0.kind == "completed" })?.at == done.at
        }
    }

    /// "Not done after all" on a repeating task that moved on: its deadline and wait as they were (without that record:
    /// due a time earlier, back in Do now), and what moving on undid as it was: the subtasks done before (all but
    /// `keepOpen`) done again, and the reminders it put away pending again. Same rule as C# and Kotlin
    /// (tests/fixtures/undo.json).
    private func undoRollForward(_ item: WorkItem, actor: Actor, now: Date, keepOpen: WorkItem? = nil) {
        guard let rule = item.repeatRule else { return }
        let rolls = activity.filter { $0.itemId == item.id && $0.kind == "completed" }
        if let rolledAt = rolls.last?.at {
            let since = rolls.count > 1 ? rolls[rolls.count - 2].at : nil
            let keep = Set(keepOpen?.selfAndDescendants.map(\.id) ?? [])
            for descendant in item.selfAndDescendants.dropFirst() where !keep.contains(descendant.id) && !descendant.isDone {
                // Done by itself (or with a parent below this task) since the time before it moved on, and not reopened.
                var lineage: Set<UUID> = [descendant.id]
                for ancestor in descendant.ancestors {
                    if ancestor === item { break }
                    lineage.insert(ancestor.id)
                }
                let last = activity.last { entry in
                    lineage.contains(entry.itemId) && (entry.kind == "completed" || entry.kind == "reopened")
                        && entry.at <= rolledAt && (since.map { entry.at > $0 } ?? true)
                }
                if let last, last.kind == "completed" { descendant.completedAt = last.at }
            }
            // Put away when it moved on (not the reminder for its next time, which ends below).
            for r in item.reminders where r.dismissedAt == rolledAt && r.dueAt < (item.nextActionAt ?? .distantFuture) { r.dismissedAt = nil }
        }
        for r in item.reminders where r.kind == .nextAction && r.dismissedAt == nil && r.dueAt > now { r.dismissedAt = now }
        if let undo = item.repeatUndo {
            item.deadline = undo.deadline
            item.nextActionAt = undo.nextActionAt
            item.plannedAt = undo.plannedAt ?? item.plannedAt
        } else {
            if let due = item.deadline {
                let day = localDay(due)
                if let before = rule.previous(anchor: day, before: day) { item.deadline = at(before, localTime(due)) }
            }
            item.nextActionAt = nil
        }
        item.repeatUndo = nil
        item.repeatRule = rule.countedUp()
        log(now, item.id, "reopened", "Reopened \"\(item.title)\" (not done after all)", actor)
    }

    /// Defers the item until `at`; with `notify`, a reminder fires at that time.
    public func scheduleNextAction(_ id: UUID, at: Date, notify: Bool = false, message: String? = nil, actor: Actor = .user, now: Date) throws {
        let item = try get(id)
        item.nextActionAt = at
        item.afterId = nil
        dismissPendingScheduleReminders(item, now: now)
        if notify {
            item.reminders.append(Reminder(dueAt: at, message: Self.optionalText(message) ?? Self.defaultReminderMessage(item), kind: .nextAction))
        }
        log(now, id, "scheduled", "Next action \(ISO8601DateFormatter().string(from: at))\(notify ? " (with reminder)" : "")", actor)
    }

    public func clearNextAction(_ id: UUID, actor: Actor = .user, now: Date) throws {
        let item = try get(id)
        item.nextActionAt = nil
        item.afterId = nil
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

    /// How much energy a task takes per hour (1 to 3; nil: not said).
    public func setEnergy(_ id: UUID, level: Int?, actor: Actor = .user, now: Date) throws {
        let item = try get(id)
        if let level, !Energy.levels.contains(level) { throw BoardError.invalid("Energy is 1 to 3 (⚡ to ⚡⚡⚡ an hour).") }
        item.energy = level
        log(now, id, "updated", "Updated \"\(item.title)\"", actor)
    }

    /// How much energy there is in a day (nil: back to the default).
    public func setEnergyPerDay(_ perDay: Int?) throws {
        if let perDay, !(1...Energy.maxPerDay).contains(perDay) { throw BoardError.invalid("A day's energy is 1 to \(Energy.maxPerDay) ⚡.") }
        energyPerDay = perDay
    }

    /// Records the order of the tasks someone sees in Do now (TaskBoard.ArrangeNow in C#, same rules): only the places
    /// those tasks already had are reused, so arranging one group's tab doesn't move the other groups' tasks.
    public func arrangeNow(_ ids: [UUID], now: Date) throws {
        guard Set(ids).count == ids.count else { throw BoardError.invalid("Each task can appear only once.") }
        for id in ids { _ = try get(id) }
        let arranged = Set(ids)
        let slots = nowOrder.indices.filter { arranged.contains(nowOrder[$0]) }
        var result: [UUID]
        if slots.count == ids.count {
            // Same set of places: keep them, fill them in the new order.
            result = nowOrder
            for (k, slot) in slots.enumerated() { result[slot] = ids[k] }
        } else {
            let start = nowOrder.firstIndex(where: arranged.contains)
            let at = start.map { nowOrder[..<$0].filter { !arranged.contains($0) }.count } ?? 0
            result = nowOrder.filter { !arranged.contains($0) }
            result.insert(contentsOf: ids, at: at)
        }
        nowOrder = result.filter { find($0).map { !$0.isDone } ?? false }
        nowArrangedAt = now
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
        try update(id, TaskChanges(title: title, details: details, priority: priority, deadline: deadline), actor: actor, now: now)
    }

    /// Edits the fields `changes` names (TaskBoard.Update in C#): all are checked first, so a field that isn't valid
    /// changes nothing.
    public func update(_ id: UUID, _ changes: TaskChanges, actor: Actor = .user, now: Date) throws {
        let item = try get(id)
        let newTitle = try changes.title.map { try Self.requireText($0, "A task title is required.") } ?? item.title
        if let details = changes.details, details.utf16.count > Self.maxTextLength {
            throw BoardError.invalid("Details must be at most \(Self.maxTextLength) characters.")
        }
        if let delay = changes.stepDelay { try Self.validate(stepDelay: delay) }
        var rule: Recurrence?
        if let text = changes.repeatRule, Self.optionalText(text) != nil {
            do {
                rule = try Recurrence.parse(text)
            } catch {
                throw BoardError.invalid((error as? LocalizedError)?.errorDescription ?? "That repeat rule isn't understood.")
            }
        }

        item.title = newTitle
        if let details = changes.details { item.details = Self.optionalText(details) }
        if let priority = changes.priority { item.priority = priority }
        if let deadline = changes.deadline { item.deadline = deadline }
        if let sequential = changes.sequential { item.sequential = sequential }
        if let delay = changes.stepDelay { item.stepDelay = delay }
        if changes.repeatRule != nil { item.repeatRule = rule }
        log(now, id, "updated", "Updated \"\(newTitle)\"", actor)
    }

    public static let maxTextLength = 10_000

    // MARK: Archive (same rules as TodoTracker.Core.TaskBoard.Archive)

    /// Puts a finished top-level task away: out of every list, kept (never deleted).
    public func archive(_ id: UUID, actor: Actor = .user, now: Date) throws {
        let item = try get(id)
        if item.parent != nil { throw BoardError.conflict("Subtasks are archived with their task.") }
        if !item.isDone { throw BoardError.conflict("Finish \"\(item.title)\" before archiving it.") }
        if item.archivedAt != nil { return }
        item.archivedAt = now
        log(now, id, "archived", "Archived \"\(item.title)\"", actor)
    }

    /// Brings an archived task back (into Done).
    public func unarchive(_ id: UUID, actor: Actor = .user, now: Date) throws {
        let item = try get(id)
        guard item.archivedAt != nil else { return }
        item.archivedAt = nil
        log(now, id, "archived", "Brought \"\(item.title)\" back from the archive", actor)
    }

    /// The focus timer's lengths (a session, the breaks, and how many sessions before a long break).
    public func setPomodoroSettings(_ settings: PomodoroSettings) throws {
        guard (1...180).contains(settings.focusMinutes), (1...60).contains(settings.shortBreakMinutes),
              (1...120).contains(settings.longBreakMinutes), (1...12).contains(settings.focusesBeforeLongBreak) else {
            throw BoardError.invalid("Focus is 1 to 180 minutes, a break 1 to 60, a long break 1 to 120, every 1 to 12 sessions.")
        }
        pomodoro.settings = settings
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

    /// Renames or recolors a group (`color` nil: none).
    public func updateGroup(_ groupId: UUID, name: String, color: String?, actor: Actor = .user, now: Date) throws {
        let group = try group(groupId)
        let clean = try validateGroup(name: name, color: color, except: group)
        group.name = clean
        group.color = color
        log(now, TaskBoard.boardScopeId, "groupChanged", "Updated group \"\(clean)\"", actor)
    }

    /// Deletes a group; its tasks move to `moveTasksTo` (nothing is lost).
    public func deleteGroup(_ groupId: UUID, moveTasksTo: UUID, actor: Actor = .user, now: Date) throws {
        let group = try group(groupId)
        if groups.count == 1 { throw BoardError.conflict("At least one group is required.") }
        let target = try self.group(moveTasksTo)
        if target === group { throw BoardError.conflict("Choose a different group to move the tasks into.") }
        for root in items where root.ownGroupId == group.id { root.ownGroupId = target.id }
        groups.removeAll { $0 === group }
        log(now, TaskBoard.boardScopeId, "groupChanged", "Deleted group \"\(group.name)\"", actor)
    }

    public func moveToGroup(_ id: UUID, groupId: UUID, actor: Actor = .user, now: Date) throws {
        let item = try get(id)
        let target = try group(groupId)
        guard item.parent == nil else { throw BoardError.conflict("Only top-level tasks can change group; subtasks follow their parent.") }
        item.ownGroupId = target.id
        log(now, id, "moved", "Moved \"\(item.title)\" to \(target.name)", actor)
    }

    // MARK: Focus timer

    /// Starts a focus session (on a task, timed as focus time on it, like the task's own timer).
    public func startFocus(_ itemId: UUID?, actor: Actor = .user, now: Date) throws {
        let item = try itemId.map { try get($0) }
        if let item, item.isDone { throw BoardError.conflict("\"\(item.title)\" is already done.") }
        stopFocusTimer(now)
        pomodoro.startFocus(now: now, itemId: item?.id)
        if let item {
            startTimerCore(item, source: .focus, now: now)
            log(now, item.id, "focusStarted", "Focus started on \"\(item.title)\"", actor)
        }
    }

    public func pauseFocus(now: Date) {
        if pomodoro.phase == .focus { stopFocusTimer(now) }
        pomodoro.pause(now: now)
    }

    public func resumeFocus(now: Date) {
        let resuming = pomodoro.phase == .focus && !pomodoro.isRunning
        pomodoro.resume(now: now)
        // A timer started by hand meanwhile keeps running: the session goes on without timing its task.
        if resuming, let id = pomodoro.itemId, let item = index[id], !item.isDone, runningTimer(now) == nil {
            startTimerCore(item, source: .focus, now: now)
        }
    }

    /// A session that has already run out ends on time first, so Skip skips the break that followed it.
    public func skipFocus(now: Date) {
        tickPomodoro(now: now)
        if pomodoro.phase == .focus { stopFocusTimer(now) }
        pomodoro.skip(now: now)
    }

    public func resetFocus(now: Date) {
        stopFocusTimer(now)
        pomodoro.reset()
    }

    /// The task "Start next focus" picks up: the last session's, if it's still there and open.
    public func nextFocusItem() -> WorkItem? {
        guard let id = pomodoro.itemId, let item = index[id], !item.isDone else { return nil }
        return item
    }

    /// "Start next focus": ends the break and starts a session on the last session's task, else `fallback`. A session
    /// that just ran out counts first; one still running is never restarted. Same as TodoTracker.Core.
    @discardableResult
    public func startNextFocus(fallback: UUID?, actor: Actor = .user, now: Date) throws -> WorkItem? {
        tickPomodoro(now: now)
        if pomodoro.phase == .focus { throw BoardError.conflict("A focus session is already going.") }
        let item = nextFocusItem() ?? fallback.flatMap { index[$0] }.flatMap { $0.isDone ? nil : $0 }
        try startFocus(item?.id, actor: actor, now: now)
        return item
    }

    @discardableResult
    public func tickPomodoro(now: Date) -> [PomodoroEvent] {
        var events: [PomodoroEvent] = []
        while let evt = pomodoro.tick(now: now) {
            events.append(evt)
            if evt.kind == .focusCompleted {
                // The session ended at its end time, whenever this runs (after sleep, say).
                stopFocusTimer(evt.at)
                if let id = evt.itemId, let item = index[id] {
                    log(evt.at, id, "focusCompleted", "Focus session completed on \"\(item.title)\"", .system)
                }
            }
        }
        return events
    }

    // MARK: Internals

    func attach(_ item: WorkItem, to parent: WorkItem?) throws {
        guard index[item.id] == nil else { throw BoardError.invalid("Duplicate task id \(item.id).") }
        index[item.id] = item
        item.parent = parent
        item.board = self
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
            // Every step done: a repeating task moves on to its next time (its steps open again).
            if rollForward(parent, actor: actor, now: now) {
                releaseWaiters(now: now, finished: parent.id)
                return
            }
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

    func log(_ at: Date, _ itemId: UUID, _ kind: String, _ summary: String, _ actor: Actor) {
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
