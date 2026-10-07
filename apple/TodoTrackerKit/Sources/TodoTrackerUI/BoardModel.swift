#if canImport(SwiftUI)
import Foundation
import SwiftUI
import TodoTrackerKit
#if canImport(UserNotifications)
import UserNotifications
#endif

/// View model for the iOS and macOS apps. On its own it owns a board on this device: persists after every change,
/// ticks the focus timer, and keeps local notifications in sync with pending reminders. On a Mac it uses Todo Tracker's
/// server instead (`use(server:)`): the tasks folder, synced with the other computers; changes go to the server and the
/// board is read back from it.
@MainActor
public final class BoardModel: ObservableObject {
    @Published public private(set) var dashboard: Dashboard
    @Published public private(set) var now = Date()
    @Published public var selectedGroupId: UUID?
    @Published public var quickText = ""
    @Published public var status: String?
    /// Note drafts and open note boxes live here (not in row views) so they survive a card moving between lists.
    @Published public var noteDrafts: [UUID: String] = [:]
    @Published public var openNotes: Set<UUID> = []
    /// True when the saved board could not be read; nothing is written so the user's data is never overwritten.
    @Published public private(set) var isReadOnly = false

    /// The server in use (nil: the board lives on this device only).
    @Published public private(set) var serverURL: URL?
    /// Opening the tasks folder through the server (Mac): changes wait.
    @Published public private(set) var isConnecting = false

    public private(set) var board: TaskBoard
    private let store: BoardFileStore?
    private var timer: Timer?
    private var lastRefresh = Date.distantPast
    private var server: ServerClient?
    /// Bumped by every change and every read: a read only lands if nothing happened since it started (so an older
    /// board never replaces a newer one).
    private var generation = 0
    private var changesInFlight = 0
    private var readsInFlight = 0
    private var readFailed = false
    private var etag: String?
    private var themeSettled = false

