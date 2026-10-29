#if canImport(SwiftUI)
import Foundation
import SwiftUI
import TodoTrackerKit
#if canImport(UserNotifications)
import UserNotifications
#endif
#if os(iOS)
import UIKit
#endif

/// View model for the iOS and macOS apps. On its own it owns a board on this device: persists after every change,
/// ticks the focus timer, and keeps local notifications in sync with pending reminders. On a Mac it uses Todo Tracker's
/// server instead (`use(server:)`): the tasks folder, synced with the other computers; changes go to the server and the
/// board is read back from it.
@MainActor
public final class BoardModel: ObservableObject {
    @Published public private(set) var dashboard: Dashboard
    /// What got done today (newest first): something to look back on.
    @Published public private(set) var doneToday: [WorkItem] = []
    @Published public private(set) var now = Date() {
        // Every tick and every refresh: the break this app saw is tracked for "Break's over".
        didSet { trackBreak() }
    }
    @Published public var selectedGroupId: UUID?
    @Published public var quickText = ""
    @Published public var status: String?
    /// Note drafts and open note boxes live here (not in row views) so they survive a card moving between lists.
    @Published public var noteDrafts: [UUID: String] = [:]
    @Published public var openNotes: Set<UUID> = []
    /// Rows whose notes are shown ("2 notes ▾"): kept here so they stay open when a card moves to another list.
    @Published public var shownNotes: Set<UUID> = []
    /// The search box: the lists show only matching tasks (TaskSearch, the same syntax as the other apps).
    @Published public var searchText = "" {
        didSet { if searchText != oldValue { refresh() } }
    }
    /// Every task the search finds, done and archived ones included (the lists show only open or done-today ones), in
    /// tree order; empty when not searching.
    @Published public private(set) var searchResults: [WorkItem] = []
    /// Finished tasks put away (found with is:archived).
    @Published public private(set) var archivedCount = 0
    /// What's typed in a task's details and not saved yet, by task: kept here so closing the sheet (or a break covering
    /// it) never loses it. Each remembers the value it started from: only a field changed from that is saved.
    @Published public private(set) var taskDrafts: [UUID: [DraftField: FieldDraft]] = [:]
    /// Why a typed repeat rule isn't understood, by task.
    @Published public private(set) var repeatProblems: [UUID: String] = [:]
    /// A task a deep link asked to open (todotracker://task/<id>): the dashboard shows it, then clears this.
    @Published public var openTaskId: UUID?
    /// True when the saved board could not be read; nothing is written so the user's data is never overwritten.
    @Published public private(set) var isReadOnly = false

    /// The server in use (nil: the board lives on this device only).
    @Published public private(set) var serverURL: URL?
    /// Opening the tasks folder through the server (Mac): changes wait.
    @Published public private(set) var isConnecting = false

    public private(set) var board: TaskBoard
    private let store: DeviceBoardStore?
    /// Why the tasks kept on this device couldn't be opened (nil: they could). Nothing clears it: connecting to a
    /// computer and back never turns changes on over tasks that weren't opened.
    public let storeFailure: String?
    /// Shown read-only while the tasks kept on this device can't be opened.
    private let fallback: TaskBoard?
    /// Syncing on its own with the other devices (iPhone and iPad; nil on the Mac, whose server syncs).
    public let sync: SyncModel?
    private var timer: Timer?
    private var lastRefresh = Date.distantPast
    private var server: ServerClient?
    /// Bumped by every change and every read: a read only lands if nothing happened since it started (so an older
    /// board never replaces a newer one).
    private var generation = 0
    private var changesInFlight = 0
    private var readsInFlight = 0
    private var readFailed = false
    private var unansweredReads = 0

    /// Called when the server stops answering (the Mac app then starts it again).
    public var onServerLost: (() -> Void)?
    private var etag: String?
    private var themeSettled = false

    /// Where the phone stands with the computer it's connected to (nil: it uses the tasks kept on it).
    @Published public private(set) var remote: RemoteState?
    /// The computer's name, while connected to it (or just disconnected by it, to say so).
    @Published public private(set) var remoteComputer: String?
    /// When the board shown was last read from the computer.
    @Published public private(set) var remoteLastRead: Date?
    /// Trading a pairing code for a token right now.
    @Published public private(set) var isPairing = false
    /// Why the last try to connect didn't work (shown on the Connect screen).
    @Published public var pairingError: String?
    /// Connecting to a computer (iPhone and iPad; nil on the Mac, which uses its own server).
    public let phone: PhoneSetup?
    private var paired: PairedComputer?
    private var findingComputer = false
    private var lastFind = Date.distantPast

