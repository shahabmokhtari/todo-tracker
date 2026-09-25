#if canImport(SwiftUI)
import Foundation
import SwiftUI
import TodoTrackerKit
#if canImport(UserNotifications)
import UserNotifications
#endif

/// View model for the iOS and macOS apps: owns the local board, persists after every change,
/// ticks the focus timer, and keeps local notifications in sync with pending reminders.
@MainActor
public final class BoardModel: ObservableObject {
    @Published public private(set) var dashboard: Dashboard
    @Published public private(set) var now = Date()
    @Published public var selectedGroupId: UUID?
    @Published public var quickText = ""
    @Published public var status: String?

    public let board: TaskBoard
    private let store: BoardFileStore?
    private var timer: Timer?

    public init(store: BoardFileStore?) {
        self.store = store
        let loaded = (try? store?.load()) ?? TaskBoard()
        board = loaded
        dashboard = Agenda.build(loaded, now: Date())
        refresh()
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.tick() }
        }
    }

    public static func live() -> BoardModel {
        BoardModel(store: try? BoardFileStore(url: BoardFileStore.defaultURL()))
    }

    public static func requestNotificationPermission() { NotificationScheduler.requestAuthorization() }

    public var groups: [TaskGroup] { board.groups }

    public func count(for groupId: UUID?) -> Int {
        guard let groupId else { return dashboard.groupCounts.values.reduce(0) { $0 + $1.now } }
        return dashboard.groupCounts[groupId]?.now ?? 0
    }

    public func refresh() {
        if let g = selectedGroupId, !board.groups.contains(where: { $0.id == g }) { selectedGroupId = nil }
        now = Date()
        dashboard = Agenda.build(board, now: now, recentNoteCount: 6, groupId: selectedGroupId)
    }

    public func select(group: UUID?) {
        selectedGroupId = group
        refresh()
    }

    // MARK: Actions

    public func capture() {
        let text = quickText
        mutate("Added") {
            let capture = try QuickCaptureParser.parse(text, now: $0)
            let item = try board.addTask(NewTask(capture.title, groupId: selectedGroupId, priority: capture.priority, deadline: capture.deadline), now: $0)
            if let at = capture.nextActionAt { try board.scheduleNextAction(item.id, at: at, notify: true, now: $0) }
            quickText = ""
        }
    }

    public func complete(_ id: UUID) { mutate("Done 🎉") { try board.complete(id, now: $0) } }

    public func snooze(_ id: UUID, minutes: Int) {
        mutate("Snoozed") { try board.scheduleNextAction(id, at: $0.addingTimeInterval(TimeInterval(minutes * 60)), notify: true, now: $0) }
    }

    public func reopen(_ id: UUID) { mutate("Reopened") { try board.reopen(id, now: $0) } }

    public func bringBack(_ id: UUID) { mutate(nil) { try board.clearNextAction(id, now: $0) } }

    public func dismissReminder(_ id: UUID, reminderId: UUID) { mutate(nil) { try board.dismissReminder(id, reminderId: reminderId, now: $0) } }

    public func addNote(_ id: UUID, text: String) { mutate("Note saved") { _ = try board.addNote(id, text: text, now: $0) } }

    public func addSubtask(_ parentId: UUID, title: String) { mutate("Subtask added") { _ = try board.addTask(NewTask(title, parentId: parentId), now: $0) } }

    public func addSteps(_ parentId: UUID, titles: [String], hoursBetween: Double) {
        mutate("Steps added") { _ = try board.addSteps(parentId: parentId, titles: titles, stepDelay: hoursBetween > 0 ? hoursBetween * 3600 : nil, now: $0) }
    }

    public func delete(_ id: UUID) { mutate("Deleted") { try board.delete(id, now: $0) } }

    public func addGroup(_ name: String) {
        mutate(nil) { selectedGroupId = try board.addGroup(name: name, now: $0).id }
    }

    public func startFocus(_ id: UUID?) { mutate(nil) { try board.startFocus(id ?? dashboard.focus?.item.id, now: $0) } }

    public func pausePomodoro() { mutate(nil) { board.pauseFocus(now: $0) } }

    public func resumePomodoro() { mutate(nil) { board.resumeFocus(now: $0) } }

    public func skipPomodoro() { mutate(nil) { board.skipFocus(now: $0) } }

    public func resetPomodoro() { mutate(nil) { _ in board.resetFocus() } }

    public var pomodoroText: String {
        let seconds = Int(board.pomodoro.remaining(now).rounded(.up))
        return String(format: "%02d:%02d", seconds / 60, seconds % 60)
    }

    // MARK: Plumbing

    private func mutate(_ message: String?, _ change: (Date) throws -> Void) {
        do {
            try change(Date())
            status = message
            persist()
        } catch {
            status = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        }
        refresh()
    }

    private func persist() {
        do {
            try store?.save(board)
        } catch {
            status = "Could not save: \(error.localizedDescription)"
        }
        NotificationScheduler.sync(board)
    }

    private func tick() {
        let current = Date()
        let events = board.tickPomodoro(now: current)
        let dueUnnotified = board.allItems.filter { !$0.isDone }.flatMap { item in
            item.reminders.filter { $0.isDue(current) && $0.notifiedAt == nil }.map { (item.id, $0.id) }
        }
        for (itemId, reminderId) in dueUnnotified { board.markNotified(itemId, reminderId: reminderId, now: current) }
        if !events.isEmpty || !dueUnnotified.isEmpty { persist() }
        // Refresh the lists every 30 s (waiting items wake up); the timer text updates every second.
        if !events.isEmpty || !dueUnnotified.isEmpty || Int(current.timeIntervalSince1970) % 30 == 0 {
            refresh()
        } else {
            now = current
        }
    }
}

/// Mirrors pending reminders into local notifications (the OS delivers them even when the app is closed).
enum NotificationScheduler {
    static func requestAuthorization() {
        #if canImport(UserNotifications)
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound, .badge]) { _, _ in }
        #endif
    }

    static func sync(_ board: TaskBoard) {
        #if canImport(UserNotifications)
        guard Bundle.main.bundleIdentifier != nil else { return }
        let now = Date()
        let pending = board.allItems.filter { !$0.isDone }
            .flatMap { item in item.reminders.filter { $0.isPending && $0.notifiedAt == nil && $0.dueAt > now }.map { (item, $0) } }
            .sorted { $0.1.dueAt < $1.1.dueAt }
            .prefix(60) // iOS keeps at most 64 pending local notifications.
        let center = UNUserNotificationCenter.current()
        center.removeAllPendingNotificationRequests()
        for (item, reminder) in pending {
            let content = UNMutableNotificationContent()
            content.title = "⏰ \(item.title)"
            content.body = reminder.message
            content.sound = .default
            content.threadIdentifier = item.root.id.uuidString
            let seconds = max(1, reminder.dueAt.timeIntervalSince(now))
            let trigger = UNTimeIntervalNotificationTrigger(timeInterval: seconds, repeats: false)
            center.add(UNNotificationRequest(identifier: reminder.id.uuidString, content: content, trigger: trigger))
        }
        #endif
    }
}

extension WorkItem {
    var root: WorkItem { ancestors.last ?? self }
}
#endif
