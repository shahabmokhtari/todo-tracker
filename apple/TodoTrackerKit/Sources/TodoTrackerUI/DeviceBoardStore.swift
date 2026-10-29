import Foundation
import TodoTrackerKit

/// Where the tasks kept on this device live: the phone's tasks folder (iPhone and iPad), or the Mac app's board file
/// (used until the Mac's server has the tasks).
public protocol DeviceBoardStore: AnyObject {
    /// The board to show (a copy when the store keeps its own).
    func load() throws -> TaskBoard
    /// Makes a change and saves it; returns the board to show. `shown` is the model's board.
    func apply(_ shown: TaskBoard, _ change: (TaskBoard) throws -> Void) throws -> TaskBoard
    /// The store keeps its own board: the model's is a copy, and a change is made again to the store's.
    var keepsOwnBoard: Bool { get }
    /// The board moved into the tasks folder (the Mac's server imported it).
    var wasMigrated: Bool { get }
}

extension BoardFileStore: DeviceBoardStore {
    public func apply(_ shown: TaskBoard, _ change: (TaskBoard) throws -> Void) throws -> TaskBoard {
        try change(shown)
        try save(shown)
        return shown
    }

    public var keepsOwnBoard: Bool { false }
}

/// The phone's tasks folder (`VaultStore`): the files are the truth; the model shows a copy of the board read from
/// them, and every change goes through the store (which saves only the files it touched). The same store syncs.
public final class VaultBoardStore: DeviceBoardStore {
    public let vault: VaultStore

    public init(_ vault: VaultStore) { self.vault = vault }

    /// The phone's tasks folder (`Application Support/TodoTracker/vault`). The first time, the tasks the app kept in
    /// `board.json` move in (it's set aside as `board.json.migrated`, never deleted). With `importLeftover`, a
    /// `board.json` still there (the folder had tasks already) is brought in beside them.
    public static func open(base: URL, importLeftover: Bool) throws -> VaultBoardStore {
        try openReporting(base: base, importLeftover: importLeftover).store
    }

    /// Like `open`, also saying why a leftover `board.json` couldn't come in (it stays where it is; the tasks folder
    /// opens anyway).
    public static func openReporting(base: URL, importLeftover: Bool) throws -> (store: VaultBoardStore, problem: String?) {
        let legacy = base.appendingPathComponent("board.json")
        let vault = try VaultStore(root: base.appendingPathComponent("vault", isDirectory: true), legacyBoard: legacy)
        var problem: String?
        if importLeftover, VaultStore.hasLegacy(legacy) {
            do {
                try vault.importBeside(legacyBoard: legacy)
            } catch {
                problem = "Your tasks from before couldn't be brought in yet (\((error as? LocalizedError)?.errorDescription ?? error.localizedDescription)). They're kept, and tried again next time."
            }
        }
        if problem == nil, let unread = vault.problems.first(where: { $0.path == legacy.path }) { problem = unread.message }
        return (VaultBoardStore(vault), problem)
    }

    /// What the app opens on an iPhone or iPad: the tasks folder, or (when it can't be opened) the tasks kept before,
    /// shown read-only with why, every file left as it is.
    public static func openOnDevice(base: URL, importLeftover: Bool) -> (store: VaultBoardStore?, fallback: TaskBoard?, message: String?) {
        do {
            let opened = try openReporting(base: base, importLeftover: importLeftover)
            return (opened.store, nil, opened.problem)
        } catch {
            let why = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
            let legacy = base.appendingPathComponent("board.json")
            // Read only: nothing is moved or rewritten.
            let shown = FileManager.default.fileExists(atPath: legacy.path) ? (try? BoardCodec.decode(Data(contentsOf: legacy))) : nil
            return (nil, shown, why.hasPrefix("Couldn't open your tasks") ? why : "Couldn't open your tasks folder (\(why)). Changes are off so nothing is lost; restarting the app tries again.")
        }
    }

    public func load() throws -> TaskBoard {
        try vault.read { try BoardCodec.decode(BoardCodec.encode($0)) }
    }

    public func apply(_ shown: TaskBoard, _ change: (TaskBoard) throws -> Void) throws -> TaskBoard {
        try vault.update { try change($0) }
        return try load()
    }

    public var keepsOwnBoard: Bool { true }
    public var wasMigrated: Bool { false }
}