    public init(store: BoardFileStore?) {
        self.store = store
        var loadError: String?
        let loaded: TaskBoard
        do {
            loaded = try store?.load() ?? TaskBoard()
        } catch {
            loaded = TaskBoard()
            loadError = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        }
        board = loaded
        dashboard = Agenda.build(loaded, now: Date())
        if let loadError {
            isReadOnly = true
            status = "Couldn't open your saved board (\(loadError)). Changes are disabled so nothing gets overwritten."
        }
        refresh()
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.tick() }
        }
    }

    public static func live() -> BoardModel {
        BoardModel(store: try? BoardFileStore(url: BoardFileStore.defaultURL()))
    }

    public static func requestNotificationPermission() { NotificationScheduler.requestAuthorization() }

    // MARK: Server (Mac)

    public var usesServer: Bool { serverURL != nil }

    /// Opening the tasks folder (the server is starting): nothing changes meanwhile, so nothing is written to the board
    /// on this Mac that the server may be moving into the folder right now.
    public func beginConnecting() {
        isConnecting = true
        status = "Opening your tasks folder…"
    }

    /// Still starting after a while (the first time, macOS may be asking whether Todo Tracker may use Documents).
    public func connectingSlowly() {
        guard isConnecting else { return }
        status = "Still opening your tasks folder… If macOS asks whether Todo Tracker may use your Documents folder, allow it."
    }

    /// Use Todo Tracker's server from now on: its tasks folder, synced with the other computers.
    public func use(server client: ServerClient) {
        server = client
        serverURL = client.baseURL
        isConnecting = false
        isReadOnly = false
        etag = nil
        themeSettled = false
        status = nil
        Task { await reload() }
    }

    /// The server is gone for now (it's being restarted): say so; changes wait until it's back.
    public func serverLost(_ reason: String) {
        isConnecting = true
        status = reason
    }

    /// The server couldn't be used: say so. The board on this Mac is used, unless it already moved into the tasks
    /// folder (then it's an old copy: shown, but not changed, so nothing goes astray).
    public func reportLocalOnly(_ reason: String) {
        isConnecting = false
        if store?.wasMigrated == true {
            isReadOnly = true
            status = "Your tasks are in your tasks folder, but Todo Tracker's server isn't running (\(reason)). Changes are off until it is."
        } else {
            status = "Not syncing: \(reason). Your tasks stay on this Mac for now."
        }
    }

    /// A sign-in link for the full window in the browser (board, reports, settings, sync).
    public func launchURL(returnPath: String = "/") async -> URL? {
        guard let server else { return nil }
        do {
            return try await server.launchURL(returnPath: returnPath)
        } catch {
            status = Self.describe(error)
            return nil
        }
    }

    /// Light, dark or the system's (picked by the user here): shared with every window when the server is in use.
    public func setTheme(_ theme: String) {
        if UserDefaults.standard.string(forKey: AppTheme.storageKey) != theme {
            UserDefaults.standard.set(theme, forKey: AppTheme.storageKey)
        }
        guard let server else { return }
        // A read already on its way would bring the old theme back.
        generation += 1
        themeSettled = true
        Task { _ = try? await server.perform(.setTheme(theme)) }
    }

    /// Reads the board (and the shared theme) from the server, unless a change happened meanwhile.
    private func reload() async {
        guard let server else { return }
        generation += 1
        let mine = generation
        readsInFlight += 1
        defer { readsInFlight -= 1 }
        do {
            let (fresh, tag) = try await server.board(ifChangedFrom: etag)
            let theme = try? await server.theme()
            guard mine == generation else { return }
            etag = tag
            if let fresh {
                board = fresh
                NotificationScheduler.sync(board)
            }
            if let theme { adopt(theme: theme, from: server) }
            if readFailed {
                readFailed = false
                status = nil
            }
            refresh()
        } catch {
            guard mine == generation else { return }
            readFailed = true
            status = Self.describe(error)
        }
    }

    /// The shared theme. The first time, a choice made on this Mac wins over the server's untouched default.
    private func adopt(theme: String, from server: ServerClient) {
        let local = UserDefaults.standard.string(forKey: AppTheme.storageKey) ?? AppTheme.system.rawValue
        if !themeSettled {
            themeSettled = true
            if theme == AppTheme.system.rawValue && local != theme {
                Task { _ = try? await server.perform(.setTheme(local)) }
                return
            }
        }
        if local != theme { UserDefaults.standard.set(theme, forKey: AppTheme.storageKey) }
    }

    /// Sends a change to the server, then reads the board back.
    private func send(_ action: ServerAction, _ message: String?, onDone: ((Data) -> Void)? = nil, onFail: (() -> Void)? = nil) {
        guard let server else { return }
        changesInFlight += 1
        generation += 1
        Task {
            do {
                let data = try await server.perform(action)
                status = message
                onDone?(data)
            } catch {
                status = Self.describe(error)
                onFail?()
            }
            await reload()
            changesInFlight -= 1
        }
    }

    /// While the tasks folder is opening, changes wait (says so). True when it's opening.
    private func waitWhileConnecting() -> Bool {
        guard isConnecting else { return false }
        status = "One moment: opening your tasks folder…"
        return true
    }

    /// A change: to the server when one is in use, else to the board on this device.
    @discardableResult
    private func change(_ message: String?, server action: @autoclosure () -> ServerAction, local: (Date) throws -> Void) -> Bool {
        if waitWhileConnecting() { return false }
        if server != nil {
            send(action(), message)
            return true
        }
        return mutate(message, local)
    }
    private static func describe(_ error: Error) -> String {
        (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
    }

    public var groups: [TaskGroup] { board.groups }

    public func count(for groupId: UUID?) -> Int {
        guard let groupId else { return dashboard.groupCounts.values.reduce(0) { $0 + $1.now } }
        return dashboard.groupCounts[groupId]?.now ?? 0
    }

    public func refresh() {
        if let g = selectedGroupId, !board.groups.contains(where: { $0.id == g }) { selectedGroupId = nil }
        now = Date()
        lastRefresh = now
        dashboard = Agenda.build(board, now: now, recentNoteCount: 6, groupId: selectedGroupId)
    }

    public func select(group: UUID?) {
        selectedGroupId = group
        refresh()
    }

    // MARK: Actions

    public func capture() {
        let text = quickText
        if waitWhileConnecting() { return }
        if server != nil {
            guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
            quickText = ""
            send(.capture(text: text, groupId: selectedGroupId), "Added", onFail: { [weak self] in
                if self?.quickText.isEmpty == true { self?.quickText = text }
            })
            return
        }
        mutate("Added") {
            let capture = try QuickCaptureParser.parse(text, now: $0)
            let item = try board.addTask(NewTask(capture.title, groupId: selectedGroupId, priority: capture.priority, deadline: capture.deadline), now: $0)
            if let at = capture.nextActionAt { try board.scheduleNextAction(item.id, at: at, notify: true, now: $0) }
            quickText = ""
        }
    }

    public func complete(_ id: UUID) { change("Done 🎉", server: .complete(id)) { try board.complete(id, now: $0) } }

    public func snooze(_ id: UUID, minutes: Int) {
        let at = Date().addingTimeInterval(TimeInterval(minutes * 60))
        change("Snoozed", server: .schedule(id, at: at)) { try board.scheduleNextAction(id, at: $0.addingTimeInterval(TimeInterval(minutes * 60)), notify: true, now: $0) }
    }

    public func reopen(_ id: UUID) { change("Reopened", server: .reopen(id)) { try board.reopen(id, now: $0) } }

    public func bringBack(_ id: UUID) { change(nil, server: .bringBack(id)) { try board.clearNextAction(id, now: $0) } }

    public func dismissReminder(_ id: UUID, reminderId: UUID) {
        change(nil, server: .dismissReminder(id, reminderId: reminderId)) { try board.dismissReminder(id, reminderId: reminderId, now: $0) }
    }

    public func addNote(_ id: UUID, text: String) {
        if waitWhileConnecting() { return }
        if server != nil {
            noteDrafts[id] = nil
            openNotes.remove(id)
            send(.addNote(id, text: text), "Note saved", onFail: { [weak self] in
                self?.noteDrafts[id] = text
                self?.openNotes.insert(id)
            })
            return
        }
        if mutate("Note saved", { _ = try board.addNote(id, text: text, now: $0) }) {
            noteDrafts[id] = nil
            openNotes.remove(id)
        }
    }

    public func toggleNote(_ id: UUID) {
        if openNotes.contains(id) { openNotes.remove(id) } else { openNotes.insert(id) }
    }

    public func draftBinding(for id: UUID) -> Binding<String> {
        Binding(get: { self.noteDrafts[id] ?? "" }, set: { self.noteDrafts[id] = $0 })
    }

    public func update(_ id: UUID, title: String, priority: Priority) {
        change("Saved", server: .update(id, title: title, priority: priority)) { try board.update(id, title: title, priority: priority, now: $0) }
    }

    public func snoozeUntilTomorrow(_ id: UUID) {
        let at = QuickCaptureParser.tomorrowMorning(now: Date(), timeZone: .current)
        change("See you tomorrow", server: .schedule(id, at: at)) { try board.scheduleNextAction(id, at: QuickCaptureParser.tomorrowMorning(now: $0, timeZone: .current), notify: true, now: $0) }
    }

    public func addSubtask(_ parentId: UUID, title: String) {
        change("Subtask added", server: .addSubtask(parent: parentId, title: title)) { _ = try board.addTask(NewTask(title, parentId: parentId), now: $0) }
    }

    public func addSteps(_ parentId: UUID, titles: [String], hoursBetween: Double) {
        let minutes = hoursBetween > 0 ? Int((hoursBetween * 60).rounded()) : nil
        change("Steps added", server: .addSteps(parent: parentId, titles: titles, stepDelayMinutes: minutes)) {
            _ = try board.addSteps(parentId: parentId, titles: titles, stepDelay: hoursBetween > 0 ? hoursBetween * 3600 : nil, now: $0)
        }
    }

    public func delete(_ id: UUID) { change("Deleted", server: .delete(id)) { try board.delete(id, now: $0) } }

    public func addGroup(_ name: String) {
        if waitWhileConnecting() { return }
        if server != nil {
            send(.addGroup(name: name), nil, onDone: { [weak self] data in
                if let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                   let text = object["id"] as? String, let id = UUID(uuidString: text) {
                    self?.selectedGroupId = id
                }
            })
            return
        }
        mutate(nil) { selectedGroupId = try board.addGroup(name: name, now: $0).id }
    }

    public func startFocus(_ id: UUID?) {
        let target = id ?? dashboard.focus?.item.id
        change(nil, server: .pomodoro(.start, itemId: target)) { try board.startFocus(target, now: $0) }
    }

    public func pausePomodoro() { change(nil, server: .pomodoro(.pause, itemId: nil)) { board.pauseFocus(now: $0) } }

    public func resumePomodoro() { change(nil, server: .pomodoro(.resume, itemId: nil)) { board.resumeFocus(now: $0) } }

    public func skipPomodoro() { change(nil, server: .pomodoro(.skip, itemId: nil)) { board.skipFocus(now: $0) } }

    public func resetPomodoro() { change(nil, server: .pomodoro(.reset, itemId: nil)) { _ in board.resetFocus() } }

    // MARK: Full-screen break

    /// Shows a full-screen break when a focus session ends (on by default; the user can turn it off).
    @Published public var fullScreenBreaks = UserDefaults.standard.object(forKey: "fullScreenBreaks") as? Bool ?? true {
        didSet { UserDefaults.standard.set(fullScreenBreaks, forKey: "fullScreenBreaks") }
    }

    /// The end of the break the user already hid or skipped (it stays hidden for that break).
    @Published private var dismissedBreak: Date?

    /// The break to show now, or nil.
    public var breakPrompt: BreakPrompt? {
        guard fullScreenBreaks, let due = Breaks.due(board.pomodoro, now: now), due.until != dismissedBreak else { return nil }
        return due
    }

    /// "I'm taking it": the screen goes away; the break timer keeps running.
    public func takeBreak() {
        dismissedBreak = Breaks.due(board.pomodoro, now: now)?.until ?? dismissedBreak
    }

    /// Back to work now: the break ends.
    public func skipBreak() {
        takeBreak()
        skipPomodoro()
    }

    public var pomodoroText: String {
        let seconds = Int(board.pomodoro.remaining(now).rounded(.up))
        return String(format: "%02d:%02d", seconds / 60, seconds % 60)
    }

    // MARK: Plumbing

    @discardableResult
    private func mutate(_ message: String?, _ change: (Date) throws -> Void) -> Bool {
        guard !isReadOnly else {
            status = "Your saved board couldn't be opened, so changes are disabled to protect it."
            return false
        }
        var ok = true
        do {
            try change(Date())
            status = message
            persist()
        } catch {
            ok = false
            status = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        }
        refresh()
        return ok
    }

    private func persist() {
        guard !isReadOnly else { return }
        do {
            try store?.save(board)
        } catch {
            status = "Could not save: \(error.localizedDescription)"
        }
        NotificationScheduler.sync(board)
    }

    private func tick() {
        let current = Date()
        if server != nil || isConnecting {
            // The server runs the focus timer and the reminders; here the clock moves and the board is read again
            // every few seconds (changes from the other computers, Obsidian, the browser).
            now = current
            if !isConnecting, changesInFlight == 0, readsInFlight == 0, current.timeIntervalSince(lastRefresh) >= 5 {
                lastRefresh = current
                Task { await reload() }
            }
            return
        }
        let events = board.tickPomodoro(now: current)
        let dueUnnotified = board.allItems.filter { !$0.isDone }.flatMap { item in
            item.reminders.filter { $0.isDue(current) && $0.notifiedAt == nil }.map { (item.id, $0.id) }
        }
        for (itemId, reminderId) in dueUnnotified { board.markNotified(itemId, reminderId: reminderId, now: current) }
        if !events.isEmpty || !dueUnnotified.isEmpty { persist() }
        // Refresh the lists every 30 s (waiting items wake up); the timer text updates every second.
        if !events.isEmpty || !dueUnnotified.isEmpty || current.timeIntervalSince(lastRefresh) >= 30 {
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
        let center = UNUserNotificationCenter.current()
        center.delegate = ForegroundPresenter.shared
        center.requestAuthorization(options: [.alert, .sound, .badge]) { _, _ in }
        #endif
    }

    static func sync(_ board: TaskBoard) {
        #if canImport(UserNotifications)
        // Only inside the app: the notification center crashes in a bare process (like the test runner, which still
        // has a bundle id).
        guard Bundle.main.bundleIdentifier != nil, Bundle.main.bundleURL.pathExtension == "app" else { return }
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

#if canImport(UserNotifications)
/// Shows reminder banners even while the app is in the foreground (iOS/macOS hide them by default).
final class ForegroundPresenter: NSObject, UNUserNotificationCenterDelegate {
    static let shared = ForegroundPresenter()

    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .list, .sound])
    }
}
#endif

extension WorkItem {
    var root: WorkItem { ancestors.last ?? self }
}
#endif
