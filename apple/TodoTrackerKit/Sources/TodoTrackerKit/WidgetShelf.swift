import Foundation

/// "Done" tapped on a widget: which task, and when. The app makes the change (the widget can't reach the tasks), then
/// removes the request.
public struct WidgetDoneRequest: Equatable, Sendable {
    /// The request's own id (its file's name).
    public let id: UUID
    public let task: UUID
    public let at: Date

    public init(id: UUID = UUID(), task: UUID, at: Date) {
        self.id = id
        self.task = task
        self.at = at
    }

    /// What the app does with a request on `board`: mark the task done, or nothing because it's done already (done
    /// since the request, gone, or finished) — so a request is never applied twice, even to a repeating task.
    public enum Outcome: Equatable, Sendable { case complete, alreadyDone }

    public func outcome(on board: TaskBoard) -> Outcome {
        guard let item = board.find(task), !item.isDone else { return .alreadyDone }
        if board.activity.contains(where: { $0.itemId == task && $0.kind == "completed" && $0.at >= at }) { return .alreadyDone }
        return .complete
    }
}

/// What the app and its widgets share (the App Group container on iPhone, iPad and Mac): the snapshot the widgets show
/// (`WidgetSnapshot`: titles, groups, ids and times; never details, notes or tokens) and the tasks marked done from a
/// widget that the app hasn't saved yet. Kept out of backups.
public struct WidgetShelf: Sendable {
    /// The App Group the app and its widgets share: the Info.plist's WidgetAppGroup (on a Mac it starts with the team's
    /// id when signed with one), else group.com.todotracker.
    public static var appGroup: String {
        (Bundle.main.object(forInfoDictionaryKey: "WidgetAppGroup") as? String).flatMap { $0.isEmpty ? nil : $0 } ?? "group.com.todotracker"
    }

    /// Posted in the app's process when Done was tapped on a widget (the app makes the change at once).
    public static let doneRequested = Notification.Name("WidgetShelf.doneRequested")

    public let directory: URL

    public init(directory: URL) {
        self.directory = directory
    }

    /// The shared container (nil when this build has no App Group, e.g. not signed for one: no widgets then).
    public static var shared: WidgetShelf? {
        guard let container = FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: appGroup) else { return nil }
        return WidgetShelf(directory: container.appendingPathComponent("Widgets", isDirectory: true))
    }

    private var snapshotURL: URL { directory.appendingPathComponent("snapshot.json") }
    private var requestsURL: URL { directory.appendingPathComponent("done", isDirectory: true) }

    // MARK: Snapshot

    public func write(_ snapshot: WidgetSnapshot) throws {
        try prepare(directory)
        try snapshot.encoded().write(to: snapshotURL, options: [.atomic])
    }

    public func snapshot() -> WidgetSnapshot? {
        (try? Data(contentsOf: snapshotURL)).flatMap(WidgetSnapshot.decode)
    }

    // MARK: Done from a widget

    /// Keeps a "Done" tapped on a widget for the app (one file each, so the widget and the app never write the same file).
    @discardableResult
    public func requestDone(_ task: UUID, at: Date = Date()) throws -> WidgetDoneRequest {
        // Kept to the millisecond, as it's read back.
        let request = WidgetDoneRequest(task: task, at: BoardCodec.parseDate(BoardCodec.formatDate(at)) ?? at)
        try prepare(requestsURL)
        let json: [String: Any] = ["task": task.uuidString.lowercased(), "at": BoardCodec.formatDate(request.at)]
        try JSONSerialization.data(withJSONObject: json).write(to: requestsURL.appendingPathComponent("\(request.id.uuidString.lowercased()).json"), options: [.atomic])
        return request
    }

    /// The requests not handled yet, oldest first.
    public func pendingDone() -> [WidgetDoneRequest] {
        guard let files = try? FileManager.default.contentsOfDirectory(at: requestsURL, includingPropertiesForKeys: nil) else { return [] }
        return files.compactMap { file -> WidgetDoneRequest? in
            guard file.pathExtension == "json", let id = UUID(uuidString: file.deletingPathExtension().lastPathComponent),
                  let data = try? Data(contentsOf: file),
                  let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let task = (json["task"] as? String).flatMap(UUID.init(uuidString:)),
                  let at = (json["at"] as? String).flatMap(BoardCodec.parseDate) else { return nil }
            return WidgetDoneRequest(id: id, task: task, at: at)
        }.sorted { $0.at < $1.at }
    }

    /// The tasks marked done on a widget, not saved yet: the widgets leave them out meanwhile.
    public func hidden() -> Set<UUID> { Set(pendingDone().map(\.task)) }

    public func remove(_ request: WidgetDoneRequest) {
        try? FileManager.default.removeItem(at: requestsURL.appendingPathComponent("\(request.id.uuidString.lowercased()).json"))
    }

    private func prepare(_ folder: URL) throws {
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        var url = folder
        try? url.setResourceValues(values)
    }
}