    /// - Parameters:
    ///   - openError: the tasks kept on this device couldn't be opened (said, and changes stay off whatever happens).
    ///   - fallback: what to show meanwhile (the tasks from before, read-only).
    ///   - notice: something to say at launch (tasks from before that couldn't come in yet).
    public init(store: DeviceBoardStore?, phone: PhoneSetup? = nil, sync: SyncModel? = nil, openError: String? = nil, fallback: TaskBoard? = nil, notice: String? = nil) {
        self.store = store
        self.phone = phone
        self.sync = sync
        self.fallback = fallback
        var loadError: String?
        let loaded: TaskBoard
        do {
            loaded = try store?.load() ?? fallback ?? TaskBoard()
        } catch {
            loaded = TaskBoard()
            loadError = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        }
        board = loaded
        dashboard = Agenda.build(loaded, now: Date())
        storeFailure = openError ?? loadError.map { "Couldn't open your saved board (\($0)). Changes are disabled so nothing gets overwritten." }
        if let storeFailure {
            isReadOnly = true
            status = storeFailure
        } else if let notice {
            status = notice
        }
        refresh()
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.tick() }
        }
        if let sync {
            // Connected to a computer, the phone uses its tasks: nothing syncs on its own meanwhile.
            sync.isPaused = { [weak self] in self?.isRemote ?? true }
            sync.onSynced = { [weak self] in self?.reloadFromStore() }
        }
    }

    public static func live() -> BoardModel {
        #if os(iOS)
        let phone = PhoneSetup.live()
        let settings = SyncSettingsStore(secrets: KeychainSecretStore())
        var store: VaultBoardStore?
        var sync: SyncModel?
        var openError: String?
        var fallback: TaskBoard?
        var notice: String?
        if let base = try? FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
            .appendingPathComponent("TodoTracker", isDirectory: true) {
            let syncFolder = base.appendingPathComponent("sync", isDirectory: true)
            // Joining a sync for the first time brings a leftover board.json in after what came (never before).
            let joining = settings.load().map { !PhoneSyncer.hasJoined(directory: syncFolder, settings: $0) } ?? false
            let opened = VaultBoardStore.openOnDevice(base: base, importLeftover: !joining)
            if let vault = opened.store {
                store = vault
                notice = opened.message
                let syncer = PhoneSyncer(store: vault.vault, directory: syncFolder, settings: settings,
                                         deviceName: phone.pairings.phoneName ?? phone.phoneName, legacyBoard: base.appendingPathComponent("board.json"))
                sync = SyncModel(syncer: syncer, settings: settings)
            } else {
                openError = opened.message
                fallback = opened.fallback
            }
        } else {
            openError = "Couldn't open your tasks: this device's app storage can't be reached. Restart the app to try again."
        }
        let model = BoardModel(store: store, phone: phone, sync: sync, openError: openError, fallback: fallback, notice: notice)
        model.connectToPairedComputer()
        // At launch: a sync now, then every minute while the app is open.
        if !model.isRemote { sync?.start() }
        return model
        #else
        return BoardModel(store: try? BoardFileStore(url: BoardFileStore.defaultURL()))
        #endif
    }

    public static func requestNotificationPermission() { NotificationScheduler.requestAuthorization() }

    // MARK: Server (Mac)

    public var usesServer: Bool { serverURL != nil || isRemote }

    /// Using a computer's tasks from this phone (connected now or not).
    public var isRemote: Bool { remote != nil && remote != .revoked }

    /// The full window in the browser: on the Mac, from its own server (not from a phone).
    public var canOpenInBrowser: Bool { serverURL != nil && !isRemote }


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
        unansweredReads = 0
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
        if server != nil {
            // Leaving the server (it's gone for good): back to this Mac's board, nothing sent anywhere.
            server = nil
            serverURL = nil
            etag = nil
            board = (try? store?.load()) ?? TaskBoard()
            refresh()
        }
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
                if isRemote { saveRemoteCopy(fresh) }
            }
            if isRemote {
                remoteLastRead = Date()
                phone?.pairings.lastRead = remoteLastRead
            }
            if let theme { adopt(theme: theme, from: server) }
            if readFailed {
                readFailed = false
                status = nil
            }
            unansweredReads = 0
            refresh()
        } catch {
            guard mine == generation else { return }
            if isRemote {
                remoteReadFailed(error)
                return
            }
            readFailed = true
            status = Self.describe(error)
            if let failure = error as? ServerError, case .unreachable(_) = failure {
                unansweredReads += 1
                // Not answering for a while (about 15 s): it's gone; whoever started it starts it again.
                if unansweredReads == 3 { onServerLost?() }
            }
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
        if isRemote, remote?.canChange != true {
            // Nothing is changed on the copy shown: it goes to the computer once it answers again.
            status = remoteBanner ?? "One moment: connecting to \(remoteComputer ?? "your computer")…"
            return true
        }
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
        dashboard = Agenda.build(board, now: now, recentNoteCount: 6, groupId: selectedGroupId, query: searchText)
        doneToday = Agenda.doneToday(board, now: now, groupId: selectedGroupId, query: searchText)
        let search = TaskSearch(searchText)
        searchResults = search.isEmpty ? [] : search.apply(board).filter { selectedGroupId == nil || $0.groupId == selectedGroupId }
        archivedCount = board.items.filter { $0.archivedAt != nil }.count
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

    /// A clicked todotracker:// link: the task it names opens (anything else is ignored).
    public func open(link: URL) {
        if PairingLink.isPairing(link.absoluteString) {
            pair(link: link.absoluteString)
            return
        }
        if case .task(let id) = TaskLinks.target(link.absoluteString) { openTaskId = id }
    }

    public func complete(_ id: UUID) { change("Done 🎉", server: .complete(id)) { try board.complete(id, now: $0) } }

    /// The quick choices right now (the same in every app).
    public var snoozeChoices: [SnoozeChoice] { Snooze.choices(now: Date(), timeZone: .current) }

    /// Snoozes until a time: a quick choice or one picked on the calendar.
    public func snooze(_ id: UUID, until at: Date) {
        let when = Snooze.describe(at, now: Date(), timeZone: .current)
        change("Snoozed until \(when)", server: .schedule(id, at: at)) { try board.scheduleNextAction(id, at: at, notify: true, now: $0) }
    }

    /// Snoozes until another task is done; it comes back with a reminder then.
    public func waitFor(_ id: UUID, after other: WorkItem) {
        change("Waiting for “\(other.title)”", server: .waitFor(id, after: other.id)) { try board.waitFor(id, after: other.id, now: $0) }
    }

    /// Tasks it can wait for: the open ones shown (to do now, then waiting), not itself, its own subtasks or parents, or
    /// what already waits for it.
    public func waitCandidates(for id: UUID) -> [WorkItem] {
        var seen = Set<UUID>()
        return (dashboard.now + dashboard.waiting).map(\.item).filter { seen.insert($0.id).inserted && board.canWaitFor(id, after: $0.id) }
    }

    public func reopen(_ id: UUID) { change("Reopened", server: .reopen(id)) { try board.reopen(id, now: $0) } }

    public func bringBack(_ id: UUID) { change(nil, server: .bringBack(id)) { try board.clearNextAction(id, now: $0) } }

    /// How much energy a task takes per hour (1 to 3; nil: not said).
    public func setEnergy(_ id: UUID, level: Int?) {
        change(level.map { "Energy: \(String(repeating: "⚡", count: $0)) an hour" } ?? "Energy: not said", server: .setEnergy(id, level: level)) {
            try board.setEnergy(id, level: level, now: $0)
        }
    }

    /// How much energy there is in a day (every app uses it).
    public func setDailyEnergy(_ perDay: Int?) {
        change("Energy in a day: \(perDay ?? Energy.defaultPerDay)", server: .setDailyEnergy(perDay)) { _ in try board.setEnergyPerDay(perDay) }
    }

    /// Do something else first: the task moves one down Do now (the next one becomes the focus).
    public func skip(_ id: UUID) {
        var ids = dashboard.now.map(\.item.id)
        guard let i = ids.firstIndex(of: id), i + 1 < ids.count else { return }
        ids.swapAt(i, i + 1)
        change(nil, server: .arrangeNow(ids)) { try board.arrangeNow(ids, now: $0) }
    }

    public func dismissReminder(_ id: UUID, reminderId: UUID) {
        change(nil, server: .dismissReminder(id, reminderId: reminderId)) { try board.dismissReminder(id, reminderId: reminderId, now: $0) }
    }

    public func addNote(_ id: UUID, text: String) {
        // Nothing typed: nothing to save (the box stays as it is).
        guard !Self.isBlank(text) else { return }
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

    /// Shows or hides a row's notes ("2 notes ▾").
    public func toggleShownNotes(_ id: UUID) {
        if shownNotes.contains(id) { shownNotes.remove(id) } else { shownNotes.insert(id) }
    }

    public func draftBinding(for id: UUID) -> Binding<String> {
        Binding(get: { self.noteDrafts[id] ?? "" }, set: { self.noteDrafts[id] = $0 })
    }

    /// Saves fields changed in a task's details (only those: edits made elsewhere meanwhile stay).
    /// `onFail`: the change didn't happen (the server said no or didn't answer, or it isn't valid here).
    @discardableResult
    public func edit(_ id: UUID, _ changes: TaskChanges, message: String? = "Saved", onFail: (() -> Void)? = nil) -> Bool {
        guard !changes.isEmpty else { return true }
        if waitWhileConnecting() { return false }
        if server != nil {
            send(.edit(id, changes), message, onFail: onFail)
            return true
        }
        if mutate(message, { try board.update(id, changes, now: $0) }) { return true }
        onFail?()
        return false
    }

    // MARK: Drafts (what's typed in a task's details)

    /// What's typed in a field and not saved yet (nil: nothing).
    public func draft(_ id: UUID, _ field: DraftField) -> String? { taskDrafts[id]?[field]?.text }

    /// Typing in a field: the first keystroke remembers the value it started from (`current`). Back to that value,
    /// there's nothing to save.
    public func setDraft(_ id: UUID, _ field: DraftField, _ text: String, current: String) {
        let base = taskDrafts[id]?[field]?.base ?? current
        var drafts = taskDrafts[id] ?? [:]
        drafts[field] = text == base ? nil : FieldDraft(text: text, base: base)
        taskDrafts[id] = drafts.isEmpty ? nil : drafts
        if field == .repeatRule { repeatProblems[id] = nil }
    }

    public func hasDrafts(_ id: UUID) -> Bool { taskDrafts[id] != nil }

    /// Saves what's typed for a task: only fields changed from where they started, so a newer change from elsewhere
    /// is never put back. A blank title or a repeat rule that isn't understood stays typed, with the reason; if the
    /// server says no, what was typed comes back.
    public func saveDrafts(_ id: UUID) {
        guard let drafts = taskDrafts[id], board.find(id) != nil else {
            taskDrafts[id] = nil
            return
        }
        var keep: [DraftField: FieldDraft] = [:]
        var changes = TaskChanges()
        if let title = drafts[.title] {
            if Self.isBlank(title.text) {
                keep[.title] = title
                status = "A task title is required."
            } else {
                changes.title = title.text
            }
        }
        if let details = drafts[.details] { changes.details = details.text }
        if let rule = drafts[.repeatRule] {
            let text = rule.text.trimmingCharacters(in: .whitespacesAndNewlines)
            do {
                if !text.isEmpty { _ = try Recurrence.parse(text) }
                changes.repeatRule = text
            } catch {
                keep[.repeatRule] = rule
                repeatProblems[id] = Self.describe(error)
                status = Self.describe(error)
            }
        }
        let sent = drafts.filter { keep[$0.key] == nil }
        taskDrafts[id] = keep.isEmpty ? nil : keep
        guard !changes.isEmpty else { return }
        edit(id, changes, onFail: { [weak self] in
            // Nothing typed is lost: back in the boxes, unless something new was typed meanwhile.
            guard let self else { return }
            var now = self.taskDrafts[id] ?? [:]
            for (field, draft) in sent where now[field] == nil { now[field] = draft }
            self.taskDrafts[id] = now.isEmpty ? nil : now
        })
    }

    /// Saves everything typed in any task's details (the details sheet went away).
    public func saveAllDrafts() {
        for id in Array(taskDrafts.keys) { saveDrafts(id) }
    }

    /// Sets the deadline (nil: none).
    public func setDeadline(_ id: UUID, _ at: Date?) {
        let message = at.map { "Due \(Snooze.describe($0, now: Date(), timeZone: .current))" } ?? "No deadline"
        edit(id, TaskChanges(deadline: .some(at)), message: message)
    }

    /// Sets how it repeats, in words ("every weekday"; blank: it doesn't repeat any more). Returns why a rule isn't
    /// understood (then nothing is sent), or nil.
    @discardableResult
    public func setRepeat(_ id: UUID, rule: String) -> String? {
        let text = rule.trimmingCharacters(in: .whitespacesAndNewlines)
        if !text.isEmpty {
            do {
                _ = try Recurrence.parse(text)
            } catch {
                return Self.describe(error)
            }
        }
        edit(id, TaskChanges(repeatRule: text), message: text.isEmpty ? "Doesn’t repeat any more" : "Repeats \(text)")
        return nil
    }

    /// Steps in order: each subtask waits for the one before it.
    public func setSequential(_ id: UUID, _ on: Bool) {
        edit(id, TaskChanges(sequential: on), message: on ? "Steps in order" : "Steps in any order")
    }

    /// Hours between steps (nil or 0: none): the next step comes back that long after the one before is done.
    public func setStepDelay(_ id: UUID, hours: Double?) {
        let delay = hours.flatMap { $0 > 0 ? $0 * 3600 : nil }
        edit(id, TaskChanges(stepDelay: .some(delay)), message: delay == nil ? "No wait between steps" : "\(Presentation.hoursLabel(hours ?? 0)) between steps")
    }

    /// "Remind me" at a time, with an optional message.
    public func addReminder(_ id: UUID, at: Date, message: String?) {
        let text = message.flatMap { Self.isBlank($0) ? nil : $0.trimmingCharacters(in: .whitespacesAndNewlines) }
        let when = Snooze.describe(at, now: Date(), timeZone: .current)
        change("Reminder set for \(when)", server: .addReminder(id, at: at, message: text)) {
            _ = try board.addReminder(id, dueAt: at, message: text, now: $0)
        }
    }

    /// A top-level task to another group (its subtasks go with it).
    public func moveToGroup(_ id: UUID, groupId: UUID) {
        let name = board.groups.first { $0.id == groupId }?.name ?? "the group"
        change("Moved to \(name)", server: .moveToGroup(id, groupId: groupId)) { try board.moveToGroup(id, groupId: groupId, now: $0) }
    }

    /// Puts a finished task away (out of the lists, kept), or brings it back.
    public func archive(_ id: UUID) { change("Archived", server: .archive(id)) { try board.archive(id, now: $0) } }

    public func unarchive(_ id: UUID) { change("Back in Done", server: .unarchive(id)) { try board.unarchive(id, now: $0) } }

    public func addSubtask(_ parentId: UUID, title: String) {
        guard !Self.isBlank(title) else { return }
        change("Subtask added", server: .addSubtask(parent: parentId, title: title)) { _ = try board.addTask(NewTask(title, parentId: parentId), now: $0) }
    }

    public func addSteps(_ parentId: UUID, titles: [String], hoursBetween: Double) {
        // Lines with only spaces aren't steps.
        let titles = titles.map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }.filter { !$0.isEmpty }
        guard !titles.isEmpty else { return }
        let minutes = hoursBetween > 0 ? Int((hoursBetween * 60).rounded()) : nil
        change("Steps added", server: .addSteps(parent: parentId, titles: titles, stepDelayMinutes: minutes)) {
            _ = try board.addSteps(parentId: parentId, titles: titles, stepDelay: hoursBetween > 0 ? hoursBetween * 3600 : nil, now: $0)
        }
    }

    public func delete(_ id: UUID) { change("Deleted", server: .delete(id)) { try board.delete(id, now: $0) } }

    public func addGroup(_ name: String) {
        guard !Self.isBlank(name) else {
            status = "Type a name for the group."
            return
        }
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

    /// Renames a group (its color stays).
    public func renameGroup(_ id: UUID, to name: String) {
        guard let group = board.groups.first(where: { $0.id == id }) else { return }
        guard !Self.isBlank(name) else {
            status = "Type a name for the group."
            return
        }
        let clean = name.trimmingCharacters(in: .whitespacesAndNewlines)
        change("Renamed to \(clean)", server: .updateGroup(id, name: clean, color: group.color)) {
            try board.updateGroup(id, name: clean, color: group.color, now: $0)
        }
    }

    /// A group's color (#rrggbb; nil: none).
    public func recolorGroup(_ id: UUID, to color: String?) {
        guard let group = board.groups.first(where: { $0.id == id }) else { return }
        change("Color changed", server: .updateGroup(id, name: group.name, color: color)) {
            try board.updateGroup(id, name: group.name, color: color, now: $0)
        }
    }

    /// Deletes a group; its tasks move to `moveTo` (nothing is lost).
    public func deleteGroup(_ id: UUID, moveTo: UUID) {
        guard let group = board.groups.first(where: { $0.id == id }), let target = board.groups.first(where: { $0.id == moveTo }) else { return }
        let wasSelected = selectedGroupId == id
        if change("Deleted \(group.name): its tasks are in \(target.name)", server: .deleteGroup(id, moveTo: moveTo), local: { try board.deleteGroup(id, moveTasksTo: moveTo, now: $0) }),
           wasSelected {
            selectedGroupId = moveTo
            refresh()
        }
    }

    /// The focus timer's lengths. Only for a board on this device: the server's are kept with the tasks folder.
    public func setPomodoroSettings(_ settings: PomodoroSettings) {
        guard !usesServer else {
            status = "The focus timer’s lengths come from your tasks folder."
            return
        }
        mutate("Focus timer saved") { _ in try board.setPomodoroSettings(settings) }
    }

    private static func isBlank(_ text: String) -> Bool { text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }

    public func startFocus(_ id: UUID?) {
        let target = id ?? dashboard.focus?.item.id
        change(nil, server: .pomodoro(.start, itemId: target)) { try board.startFocus(target, now: $0) }
    }

    public func pausePomodoro() { change(nil, server: .pomodoro(.pause, itemId: nil)) { board.pauseFocus(now: $0) } }

    public func resumePomodoro() { change(nil, server: .pomodoro(.resume, itemId: nil)) { board.resumeFocus(now: $0) } }

    public func skipPomodoro() { change(nil, server: .pomodoro(.skip, itemId: nil)) { board.skipFocus(now: $0) } }

    public func resetPomodoro() { change(nil, server: .pomodoro(.reset, itemId: nil)) { board.resetFocus(now: $0) } }

    // MARK: Task timer

    /// The timer that runs now (on any task), or nil.
    public var runningTimer: (item: WorkItem, entry: TimeEntry)? { board.runningTimer(now) }

    /// Times the task (any other timer stops), or stops it when it's the one timing.
    public func toggleTimer(_ id: UUID) {
        if runningTimer?.item.id == id {
            stopTimer()
        } else {
            let title = board.find(id)?.title ?? "the task"
            change("Timing “\(title)”", server: .startTimer(id)) { _ = try board.startTimer(id, now: $0) }
        }
    }

    public func stopTimer() { change("Timer stopped", server: .stopTimer) { _ = board.stopTimer(now: $0) } }

    // MARK: Full-screen break

    /// Shows a full-screen break when a focus session ends (on by default; the user can turn it off).
    @Published public var fullScreenBreaks = UserDefaults.standard.object(forKey: "fullScreenBreaks") as? Bool ?? true {
        didSet { UserDefaults.standard.set(fullScreenBreaks, forKey: "fullScreenBreaks") }
    }

    /// The end of the break the user already hid or skipped (it stays hidden for that break).
    @Published private var dismissedBreak: Date?

    /// The break this app saw (shown, or hidden with "I'm taking it"): when it runs out, it asks about the next focus.
    @Published private var seenBreak: Date?
    @Published private var dismissedOver: Date?

    /// The break to show now (or "Break's over"), or nil.
    public var breakPrompt: BreakPrompt? {
        guard fullScreenBreaks else { return nil }
        let due = Breaks.due(board.pomodoro, now: now)
        if let due, due.until != dismissedBreak { return due }
        let seen = due?.until ?? seenBreak
        let p = board.pomodoro
        guard due == nil, let seen, Breaks.over(phase: p.phase, running: p.isRunning, endsAt: p.endsAt, breakEndedAt: p.breakEndedAt, now: now, seen: seen, dismissedOver: dismissedOver) else { return nil }
        return BreakPrompt(until: seen, isLong: false, isOver: true)
    }

    /// What "Start next focus" picks up (nil: no task).
    public var nextFocusTitle: String? { (board.nextFocusItem() ?? dashboard.focus?.item)?.title }

    /// Remembers the break this app saw (whether it ran out by itself, the timer says).
    private func trackBreak() {
        if let due = Breaks.due(board.pomodoro, now: now) {
            if seenBreak != due.until { seenBreak = due.until }
            return
        }
        #if os(iOS)
        // On the phone, a break that ran out while the app was asleep (a notification said so): back in the app, it asks
        // about the next one. (The Mac only asks about a break it showed.)
        if seenBreak == nil, board.pomodoro.phase == .idle, let ended = board.pomodoro.breakEndedAt { seenBreak = ended }
        #endif
    }

    /// "I'm taking it" (the break timer keeps running), or "Not now" when the break is over.
    public func takeBreak() {
        if breakPrompt?.isOver == true {
            dismissedOver = seenBreak
        } else {
            dismissedBreak = Breaks.due(board.pomodoro, now: now)?.until ?? dismissedBreak
        }
    }

    /// Back to work now: the break ends.
    public func skipBreak() {
        takeBreak()
        seenBreak = nil
        skipPomodoro()
    }

    /// The next focus session now (during the break, or when it's over): on the last task, else today's top one.
    public func startNextFocus() {
        dismissedBreak = Breaks.due(board.pomodoro, now: now)?.until ?? dismissedBreak
        dismissedOver = seenBreak
        seenBreak = nil
        let fallback = dashboard.focus?.item.id
        change("Focus started", server: .pomodoro(.next, itemId: fallback)) { try board.startNextFocus(fallback: fallback, now: $0) }
    }

    public var pomodoroText: String {
        let seconds = Int(board.pomodoro.remaining(now).rounded(.up))
        return String(format: "%02d:%02d", seconds / 60, seconds % 60)
    }

    // MARK: A computer's tasks (iPhone and iPad)

    /// How often a computer that doesn't answer is tried again (and whenever the app comes back to the front).
    static let retryEvery: TimeInterval = 30

    /// The calm line above the tasks while the computer isn't answering, or after it disconnected this phone.
    public var remoteBanner: String? {
        guard let remote, let remoteComputer else { return nil }
        return remote.banner(computer: remoteComputer, lastRead: remoteLastRead, now: now)
    }

    /// At launch: when this phone is connected to a computer, its tasks as last read are shown at once (without
    /// changes), and the computer is looked for on each of its addresses.
    public func connectToPairedComputer() {
        guard let phone, let saved = phone.pairings.load() else { return }
        paired = saved
        remoteComputer = saved.computer
        remote = .connecting
        remoteLastRead = phone.pairings.lastRead
        if let cached = phone.cacheURL.flatMap({ try? Data(contentsOf: $0) }), let copy = try? BoardCodec.decode(cached) {
            board = copy
        } else {
            board = TaskBoard()
        }
        status = nil
        refresh()
        findComputer()
    }

    /// Pulled down to refresh: the board is read again from the server (a computer out of reach is looked for).
    public func pullToRefresh() async {
        if isRemote, remote?.canChange != true {
            findComputer()
        } else if server != nil, !isConnecting {
            await reload()
        }
    }

    /// The app is in front again: a computer that wasn't answering is tried right away.
    public func becameActive() {
        if !isRemote { sync?.start() }
        guard isRemote, remote?.canChange != true, !findingComputer else { return }
        findComputer()
    }

    /// The app went to the background: syncing waits for iOS to wake the app.
    public func wentToBackground() { sync?.stop() }

    /// The tasks folder changed (a sync brought something): the board is read again, reminders set up again.
    public func reloadFromStore() {
        guard !isRemote, let store, !isReadOnly, storeFailure == nil else { return }
        do {
            board = try store.load()
        } catch {
            status = Self.describe(error)
            return
        }
        NotificationScheduler.sync(board)
        refresh()
    }

    /// Where this phone's tasks are, in words, for Settings: connected to a computer, syncing on its own, or only here.
    public var modeDescription: String {
        let device = phone?.deviceKind ?? "device"
        if isRemote { return "Connected to \(remoteComputer ?? "your computer"): you’re using its tasks. The tasks kept on this \(device) wait, untouched." }
        if sync?.isOn == true { return "Your tasks are on this \(device) and sync on their own with your other devices." }
        return "Your tasks are only on this \(device)."
    }

    /// The name the computer shows for this phone ("Shahab's iPad"): the one typed last time, else the device's own
    /// (on a real device that's just "iPhone" or "iPad").
    public var phoneName: String { phone?.pairings.phoneName ?? phone?.phoneName ?? "" }

    /// Connects this phone with the link from Settings › Phones on a computer (scanned, pasted or opened), under the
    /// name given (nil or blank: the one it has).
    public func pair(link text: String, phoneName typed: String? = nil) {
        guard let phone else { return }
        if let typed, !Self.isBlank(typed) { phone.pairings.phoneName = typed }
        let name = phoneName
        let link: PairingLink
        do {
            link = try PairingLink.parse(text)
        } catch {
            pairingError = Self.describe(error)
            status = pairingError
            return
        }
        guard !isPairing else { return }
        // Connecting: a sync running on its own stops now (it's turned off once connected).
        sync?.cancel()
        isPairing = true
        pairingError = nil
        status = "Connecting to \(link.computer)…"
        let session = phone.makeSession(link.fingerprint)
        Task {
            defer { isPairing = false }
            do {
                let made = try await PhoneConnect.claim(link, phoneName: name, fetch: PhoneConnect.fetch(with: session))
                let saved = PairedComputer(hosts: link.hosts, fingerprint: link.fingerprint, computer: made.computer, lastHost: made.host, token: made.token)
                guard phone.pairings.save(saved) else {
                    pairingError = "Couldn’t keep the connection on this phone (its Keychain said no). Try again."
                    status = pairingError
                    return
                }
                phone.pairings.lastRead = nil
                if let cache = phone.cacheURL { try? FileManager.default.removeItem(at: cache) }
                // One or the other: using a computer's tasks turns syncing on its own off (the tasks here stay).
                sync?.turnOff()
                paired = saved
                remoteComputer = made.computer
                remoteLastRead = nil
                remote = .connecting
                connect(to: made.host)
                status = "Connected to \(made.computer)"
            } catch {
                pairingError = Self.describe(error)
                status = pairingError
            }
        }
    }

    /// Back to the tasks on this phone: the connection is forgotten here (the board on this phone was never touched).
    public func disconnectComputer() {
        let name = remoteComputer ?? "your computer"
        let wasRevoked = remote == .revoked
        phone?.pairings.forget()
        if let cache = phone?.cacheURL { try? FileManager.default.removeItem(at: cache) }
        leaveComputer()
        remote = nil
        remoteComputer = nil
        status = wasRevoked ? nil : "Disconnected from \(name). These are the tasks on this phone, as you left them."
    }

    /// Looks for the computer on each of its addresses (the last good one first).
    private func findComputer() {
        guard let phone, let saved = paired, !findingComputer else { return }
        findingComputer = true
        lastFind = Date()
        remote = remote?.after(.retrying)
        let session = phone.makeSession(saved.fingerprint)
        Task {
            defer {
                findingComputer = false
                lastFind = Date()
            }
            do {
                let host = try await PhoneConnect.findHost(saved.hosts, preferring: saved.lastHost, token: saved.token, computer: saved.computer, fetch: PhoneConnect.fetch(with: session))
                guard paired == saved else { return }
                if host != saved.lastHost {
                    var moved = saved
                    moved.lastHost = host
                    paired = moved
                    phone.pairings.save(moved)
                }
                connect(to: host)
            } catch PhoneConnect.Failure.disconnected {
                guard paired == saved else { return }
                computerDisconnectedThisPhone()
            } catch {
                guard paired == saved else { return }
                computerUnreachable()
            }
        }
    }

    /// Uses the computer at that address from now on (its tasks, read every few seconds; changes go there).
    private func connect(to host: String) {
        guard let phone, let saved = paired, let base = PairingLink.baseURL(host) else { return }
        server = ServerClient(baseURL: base, token: saved.token, session: phone.makeSession(saved.fingerprint))
        serverURL = base
        isConnecting = false
        isReadOnly = false
        etag = nil
        themeSettled = false
        remote = remote?.after(.answered(host: host))
        if status == remoteBanner { status = nil }
        Task { await reload() }
    }

    private func remoteReadFailed(_ error: Error) {
        if case let .refused(status, _)? = error as? ServerError, status == 401 {
            computerDisconnectedThisPhone()
        } else if case .unreachable? = error as? ServerError {
            // That address stopped answering: every address is tried (another network may still reach it).
            findComputer()
        } else {
            status = Self.describe(error)
        }
    }

    private func computerUnreachable() {
        server = nil
        serverURL = nil
        etag = nil
        remote = remote?.after(.noAnswer)
        refresh()
    }

    /// The computer no longer knows this phone: say so, and go back to the tasks on this phone.
    private func computerDisconnectedThisPhone() {
        phone?.pairings.forget()
        if let cache = phone?.cacheURL { try? FileManager.default.removeItem(at: cache) }
        leaveComputer()
        remote = .revoked
        status = nil
    }

    /// The computer's tasks leave; the board on this phone comes back.
    private func leaveComputer() {
        if let storeFailure {
            isReadOnly = true
            status = storeFailure
        }
        paired = nil
        server = nil
        serverURL = nil
        etag = nil
        remoteLastRead = nil
        board = (try? store?.load()) ?? fallback ?? TaskBoard()
        NotificationScheduler.sync(board)
        refresh()
    }

    /// The board as last read, kept so the phone shows it when the computer can't be reached.
    private func saveRemoteCopy(_ board: TaskBoard) {
        guard let url = phone?.cacheURL, let data = try? BoardCodec.encode(board) else { return }
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: url, options: [.atomic, .completeFileProtectionUntilFirstUserAuthentication])
    }

    // MARK: Plumbing

    @discardableResult
    private func mutate(_ message: String?, _ change: (Date) throws -> Void) -> Bool {
        guard !isRemote else { return false }
        guard !isReadOnly, storeFailure == nil else {
            status = storeFailure ?? (store?.wasMigrated == true
                ? "Your tasks are in your tasks folder, and Todo Tracker's server isn't running: changes are off until it is."
                : "Your saved board couldn't be opened, so changes are disabled to protect it.")
            return false
        }
        var ok = true
        let at = Date()
        do {
            if let store {
                // The change is made to the store's board (`board` points at it meanwhile), then the board shown is
                // what was saved.
                board = try store.apply(board) { target in
                    board = target
                    try change(at)
                }
            } else {
                try change(at)
            }
            status = message
            NotificationScheduler.sync(board)
            sync?.changed()
        } catch {
            ok = false
            status = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
            // Nothing was saved: the board shown is the one kept.
            if let store, store.keepsOwnBoard, let kept = try? store.load() { board = kept }
        }
        refresh()
        return ok
    }

    /// Saves a change the clock made (the focus timer, reminders that went off, a forgotten timer). With a store that
    /// keeps its own board, `redo` makes it there too.
    private func persist(redo: (TaskBoard) -> Void) {
        // The computer's tasks are never written to the board on this phone.
        guard !isReadOnly, storeFailure == nil, !isRemote else { return }
        do {
            if let store {
                board = try store.apply(board) { target in if store.keepsOwnBoard { redo(target) } }
            }
            sync?.changed()
        } catch {
            status = "Could not save: \(error.localizedDescription)"
        }
        NotificationScheduler.sync(board)
    }

    private func tick() {
        let current = Date()
        if isRemote {
            now = current
            if remote?.canChange == true {
                if changesInFlight == 0, readsInFlight == 0, current.timeIntervalSince(lastRefresh) >= 5 {
                    lastRefresh = current
                    Task { await reload() }
                }
            } else if !findingComputer, current.timeIntervalSince(lastFind) >= Self.retryEvery {
                // Not answering: every 30 s, every address again.
                findComputer()
            }
            return
        }
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
        let readOnly = isReadOnly
        var events: [PomodoroEvent] = []
        var dueUnnotified: [(UUID, UUID)] = []
        var closed = 0
        let clockWork: (TaskBoard) -> Void = { board in
            events = board.tickPomodoro(now: current)
            dueUnnotified = board.allItems.filter { !$0.isDone }.flatMap { item in
                item.reminders.filter { $0.isDue(current) && $0.notifiedAt == nil }.map { (item.id, $0.id) }
            }
            for (itemId, reminderId) in dueUnnotified { board.markNotified(itemId, reminderId: reminderId, now: current) }
            // A timer left running for half a day was forgotten: it ends (at 12 hours), so the task can be timed again.
            closed = readOnly ? 0 : board.closeForgottenTimers(now: current)
        }
        clockWork(board)
        if !events.isEmpty || !dueUnnotified.isEmpty || closed > 0 { persist(redo: clockWork) }
        // Refresh the lists every 30 s (waiting items wake up); the timer text updates every second.
        if !events.isEmpty || !dueUnnotified.isEmpty || closed > 0 || current.timeIntervalSince(lastRefresh) >= 30 {
            refresh()
        } else {
            now = current
        }
    }
}

/// A field of a task's details that keeps what's typed until it's saved.
public enum DraftField: Hashable, Sendable { case title, details, repeatRule }

/// What's typed in a field, and the value it started from.
public struct FieldDraft: Equatable, Sendable {
    public let text: String
    public let base: String
}

/// What the phone needs to use a computer's tasks: where the pairing is kept, where the last board read is kept, the
/// network (trusting only the paired certificate) and the phone's name as the computer shows it.
public struct PhoneSetup {
    public let pairings: PairingStore
    public let cacheURL: URL?
    public let makeSession: (_ fingerprint: String) -> URLSession
    public let phoneName: String
    /// "iPhone" or "iPad", in sentences ("Your tasks are only on this iPad").
    public let deviceKind: String

    public init(pairings: PairingStore, cacheURL: URL?, phoneName: String, deviceKind: String = "iPhone", makeSession: @escaping (_ fingerprint: String) -> URLSession = { PinnedTrust.session(fingerprint: $0) }) {
        self.deviceKind = deviceKind
        self.pairings = pairings
        self.cacheURL = cacheURL
        self.makeSession = makeSession
        self.phoneName = phoneName
    }

    #if os(iOS)
    @MainActor
    public static func live() -> PhoneSetup {
        let base = try? FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
        return PhoneSetup(pairings: PairingStore(secrets: KeychainSecretStore()),
                          cacheURL: base?.appendingPathComponent("TodoTracker", isDirectory: true).appendingPathComponent("computer-board.json"),
                          phoneName: UIDevice.current.name, deviceKind: UIDevice.current.userInterfaceIdiom == .pad ? "iPad" : "iPhone")
    }
    #endif
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
        // When the focus session or the break ends (with the app closed or the phone locked).
        // The app may be asleep when the break starts, so the break's end is set up with the session's.
        let p = board.pomodoro
        if p.isRunning, let end = p.endsAt, p.phase != .idle {
            var ends: [(at: Date, focus: Bool)] = [(end, p.phase == .focus)]
            if p.phase == .focus {
                let isLong = (p.completedFocusCount + 1) % max(1, p.settings.focusesBeforeLongBreak) == 0
                ends.append((end.addingTimeInterval(p.settings.duration(of: isLong ? .longBreak : .shortBreak)), false))
            }
            for (i, phaseEnd) in ends.enumerated() where phaseEnd.at > now {
                let content = UNMutableNotificationContent()
                content.title = phaseEnd.focus ? "🍅 Focus session done" : "Break is over"
                content.body = phaseEnd.focus ? "Nice work. Stand up, stretch, drink some water." : "Ready for the next focus block? Open to start it."
                content.sound = .default
                content.interruptionLevel = .timeSensitive
                let trigger = UNTimeIntervalNotificationTrigger(timeInterval: max(1, phaseEnd.at.timeIntervalSince(now)), repeats: false)
                center.add(UNNotificationRequest(identifier: "focus-timer-\(i)", content: content, trigger: trigger))
            }
        }
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

#endif
