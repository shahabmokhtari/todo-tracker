import Foundation

/// A file the app couldn't read; it is shown to the person and never overwritten until fixed.
public struct VaultProblem: Equatable, Sendable {
    public let path: String
    public let message: String
}

/// The phone's tasks folder: a slim port of C#'s `VaultBoardStore` (docs/design/phone-sync.md, "The phone's store").
///
/// * The files are the truth: the board is read from them, and a change rewrites only the files of the tasks it
///   touched (with each file's last parsed layout, so nothing the app doesn't own is lost).
/// * Reading never rewrites a task file. Saves are all-or-nothing: every touched file is checked against what was last
///   read before anything is written, writes are atomic, and a failure part way puts every file back.
/// * Nothing is deleted outright: a removed task's file goes to `.todo-tracker/trash` (purged after 30 days).
///
/// One writer at a time: every call takes the store's lock. Version history, the file watcher, the cross-process lock
/// and AGENTS.md are left out (phones have no other editor); reacting to completions typed in another editor is left
/// to the device where they were typed.
public final class VaultStore {
    public let root: URL
    public let timeZone: TimeZone
    private let clock: () -> Date
    private let lock = NSRecursiveLock()
    private let fm = FileManager.default

    private var board = TaskBoard(seedDefaultGroups: false)
    private var caches = Caches()
    private var dormantGroups: [ConfigGroup] = []
    private var configText = ""
    private var stateText = ""
    private var activity: [ActivityEntry] = []
    private var activityLength: Int64 = 0
    private var problemList: [VaultProblem] = []
    private var forceContent = true
    private var syncHashes: [OIKey: (stamp: Stamp, hash: String)] = [:]

    /// Opens (and on first use creates) the tasks folder at `root`. A new, empty folder gets the default tabs (Work,
    /// Personal) with ids every device derives alike, unless `legacyBoard` (a `board.json` from before) is given: then
    /// its tasks become the folder's files and the board file is set aside as `.migrated`.
    public init(root: URL, timeZone: TimeZone = .current, clock: @escaping () -> Date = Date.init, legacyBoard: URL? = nil) throws {
        self.root = root.standardizedFileURL
        self.timeZone = timeZone
        self.clock = clock
        try withLock {
            try VaultFiles.makeDirectories(self.root.path)
            let fresh = !fm.fileExists(atPath: metaPath("config.json")) && enumerateTaskFiles().isEmpty
            if fresh, let legacyBoard, let legacy = readLegacy(legacyBoard) {
                try migrate(legacyBoard, legacy)
            } else if fresh {
                // The default tabs in this order; their ids come from the folder name, so two new vaults that later sync
                // share the same tabs.
                let seeded = TaskBoard()
                let groups = seeded.groups.map { ConfigGroup(id: VaultText.stableGuid("group|" + $0.name.oUpper()), folder: $0.name, color: $0.color) }
                for group in seeded.groups { try VaultFiles.makeDirectories(full(group.name)) }
                try VaultFiles.writeAtomic(metaPath("config.json"), Self.configText(groups: groups, labels: [], order: [], energyPerDay: nil))
            }
            purgeOldTrash()
            _ = try reload()
        }
    }

    // ---- Public API -------------------------------------------------------------------

    /// Files that couldn't be read (their last good version stays visible and they are never overwritten).
    public var problems: [VaultProblem] { withLock { problemList } }

    /// Reads the board (after taking in whatever changed on disk since the last read).
    public func read<T>(_ read: (TaskBoard) throws -> T) throws -> T {
        try withLock {
            if forceContent || diskDiffers() { _ = try reload() }
            return try read(board)
        }
    }

    /// Applies a change and saves the files it touched. If a file changed on disk in between, the change is applied
    /// again to what's there now (up to 3 times); if saving fails, nothing is changed.
    @discardableResult
    public func update<T>(_ mutate: (TaskBoard) throws -> T) throws -> T {
        try withLock {
            var attempt = 1
            while true {
                if forceContent || diskDiffers() { _ = try reload() }
                let snapshot = try BoardCodec.encode(board)
                do {
                    let result = try mutate(board)
                    _ = try persist()
                    return result
                } catch is VaultConflict where attempt < 3 {
                    board = try restored(snapshot)
                    markDirty()
                    attempt += 1
                } catch {
                    board = try restored(snapshot)
                    markDirty()
                    throw error
                }
            }
        }
    }

    /// Something may have changed on disk; the next read re-reads every file.
    public func markDirty() { withLock { forceContent = true } }

    /// Vault-relative path (forward slashes) of the file holding the task (its top-level task's file).
    public func path(of id: UUID) -> String? {
        withLock {
            guard let item = board.find(id) else { return nil }
            return caches.paths[item.root.id]
        }
    }

    /// The tasks an app kept on its own before it used this folder (the phone's `board.json`), brought in **beside**
    /// what's here: a top-level task whose id (or any of its subtasks' ids) is already here is skipped, never replaced;
    /// the others go to the tab of the same name, else the first tab. Then the file is set aside as `.migrated`.
    /// Returns how many tasks came in.
    @discardableResult
    public func importBeside(legacyBoard url: URL) throws -> Int {
        try withLock {
            guard let legacy = readLegacy(url) else { return 0 }
            if forceContent || diskDiffers() { _ = try reload() }
            // All or nothing: if saving fails part way, the files are put back (persist) and so is the board here.
            let snapshot = try BoardCodec.encode(board)
            do {
                var added = 0
                for item in legacy.items {
                    if hasAnyId(item) { continue }
                    let tab = legacy.groups.first { $0.id == item.groupId }?.name
                    let group = board.groups.first { Ordinal.equalIgnoreCase($0.name, tab) } ?? board.groups[0]
                    try board.attachLoaded(item, groupId: group.id)
                    added += 1
                }
                _ = try persist()
                try setAside(url)
                return added
            } catch {
                board = try restored(snapshot)
                markDirty()
                throw error
            }
        }
    }

    /// Whether `text` would be read as a task file at `rel` (merged text is checked before it's written).
    public func isValidTaskText(_ rel: String, _ text: String) -> Bool {
        (try? TaskMarkdown.parse(text, fileStem: VaultFiles.stem(rel), context: context(rel))) != nil
    }

    // ---- Startup ----------------------------------------------------------------------

    /// An old board file, or nil when there's none or it can't be read (then it's left where it is, untouched).
    private func readLegacy(_ url: URL) -> TaskBoard? {
        guard fm.fileExists(atPath: url.path) else { return nil }
        do {
            return try BoardCodec.decode(Data(contentsOf: url))
        } catch {
            problemList.append(VaultProblem(path: url.path, message: "These tasks couldn't be brought into the tasks folder: \(error.localizedDescription)"))
            return nil
        }
    }

    private func migrate(_ url: URL, _ legacy: TaskBoard) throws {
        board = legacy
        var folders: [UUID: String] = [:]
        var order: [UUID] = []
        for group in board.groups {
            let folder = VaultFiles.uniqueStem(VaultFiles.safeFolderName(group.name)) { f in folders.values.contains { Ordinal.equalIgnoreCase($0, f) } }
            folders[group.id] = folder
            order.append(group.id)
            try VaultFiles.makeDirectories(full(folder))
        }
        caches = Caches()
        caches.groupFolders = folders
        _ = try persist()
        try setAside(url)
    }

    /// The old board file and its backups are renamed `.migrated`: kept, and never read again. An earlier `.migrated`
    /// is never replaced: this one gets the next free name (`.migrated 2`, `.migrated 3`…).
    private func setAside(_ url: URL) throws {
        for path in [url.path, url.path + ".bak", url.path + ".restoring"] where fm.fileExists(atPath: path) {
            var target = path + ".migrated"
            var n = 2
            while fm.fileExists(atPath: target) {
                target = path + ".migrated \(n)"
                n += 1
            }
            try VaultFiles.move(path, target)
        }
    }

    // ---- Reading the vault --------------------------------------------------------------

    struct Stamp: Equatable {
        var time: Date?
        var size: Int64
    }

    /// The stamp of a file that is there but couldn't be read (never equal to a real one, so it's read again).
    static let unreadableStamp = Stamp(time: nil, size: -2)

    /// What we last saw of a file: its text, the last version that parsed, and why it doesn't parse now.
    struct FileState {
        var text: String
        var good: String?
        var stamp: Stamp
        var error: String?
    }

    /// Everything the store remembers about the files; replaced as a whole after a successful save.
    struct Caches {
        var files: [OIKey: FileState] = [:]
        var paths: [UUID: String] = [:]
        var groupFolders: [UUID: String] = [:]
        var fileIds: [OIKey: Set<UUID>] = [:]
        var layouts: [OIKey: ParsedTaskFile] = [:]
        var signatures: [UUID: String] = [:]
        var blocked: Set<UUID> = []
    }

    struct ConfigGroup {
        var id: UUID
        var folder: String
        var color: String?
    }

    private struct VaultConflict: Error, LocalizedError {
        let path: String
        var errorDescription: String? { "\"\(path)\" changed while saving." }
    }

    /// A change the vault can't take as it is (a file that can't be read, content only a top-level task can keep).
    public struct VaultRefusal: Error, LocalizedError {
        public let message: String
        public var errorDescription: String? { message }
    }

    /// Cheap check (names, sizes, times) for changes since the last read.
    private func diskDiffers() -> Bool {
        let taskFiles = enumerateTaskFiles()
        for file in taskFiles {
            guard let state = caches.files[OIKey(file)], state.stamp == stamp(file) else { return true }
        }
        if taskFiles.count != caches.files.count { return true }
        if Set(topFolders().map(OIKey.init)) != Set(caches.groupFolders.values.map(OIKey.init)) { return true }
        guard let config = try? readOrEmpty(metaPath("config.json")), let state = try? readOrEmpty(metaPath("state.json")) else { return true }
        return !config.oEq(configText) || !state.oEq(stateText) || fileLength(metaPath("activity.jsonl")) != activityLength
    }

    /// Rebuilds the board from disk. Returns whether anything changed.
    private func reload() throws -> Bool {
        let force = forceContent
        forceContent = false
        let before = try BoardCodec.encode(board)
        let oldBoard = board
        let oldCaches = caches
        let config = try readConfig()
        let folders = topFolders()

        // Read every task file (unchanged files come from the cache).
        var files: [OIKey: FileState] = [:]
        var parsedByFolder: [OIKey: [(rel: String, parsed: ParsedTaskFile)]] = [:]
        for folder in folders { parsedByFolder[OIKey(folder)] = [] }
        var problems: [VaultProblem] = []
        for folder in folders {
            for rel in enumerateTaskFiles(in: folder) {
                let state = readFile(rel, oldCaches, force)
                files[OIKey(rel)] = state
                if let error = state.error { problems.append(VaultProblem(path: rel, message: error)) }
                if let good = state.good, let parsed = try? parseText(good, rel) { parsedByFolder[OIKey(folder)]!.append((rel, parsed)) }
            }
        }

        // Groups: config order; a renamed folder keeps its group (recognized by the tasks inside); new folders append.
        // Config groups whose folder is missing stay dormant (a sync may deliver the folder later).
        var groups: [(id: UUID, folder: String, color: String?)] = []
        var claimed = Set<OIKey>()
        var dormant: [ConfigGroup] = []
        for g in config.groups {
            let known = groups.contains { $0.id == g.id }
            var folder = folders.first { Ordinal.equalIgnoreCase($0, g.folder) && !claimed.contains(OIKey($0)) }
            if folder == nil && !known {
                folder = folders.first { f in
                    !claimed.contains(OIKey(f)) && !config.groups.contains { Ordinal.equalIgnoreCase($0.folder, f) }
                        && parsedByFolder[OIKey(f)]!.contains { oldBoard.find($0.parsed.root.id)?.groupId == g.id }
                }
            }
            guard let found = folder, !known else {
                if folder == nil && !known { dormant.append(g) }
                continue
            }
            claimed.insert(OIKey(found))
            groups.append((g.id, found, g.color))
        }
        for folder in folders.filter({ !claimed.contains(OIKey($0)) }).sorted(by: Ordinal.lessIgnoreCase) {
            claimed.insert(OIKey(folder))
            groups.append((VaultText.stableGuid("group|" + folder.oUpper()), folder, nil))
        }
        if groups.isEmpty {
            try VaultFiles.makeDirectories(full("Inbox"))
            groups.append((VaultText.stableGuid("group|INBOX"), "Inbox", nil))
            parsedByFolder[OIKey("Inbox")] = []
        }

        let next = TaskBoard(seedDefaultGroups: false)
        next.timeZone = timeZone
        var groupFolders: [UUID: String] = [:]
        for g in groups {
            next.appendLoaded(group: TaskGroup(id: g.id, name: g.folder, color: g.color))
            groupFolders[g.id] = g.folder
        }
        for label in config.labels where !next.labels.contains(where: { Ordinal.equalIgnoreCase($0.name, label.name) }) {
            next.labels.append(LabelDefinition(name: label.name, color: label.color))
        }
        next.energyPerDay = config.energyPerDay.flatMap { (1...Energy.maxPerDay).contains($0) ? $0 : nil }

        // Tasks in the remembered order. A copied file's ids are taken: it gets ids derived from its path (stable,
        // without rewriting it); the file that had them before keeps them.
        var order: [UUID: Int] = [:]
        for (i, id) in config.order.enumerated() where order[id] == nil { order[id] = i }
        var paths: [UUID: String] = [:]
        var fileIds: [OIKey: Set<UUID>] = [:]
        var layouts: [OIKey: ParsedTaskFile] = [:]
        var candidates: [(group: UUID, rel: String, parsed: ParsedTaskFile)] = []
        for g in groups { for p in parsedByFolder[OIKey(g.folder)] ?? [] { candidates.append((g.id, p.rel, p.parsed)) } }
        candidates = TaskMarkdown.stableSorted(candidates) { a, b in
            let ka = oldCaches.paths[a.parsed.root.id].map { Ordinal.equalIgnoreCase($0, a.rel) } ?? false
            let kb = oldCaches.paths[b.parsed.root.id].map { Ordinal.equalIgnoreCase($0, b.rel) } ?? false
            if ka != kb { return ka }
            return Ordinal.lessIgnoreCase(a.rel, b.rel)
        }
        for (groupId, rel, parsed) in candidates {
            let rootItem = parsed.root
            if rootItem.selfAndDescendants.contains(where: { next.find($0.id) != nil }) {
                for item in rootItem.selfAndDescendants where item === rootItem || next.find(item.id) != nil {
                    item.id = VaultText.stableGuid("\(rel)|copy|\(guidD(item.id))")
                }
                parsed.blockIds.removeAll()
            }
            try next.attachLoaded(rootItem, groupId: groupId)
            paths[rootItem.id] = rel
            fileIds[OIKey(rel)] = Set(rootItem.selfAndDescendants.map(\.id))
            layouts[OIKey(rel)] = parsed
        }
        next.items = TaskMarkdown.stableSorted(next.items) { (order[$0.id] ?? Int.max) < (order[$1.id] ?? Int.max) }

        loadActivity(next)
        try loadState(next)

        var blocked = Set<UUID>()
        for (rel, state) in files where state.error != nil {
            if let id = paths.first(where: { OIKey($0.value) == rel })?.key { blocked.insert(id) }
        }

        board = next
        dormantGroups = dormant
        var signatures: [UUID: String] = [:]
        for item in next.items { signatures[item.id] = signature(item) }
        caches = Caches(files: files, paths: paths, groupFolders: groupFolders, fileIds: fileIds, layouts: layouts, signatures: signatures, blocked: blocked)
        problemList = problems

        // Tasks waiting for one finished or deleted elsewhere come back with a reminder.
        let changed = board.releaseFinishedWaits(now: clock(), skipRoots: blocked) ? try persist() : try writeMetadataIfChanged()
        return try changed || BoardCodec.encode(board) != before
    }

    private func readFile(_ rel: String, _ old: Caches, _ force: Bool) -> FileState {
        let s = stamp(rel)
        let cached = old.files[OIKey(rel)]
        if let cached, cached.stamp == s, !force { return cached }
        let text: String
        do {
            text = try readText(full(rel)) ?? ""
        } catch {
            // There but unreadable: shown as a problem; its last good version stays and it is never written over.
            return FileState(text: cached?.text ?? "", good: cached?.good, stamp: Self.unreadableStamp, error: error.localizedDescription)
        }
        if var cached, cached.text.oEq(text) {
            cached.stamp = s
            return cached
        }
        do {
            _ = try parseText(text, rel)
            return FileState(text: text, good: text, stamp: s, error: nil)
        } catch {
            return FileState(text: text, good: cached?.good, stamp: s, error: (error as? VaultFormatError)?.message ?? "\(error)")
        }
    }

    private func parseText(_ text: String, _ rel: String) throws -> ParsedTaskFile {
        try TaskMarkdown.parse(text, fileStem: VaultFiles.stem(rel), context: context(rel))
    }

    private func context(_ rel: String) -> TaskMarkdownContext {
        TaskMarkdownContext(timeZone: timeZone, now: clock(), fileDirectory: VaultFiles.directory(rel), idSeed: rel)
    }

    private func signature(_ item: WorkItem) -> String {
        let json = (try? BoardCodec.encoder.encode(ItemDocument(item))).map { String(decoding: $0, as: UTF8.self) } ?? ""
        return "\(item.groupId)|\(json)"
    }

    private func hasAnyId(_ item: WorkItem) -> Bool { item.selfAndDescendants.contains { board.find($0.id) != nil } }

    private func restored(_ snapshot: Data) throws -> TaskBoard {
        let b = try BoardCodec.decode(snapshot)
        b.timeZone = timeZone
        return b
    }

    // ---- Writing the vault --------------------------------------------------------------

    private struct SavePlan {
        var operations: [(inout [() throws -> Void]) throws -> Void] = []
        var touched = Set<OIKey>()
        var touchedOrder: [String] = []
        var caches: Caches

        mutating func touch(_ rel: String) { if touched.insert(OIKey(rel)).inserted { touchedOrder.append(rel) } }
    }

    /// Saves what changed: plans every file operation, verifies each touched file still has the content the app last
    /// read, then commits with an undo log so a failure part way leaves the vault as it was.
    private func persist() throws -> Bool {
        var plan = try makePlan()
        if plan.operations.isEmpty { return try writeMetadataIfChanged() }

        // Verify before touching anything: a file that changed since we read it means re-read and re-apply.
        for rel in plan.touchedOrder {
            let path = full(rel)
            if let known = caches.files[OIKey(rel)] {
                guard let now = try readText(path), now.oEq(known.text) else { throw VaultConflict(path: rel) }
            } else if fm.fileExists(atPath: path) {
                throw VaultConflict(path: rel)
            }
        }

        var undo: [() throws -> Void] = []
        let previous = caches
        let previousMeta = (configText, stateText, activity, activityLength)
        do {
            for op in plan.operations { try op(&undo) }
            // Metadata (groups, order) describes the new state, so it is written from the new caches.
            for rel in plan.touchedOrder where plan.caches.files[OIKey(rel)] != nil {
                plan.caches.files[OIKey(rel)]!.stamp = stamp(rel)
            }
            caches = plan.caches
            _ = try writeMetadataIfChanged(&undo)
        } catch {
            caches = previous
            (configText, stateText, activity, activityLength) = previousMeta
            for step in undo.reversed() { try? step() }
            throw error
        }
        return true
    }

    private func makePlan() throws -> SavePlan {
        let current = caches
        var plan = SavePlan(caches: current)

        // Groups: create, rename (moving files and links with them), or retire their folders.
        var renames: [(from: String, to: String)] = []
        for group in board.groups {
            let wanted = VaultFiles.safeFolderName(group.name)
            if let folder = plan.caches.groupFolders[group.id] {
                if !VaultFiles.safeFolderName(folder).oEq(wanted) {
                    let target = VaultFiles.uniqueStem(wanted) { f in
                        (plan.caches.groupFolders.values.contains { Ordinal.equalIgnoreCase($0, f) } || dirExists(full(f))) && !Ordinal.equalIgnoreCase(f, folder)
                    }
                    renames.append((folder, target))
                    plan.caches.groupFolders[group.id] = target
                }
            } else {
                let target = VaultFiles.uniqueStem(wanted) { f in plan.caches.groupFolders.values.contains { Ordinal.equalIgnoreCase($0, f) } || dirExists(full(f)) }
                plan.caches.groupFolders[group.id] = target
                plan.operations.append { undo in try self.createFolder(target, &undo) }
            }
        }

        var relocations: [(from: String, to: String)] = []
        for (id, folder) in plan.caches.groupFolders where !board.groups.contains(where: { $0.id == id }) {
            plan.caches.groupFolders[id] = nil
            let target = VaultFiles.uniqueStem("\(VaultFiles.attachmentsFolder)/\(folder)") { dirExists(full($0)) }
            relocations.append((folder, target))
        }
        for r in renames { remapPrefix(&plan.caches, r.from, r.to, includeTasks: true) }
        for r in relocations { remapPrefix(&plan.caches, r.from, r.to, includeTasks: false) }
        for r in renames { plan.operations.insert({ undo in try self.moveFolder(r.from, r.to, &undo) }, at: 0) }

        // Tasks: write the ones that changed (or moved); a different group keeps the file name and subfolder.
        var writes: [(rel: String, text: String, gained: Bool)] = []
        var reserved = Set(plan.caches.paths.values.map(OIKey.init))
        for item in board.items {
            let folder = plan.caches.groupFolders[item.groupId]!
            let sig = signature(item)
            var rel: String
            let known = plan.caches.paths[item.id]
            if let known {
                rel = known
                if !Ordinal.equalIgnoreCase(rel.oSplit("/")[0], folder) {
                    let source = rel
                    let name = VaultFiles.stem(rel)
                    var sub = VaultFiles.directory(rel)
                    sub = sub.oContains("/") ? sub.sub(sub.oIndex("/")! + 1) : ""
                    rel = newFilePath(sub.isEmpty ? folder : folder + "/" + sub, name, reserved)
                    reserved.insert(OIKey(rel))
                    moveEntry(&plan.caches, source, rel)
                    let target = rel
                    plan.touch(source)
                    plan.operations.append { undo in try self.moveTaskFile(source, target, &undo) }
                }
            } else {
                rel = newFilePath(folder, VaultFiles.safeName(item.title), reserved)
                reserved.insert(OIKey(rel))
            }

            if current.blocked.contains(item.id) {
                if current.signatures[item.id] != sig {
                    let why = problemList.first { $0.path.oEq(rel) }?.message ?? ""
                    throw VaultRefusal(message: "\"\(rel)\" can't be read right now (\(why)). Fix the file, then try again.")
                }
                continue
            }

            plan.caches.paths[item.id] = rel
            if known != nil, current.signatures[item.id] == sig, rel.oEq(current.paths[item.id]!) { continue }

            let text = TaskMarkdown.render(item, context: context(rel), previous: layout(&plan.caches, rel))
            let ids = Set(item.selfAndDescendants.map(\.id))
            let gained = !(plan.caches.fileIds[OIKey(rel)].map { ids.isSubset(of: $0) } ?? false)
            plan.caches.signatures[item.id] = sig
            plan.caches.fileIds[OIKey(rel)] = ids
            if !(plan.caches.files[OIKey(rel)]?.text.oEq(text) ?? false) { writes.append((rel, text, gained)) }
        }

        // Files that gain tasks are written before files that lose them: a crash in between duplicates, never loses.
        for w in TaskMarkdown.stableSorted(writes, { $0.gained && !$1.gained }) {
            plan.touch(w.rel)
            plan.caches.files[OIKey(w.rel)] = FileState(text: w.text, good: w.text, stamp: Stamp(time: nil, size: -1), error: nil)
            plan.caches.layouts[OIKey(w.rel)] = nil
            plan.operations.append { undo in try self.writeTaskFile(w.rel, w.text, &undo) }
        }

        // Top-level tasks that are gone (deleted, or now a subtask) go to the trash.
        let live = Set(board.items.map(\.id))
        for (id, rel) in plan.caches.paths where !live.contains(id) {
            if current.blocked.contains(id) { throw VaultRefusal(message: "\"\(rel)\" can't be read right now, so it can't be removed. Fix the file first.") }
            if board.find(id) != nil && hasFileOnlyContent(rel) {
                throw VaultRefusal(message: "\"\(rel)\" has content that only a top-level task can keep (a rich version, extra properties, or sections). Move that content first.")
            }
            plan.caches.paths[id] = nil
            plan.caches.signatures[id] = nil
            plan.caches.fileIds[OIKey(rel)] = nil
            plan.caches.files[OIKey(rel)] = nil
            plan.caches.layouts[OIKey(rel)] = nil
            plan.touch(rel)
            plan.operations.append { undo in try self.trashTaskFile(rel, &undo) }
        }

        for r in relocations { plan.operations.append { undo in try self.retireFolder(r.from, r.to, &undo) } }
        return plan
    }

    /// A folder moved: task paths, cached files, and attachment links under it move along.
    private func remapPrefix(_ c: inout Caches, _ from: String, _ to: String, includeTasks: Bool) {
        func remap(_ path: String) -> String? {
            Ordinal.upper(path).oStarts(Ordinal.upper(from + "/")) ? to + path.sub(from.u16) : nil
        }
        if includeTasks {
            for (id, rel) in c.paths {
                if let moved = remap(rel) {
                    moveEntry(&c, rel, moved)
                    c.paths[id] = moved
                }
            }
        }
        for item in board.allItems {
            for attachment in item.attachments { if let moved = remap(attachment.path) { attachment.path = moved } }
        }
    }

    private func moveEntry(_ c: inout Caches, _ from: String, _ to: String) {
        if let state = c.files.removeValue(forKey: OIKey(from)) { c.files[OIKey(to)] = state }
        if let ids = c.fileIds.removeValue(forKey: OIKey(from)) { c.fileIds[OIKey(to)] = ids }
        if let layout = c.layouts.removeValue(forKey: OIKey(from)) { c.layouts[OIKey(to)] = layout }
    }

    private func layout(_ c: inout Caches, _ rel: String) -> ParsedTaskFile? {
        if let layout = c.layouts[OIKey(rel)] { return layout }
        guard let good = c.files[OIKey(rel)]?.good, let layout = try? parseText(good, rel) else { return nil }
        c.layouts[OIKey(rel)] = layout
        return layout
    }

    /// Content that would be lost if the file stopped being a top-level task.
    private func hasFileOnlyContent(_ rel: String) -> Bool {
        if fm.fileExists(atPath: full(VaultFiles.changeExtension(rel, ".html"))) { return true }
        guard let l = layout(&caches, rel) else { return false }
        return l.layout.contains { $0.kind == nil } || !l.leading.isEmpty || l.subtasksTrailer != nil || l.notesPreamble != nil
            || l.attachmentsPreamble != nil || l.timePreamble != nil
            || l.frontmatter.blocks.contains { $0.key == nil || !(Ordinal.equalIgnoreCase($0.key, l.idKey) || TaskMarkdown.isOwnedProperty($0.key!)) }
    }

    private func newFilePath(_ folder: String, _ stem: String, _ reserved: Set<OIKey>) -> String {
        let unique = VaultFiles.uniqueStem(stem) { s in
            let rel = "\(folder)/\(s).md"
            return reserved.contains(OIKey(rel)) || fm.fileExists(atPath: full(rel)) || fm.fileExists(atPath: full("\(folder)/\(s).html"))
        }
        return "\(folder)/\(unique).md"
    }

    private func createFolder(_ folder: String, _ undo: inout [() throws -> Void]) throws {
        let path = full(folder)
        if !dirExists(path) {
            try VaultFiles.makeDirectories(path)
            undo.append { try self.fm.removeItem(atPath: path) }
        }
    }

    private func moveFolder(_ from: String, _ to: String, _ undo: inout [() throws -> Void]) throws {
        let source = full(from), target = full(to)
        if !dirExists(source) {
            try createFolder(to, &undo)
            return
        }
        if Ordinal.equalIgnoreCase(from, to) {
            // Case-only rename: through a temporary name (case-insensitive file systems).
            let temp = target + ".rename-" + String(guidN(UUID()).prefix(6))
            try VaultFiles.move(source, temp)
            try VaultFiles.move(temp, target)
        } else {
            try VaultFiles.move(source, target)
        }
        undo.append { try VaultFiles.move(target, source) }
    }

    /// A deleted group's folder: whatever is left in it (images, other notes) moves under _attachments.
    private func retireFolder(_ from: String, _ to: String, _ undo: inout [() throws -> Void]) throws {
        let source = full(from)
        guard dirExists(source) else { return }
        if (try fm.contentsOfDirectory(atPath: source)).isEmpty {
            try fm.removeItem(atPath: source)
            undo.append { try VaultFiles.makeDirectories(source) }
            return
        }
        let target = full(to)
        try VaultFiles.makeDirectories((target as NSString).deletingLastPathComponent)
        try VaultFiles.move(source, target)
        undo.append { try VaultFiles.move(target, source) }
    }

    private func writeTaskFile(_ rel: String, _ text: String, _ undo: inout [() throws -> Void]) throws {
        let path = full(rel)
        let previous = try readData(path)
        try VaultFiles.writeAtomic(path, text, exclusive: previous == nil)
        undo.append {
            if let previous { try VaultFiles.writeAtomic(path, previous) } else { try self.fm.removeItem(atPath: path) }
        }
    }

    private func moveTaskFile(_ from: String, _ to: String, _ undo: inout [() throws -> Void]) throws {
        let source = full(from), target = full(to)
        try VaultFiles.makeDirectories((target as NSString).deletingLastPathComponent)
        if fm.fileExists(atPath: source) {
            try VaultFiles.move(source, target)
            undo.append { try VaultFiles.move(target, source) }
        }
        let html = full(VaultFiles.changeExtension(from, ".html"))
        if fm.fileExists(atPath: html) {
            let htmlTarget = full(VaultFiles.changeExtension(to, ".html"))
            try VaultFiles.move(html, htmlTarget)
            undo.append { try VaultFiles.move(htmlTarget, html) }
        }
    }

    private func trashTaskFile(_ rel: String, _ undo: inout [() throws -> Void]) throws {
        for file in [full(rel), full(VaultFiles.changeExtension(rel, ".html"))] where fm.fileExists(atPath: file) {
            let trash = try trashPath((file as NSString).lastPathComponent)
            try VaultFiles.move(file, trash)
            undo.append { try VaultFiles.move(trash, file) }
        }
    }

    /// `.todo-tracker/trash/<yyyyMMdd-HHmmss> <name>` (` 2`, ` 3`… on clashes).
    private func trashPath(_ name: String) throws -> String {
        let trash = metaPath("trash")
        try VaultFiles.makeDirectories(trash)
        let t = LocalDateTime(wallMillis: VaultText.millis(clock()))
        let stamp = String(format: "%04d%02d%02d-%02d%02d%02d", t.year, t.month, t.day, t.hour, t.minute, t.second)
        var candidate = trash + "/\(stamp) \(name)"
        var n = 2
        while fm.fileExists(atPath: candidate) {
            candidate = trash + "/\(stamp) \(n) \(name)"
            n += 1
        }
        return candidate
    }

    /// Empties the trash of what was put there more than 30 days ago, dated by the `yyyyMMdd-HHmmss ` its name starts
    /// with (a file keeps its own time when it's moved to the trash, so that can't say when it was put there). Anything
    /// without that stamp is left alone.
    private func purgeOldTrash() {
        let trash = metaPath("trash")
        guard let names = try? fm.contentsOfDirectory(atPath: trash) else { return }
        let cutoff = clock().addingTimeInterval(-30 * 86_400)
        for name in names {
            guard let trashed = Self.trashedAt(name), trashed < cutoff else { continue }
            try? fm.removeItem(atPath: trash + "/" + name)
        }
    }

    private static let trashStamp = Rx(#"^(\d{4})(\d{2})(\d{2})-(\d{2})(\d{2})(\d{2}) "#)

    /// When a trash entry was put there (UTC, from its name), or nil when its name doesn't say.
    static func trashedAt(_ name: String) -> Date? {
        guard let m = trashStamp.match(name) else { return nil }
        let v = (1...6).map { Int(m.group($0))! }
        guard (1...12).contains(v[1]), v[2] >= 1, v[2] <= LocalDay.length(v[0], v[1]), v[3] < 24, v[4] < 60, v[5] < 60 else { return nil }
        return VaultText.date(millis: LocalDateTime(year: v[0], month: v[1], day: v[2], hour: v[3], minute: v[4], second: v[5]).wallMillis)
    }

    // ---- Settings, timer state, activity ------------------------------------------------

    private struct Config {
        var groups: [ConfigGroup] = []
        var labels: [(name: String, color: String)] = []
        var order: [UUID] = []
        var energyPerDay: Int?
    }

    private func readConfig() throws -> Config {
        let text = try readOrEmpty(metaPath("config.json"))
        configText = text
        guard !text.isEmpty, let doc = (try? JSONText.parse(text))?.objectValue else { return Config() }
        var config = Config()
        for g in doc.get("groups")?.arrayValue ?? [] {
            guard let o = g.objectValue, let id = parseGuid(o.get("id")?.stringValue), let folder = o.get("folder")?.stringValue else { return Config() }
            config.groups.append(ConfigGroup(id: id, folder: folder, color: o.get("color")?.stringValue))
        }
        for l in doc.get("labels")?.arrayValue ?? [] {
            guard let o = l.objectValue, let name = o.get("name")?.stringValue else { return Config() }
            config.labels.append((name, o.get("color")?.stringValue ?? "#64748b"))
        }
        for id in doc.get("order")?.arrayValue ?? [] {
            guard let g = parseGuid(id.stringValue) else { return Config() }
            config.order.append(g)
        }
        config.energyPerDay = doc.get("energyPerDay")?.int64Value.map { Int($0) }
        return config
    }

    static func configText(groups: [ConfigGroup], labels: [(name: String, color: String)], order: [UUID], energyPerDay: Int?) -> String {
        var doc = JSONObject()
        doc["schemaVersion"] = .int(2)
        doc["groups"] = .array(groups.map { g in
            var o = JSONObject([("id", .string(guidD(g.id))), ("folder", .string(g.folder))])
            if let color = g.color { o["color"] = .string(color) }
            return .object(o)
        })
        doc["labels"] = .array(labels.map { .object(JSONObject([("name", .string($0.name)), ("color", .string($0.color))])) })
        doc["order"] = .array(order.map { .string(guidD($0)) })
        if let energyPerDay { doc["energyPerDay"] = .int(energyPerDay) }
        return JSONText.write(.object(doc), indented: true) + "\n"
    }

    private func currentConfigText() -> String {
        let folders = caches.groupFolders
        let groups = board.groups.compactMap { g in folders[g.id].map { ConfigGroup(id: g.id, folder: $0, color: g.color) } } + dormantGroups
        return Self.configText(groups: groups, labels: board.labels.map { ($0.name, $0.color) }, order: board.items.map(\.id), energyPerDay: board.energyPerDay)
    }

    private func writeMetadataIfChanged() throws -> Bool {
        var undo: [() throws -> Void] = []
        return try writeMetadataIfChanged(&undo)
    }

    private func writeMetadataIfChanged(_ undo: inout [() throws -> Void]) throws -> Bool {
        var changed = try writeIfChanged(metaPath("config.json"), currentConfigText(), &configText, &undo)
        changed = try writeIfChanged(metaPath("state.json"), currentStateText(), &stateText, &undo) || changed
        return try appendActivity(&undo) || changed
    }

    private func writeIfChanged(_ path: String, _ text: String, _ known: inout String, _ undo: inout [() throws -> Void]) throws -> Bool {
        if text.oEq(known) { return false }
        let previous = known
        try VaultFiles.writeAtomic(path, text)
        known = text
        undo.append {
            if previous.isEmpty { try self.fm.removeItem(atPath: path) } else { try VaultFiles.writeAtomic(path, previous) }
        }
        return true
    }

    private func loadState(_ b: TaskBoard) throws {
        let text = try readOrEmpty(metaPath("state.json"))
        stateText = text
        guard !text.isEmpty, let s = (try? JSONText.parse(text))?.objectValue else { return }
        func instant(_ key: String) -> Date? { s.get(key)?.stringValue.flatMap { Instant(parsing: $0) }?.date }
        func int(_ o: JSONObject?, _ key: String) -> Int? { o?.get(key)?.int64Value.map { Int($0) } }
        let settingsObject = s.get("settings")?.objectValue
        var settings = PomodoroSettings()
        if let o = settingsObject {
            settings = PomodoroSettings(focusMinutes: int(o, "focusMinutes") ?? 25, shortBreakMinutes: int(o, "shortBreakMinutes") ?? 5,
                                        longBreakMinutes: int(o, "longBreakMinutes") ?? 15, focusesBeforeLongBreak: int(o, "focusesBeforeLongBreak") ?? 4)
        }
        var timer = PomodoroTimer(settings: settings)
        let phase = s.get("phase")?.stringValue.flatMap { p in [PomodoroPhase.idle, .focus, .shortBreak, .longBreak].first { Ordinal.equalIgnoreCase($0.rawValue, p) } } ?? .idle
        let itemId = parseGuid(s.get("itemId")?.stringValue).flatMap { b.find($0) != nil ? $0 : nil }
        timer.restore(phase: phase, endsAt: instant("endsAt"), pausedRemaining: int(s, "pausedRemainingSeconds").map(TimeInterval.init),
                      itemId: itemId, completedFocusCount: int(s, "completedFocusCount") ?? 0, breakEndedAt: instant("breakEndedAt"))
        b.pomodoro = timer
        var seen = Set<UUID>()
        b.nowOrder = (s.get("nowOrder")?.arrayValue ?? []).compactMap { parseGuid($0.stringValue) }.filter { b.find($0) != nil && seen.insert($0).inserted }
        b.nowArrangedAt = instant("nowArrangedAt")
    }

    private func currentStateText() -> String {
        let p = board.pomodoro
        var doc = JSONObject()
        doc["settings"] = .object(JSONObject([("focusMinutes", .int(p.settings.focusMinutes)), ("shortBreakMinutes", .int(p.settings.shortBreakMinutes)),
                                              ("longBreakMinutes", .int(p.settings.longBreakMinutes)), ("focusesBeforeLongBreak", .int(p.settings.focusesBeforeLongBreak))]))
        doc["phase"] = .string(p.phase.rawValue)
        if let endsAt = p.endsAt { doc["endsAt"] = .instant(Instant(endsAt)) }
        if let r = p.pausedRemaining { doc["pausedRemainingSeconds"] = .int(Int(r.rounded(.toNearestOrEven))) }
        if let id = p.itemId { doc["itemId"] = .string(guidD(id)) }
        doc["completedFocusCount"] = .int(p.completedFocusCount)
        if !board.nowOrder.isEmpty { doc["nowOrder"] = .array(board.nowOrder.map { .string(guidD($0)) }) }
        if let at = p.breakEndedAt { doc["breakEndedAt"] = .instant(Instant(at)) }
        if let at = board.nowArrangedAt { doc["nowArrangedAt"] = .instant(Instant(at)) }
        return JSONText.write(.object(doc), indented: true) + "\n"
    }

    private static let activityKinds = ["created", "updated", "noteAdded", "completed", "reopened", "scheduled", "reminderAdded", "reminderFired", "reminderDismissed", "deleted",
                                        "moved", "focusStarted", "focusCompleted", "groupChanged", "attachmentAdded", "attachmentRemoved", "labelChanged", "archived", "timeLogged"]
    private static let actorKinds: [ActorKind] = [.user, .agent, .browser, .teams, .system, .vault, .connector]

    /// One activity line as C# writes it (`{"at","itemId","kind","summary","actorKind","actor"}`).
    static func activityLine(_ a: ActivityEntry) -> String {
        var o = JSONObject([("at", .instant(Instant(a.at))), ("itemId", .string(guidD(a.itemId))), ("kind", .string(a.kind)),
                            ("summary", .string(a.summary)), ("actorKind", .string(a.actor.kind.rawValue))])
        if let name = a.actor.name { o["actor"] = .string(name) }
        return JSONText.write(.object(o))
    }

    /// Reads only what was appended since the last read; a shorter file means it was replaced.
    private func loadActivity(_ b: TaskBoard) {
        let path = metaPath("activity.jsonl")
        let length = fileLength(path)
        if length < activityLength {
            activity = []
            activityLength = 0
        }
        if length > activityLength, let handle = FileHandle(forReadingAtPath: path) {
            defer { try? handle.close() }
            try? handle.seek(toOffset: UInt64(activityLength))
            let data = (try? handle.readToEnd()) ?? Data()
            if let complete = data.lastIndex(of: 0x0A) {
                let whole = data[data.startIndex...complete]
                for line in String(decoding: whole, as: UTF8.self).oSplit("\n") where !line.isEmpty {
                    if let entry = Self.parseActivity(line) { activity.append(entry) }
                }
                activityLength += Int64(whole.count)
            }
        }
        for entry in activity { b.appendLoaded(activity: entry) }
    }

    static func parseActivity(_ line: String) -> ActivityEntry? {
        guard let o = (try? JSONText.parse(line))?.objectValue,
              let at = o.get("at")?.stringValue.flatMap({ Instant(parsing: $0) }),
              let itemId = parseGuid(o.get("itemId")?.stringValue),
              let kindText = o.get("kind")?.stringValue, let kind = activityKinds.first(where: { Ordinal.equalIgnoreCase($0, kindText) }),
              let summary = o.get("summary")?.stringValue,
              let actorText = o.get("actorKind")?.stringValue, let actorKind = actorKinds.first(where: { Ordinal.equalIgnoreCase($0.rawValue, actorText) }) else { return nil }
        return ActivityEntry(at: at.date, itemId: itemId, kind: kind, summary: summary, actor: Actor(kind: actorKind, name: o.get("actor")?.stringValue))
    }

    private func appendActivity(_ undo: inout [() throws -> Void]) throws -> Bool {
        let pending = board.activity.dropFirst(activity.count)
        if pending.isEmpty { return false }
        let path = metaPath("activity.jsonl")
        try VaultFiles.makeDirectories(metaPath(""))
        let length = fileLength(path)
        var text = ""
        // Never glue a new entry onto a torn last line.
        if length > 0, let last = lastByte(path), last != 0x0A { text += "\n" }
        for a in pending { text += Self.activityLine(a) + "\n" }
        try append(Data(text.utf8), to: path)
        undo.append { try self.truncate(path, to: length) }
        activity.append(contentsOf: pending)
        activityLength = fileLength(path)
        return true
    }

    // ---- Sync -------------------------------------------------------------------------

    /// Everything that syncs, read after taking in outside edits: task files (keyed by task id), their rich pages,
    /// other files in the vault's folders, the tabs/labels settings, the Now order and the activity log.
    public func readSync() throws -> SyncSnapshot {
        try withLock {
            if forceContent || diskDiffers() { _ = try reload() }
            return try buildSyncSnapshot()
        }
    }

    /// Whether the file at `rel` (or an iCloud placeholder of it, `.<name>.icloud`) is there: such a file isn't deleted,
    /// whatever its content (never say it was deleted).
    public func isPresent(_ rel: String) -> Bool {
        let path = full(rel)
        var st = stat()
        return lstat(path, &st) == 0 || lstat(full(Self.placeholder(rel)), &st) == 0
    }

    static func placeholder(_ rel: String) -> String {
        let dir = VaultFiles.directory(rel)
        return (dir.isEmpty ? "" : dir + "/") + "." + VaultFiles.fileName(rel) + ".icloud"
    }

    /// Everything that syncs. A file that is there but can't be read (or an iCloud placeholder) stops the sync: left
    /// out, it would read as deleted here and be deleted on every other device.
    private func buildSyncSnapshot() throws -> SyncSnapshot {
        // A task file that can't be understood has no task id to be listed under: left out (or listed as a plain
        // file), the other devices would take its task as deleted and trash their copy. Sync waits until it's fixed.
        if let broken = caches.files.sorted(by: { Ordinal.less($0.key.value, $1.key.value) }).first(where: { $0.value.good == nil }) {
            throw VaultRefusal(message: "\"\(broken.key.value)\" can't be read (\(broken.value.error ?? "")). Sync waits until it's fixed, so your other devices keep the task.")
        }
        if let unreadable = caches.files.first(where: { $0.value.stamp == Self.unreadableStamp })?.key.value {
            throw SyncNotArrived("\"\(VaultFiles.fileName(unreadable))\" can't be read right now (it may not be downloaded yet); sync waits for it.")
        }
        if let waiting = placeholders().first {
            throw SyncNotArrived("\"\(VaultFiles.fileName(waiting))\" isn't downloaded yet; sync waits for it.")
        }
        var entries: SyncEntries = [:]
        var memory: [OKey: Data] = [:]
        var covered = Set<OIKey>()
        var seen = Set<OIKey>()
        func add(_ key: String, _ rel: String) throws {
            let path = full(rel)
            guard let attributes = try? fm.attributesOfItem(atPath: path), attributes[.type] as? FileAttributeType == .typeRegular,
                  (attributes[.size] as? Int64 ?? 0) <= SyncLimits.maxFileBytes else { return }
            let s = Stamp(time: attributes[.modificationDate] as? Date, size: attributes[.size] as? Int64 ?? 0)
            var known = syncHashes[OIKey(rel)]
            if known == nil || known!.stamp != s {
                let read: Data?
                do { read = try readData(path) } catch { throw SyncNotArrived(error.localizedDescription) }
                guard let data = read else { return }
                known = (s, SyncHash.of(data))
                syncHashes[OIKey(rel)] = known
            }
            entries[OKey(key)] = SyncEntry(key: key, path: rel, hash: known!.hash)
            covered.insert(OIKey(rel))
            seen.insert(OIKey(rel))
        }
        for (id, rel) in caches.paths.sorted(by: { Ordinal.less($0.value, $1.value) }) {
            try add(SyncKeys.task(id), rel)
            try add(SyncKeys.rich(id), VaultFiles.changeExtension(rel, ".html"))
        }
        for rel in syncableFiles() where !covered.contains(OIKey(rel)) { try add(SyncKeys.file(rel), rel) }

        func remember(_ key: String, _ path: String, _ bytes: Data) {
            entries[OKey(key)] = SyncEntry(key: key, path: path, hash: SyncHash.of(bytes))
            memory[OKey(key)] = bytes
        }
        if let config = try readData(metaPath("config.json")) { remember(SyncKeys.config, SyncKeys.configPath, config) }
        remember(SyncKeys.order, SyncKeys.orderPath, Self.orderBytes(board.nowOrder))
        if let log = try readData(metaPath("activity.jsonl")) { remember(SyncKeys.activity, SyncKeys.activityPath, log) }
        for key in syncHashes.keys where !seen.contains(key) { syncHashes[key] = nil }

        return SyncSnapshot(entries: entries) { [weak self] e in
            if let bytes = memory[OKey(e.key)] { return bytes }
            guard let self else { throw SyncStaleError("The tasks folder was closed.") }
            return try self.withLock {
                let content = self.fm.contents(atPath: self.full(e.path)) ?? Data()
                if SyncHash.of(content) == e.hash { return content }
                // Changed without a new time or size (rare): hash it again next time.
                self.syncHashes[OIKey(e.path)] = nil
                throw SyncStaleError("\(e.path) changed.")
            }
        }
    }

    /// The Now order as it syncs: the compact JSON array of ids.
    static func orderBytes(_ ids: [UUID]) -> Data { Data(JSONText.write(.array(ids.map { .string(guidD($0)) })).utf8) }

    private static func parseOrder(_ content: Data) -> [UUID] {
        guard let array = (try? JSONText.parse(content))?.arrayValue else { return [] }
        return array.compactMap { parseGuid($0.stringValue) }.filter { $0 != emptyGuid }
    }

    /// iCloud placeholders (`.<name>.icloud`: a file not downloaded yet) in the vault's folders.
    private func placeholders() -> [String] {
        var result: [String] = []
        var pending: [String] = plainEntries("").filter { isPlainDirectory($0) }
        while let dir = pending.popLast() {
            for name in (try? fm.contentsOfDirectory(atPath: full(dir))) ?? [] {
                let rel = dir + "/" + name
                if name.oStarts(".") && name.oEnds(".icloud") && name.u16 > 8 {
                    result.append(dir + "/" + name.sub(1, name.u16 - 7))
                } else if !name.oStarts(".") && isPlainDirectory(rel) {
                    pending.append(rel)
                }
            }
        }
        return result
    }

    /// Files in the vault's folders (never root-level files, hidden ones, or links that lead elsewhere).
    private func syncableFiles() -> [String] {
        var result: [String] = []
        var pending: [String] = plainEntries("").filter { isPlainDirectory($0) }
        while let dir = pending.popLast() {
            for name in plainEntries(dir) {
                if isPlainDirectory(name) {
                    pending.append(name)
                } else if isRegularFile(name), VaultFiles.isSyncablePath(name) {
                    result.append(name)
                }
            }
        }
        return result
    }

    /// Applies a sync plan. Every file it touches must still be what the plan was worked out from (else
    /// `SyncStaleError` and nothing is changed); deleted files go to the trash. A failure part way undoes it all.
    public func applySync(_ ops: [SyncOp]) throws {
        try withLock {
            if forceContent || diskDiffers() { _ = try reload() }
            try verify(ops)
            var order: [UUID]?
            var undo: [() throws -> Void] = []
            do {
                for op in ops {
                    switch op.kind {
                    case .appendActivity:
                        let path = metaPath("activity.jsonl")
                        let length = fileLength(path)
                        try VaultFiles.makeDirectories(metaPath(""))
                        var data = Data()
                        if length > 0, let last = lastByte(path), last != 0x0A { data.append(0x0A) }
                        data.append(op.content!)
                        try append(data, to: path)
                        undo.append { try self.truncate(path, to: length) }
                    case .write:
                        let path = full(op.path)
                        let before = try readData(path)
                        try VaultFiles.writeAtomic(path, op.content!, exclusive: op.expected == nil)
                        undo.append {
                            if let before { try VaultFiles.writeAtomic(path, before) } else { try self.fm.removeItem(atPath: path) }
                        }
                    case .move:
                        let source = full(op.path), target = full(op.to!)
                        try VaultFiles.makeDirectories((target as NSString).deletingLastPathComponent)
                        try VaultFiles.move(source, target)
                        undo.append { try VaultFiles.move(target, source) }
                    case .delete:
                        let source = full(op.path)
                        let trash = try trashPath(VaultFiles.fileName(op.path))
                        try VaultFiles.move(source, trash)
                        undo.append { try VaultFiles.move(trash, source) }
                    case .setConfig:
                        let path = metaPath("config.json")
                        let previous = try readData(path)
                        try VaultFiles.writeAtomic(path, op.content!)
                        undo.append { if let previous { try VaultFiles.writeAtomic(path, previous) } else { try self.fm.removeItem(atPath: path) } }
                    case .setOrder:
                        order = Self.parseOrder(op.content!)
                    }
                }
            } catch {
                for step in undo.reversed() { try? step() }
                markDirty()
                _ = try? reload()
                throw error
            }
            markDirty()
            _ = try reload()
            if let order {
                var seen = Set<UUID>()
                board.nowOrder = order.filter { board.find($0) != nil && seen.insert($0).inserted }
                _ = try writeMetadataIfChanged()
            }
        }
    }

    /// Checks the plan against the files as they are now (moves and writes in order); nothing is written yet.
    private func verify(_ ops: [SyncOp]) throws {
        var known: [OIKey: String?] = [:]
        func current(_ rel: String) throws -> String? {
            if let hash = known[OIKey(rel)] { return hash }
            let hash = try readData(full(rel)).map(SyncHash.of)
            known[OIKey(rel)] = .some(hash)
            return hash
        }
        func expect(_ rel: String, _ expected: String?) throws {
            if try current(rel) != expected { throw SyncStaleError("\(rel) changed while syncing.") }
        }
        // APFS treats names that differ only in Unicode normalization (Café NFC / NFD) or letter case as one file: two
        // such names in one plan would write over each other.
        var claimed: [String: String] = [:]
        func claim(_ rel: String) throws {
            let key = VaultFiles.fileSystemKey(rel)
            if let other = claimed[key], !other.oEq(rel) {
                throw SyncDataError("\"\(rel)\" and \"\(other)\" would be the same file here; one of them needs another name.")
            }
            claimed[key] = rel
        }
        func release(_ rel: String) {
            let key = VaultFiles.fileSystemKey(rel)
            if claimed[key]?.oEq(rel) == true { claimed[key] = nil }
        }
        for op in ops {
            if [.write, .move, .delete].contains(op.kind) {
                guard VaultFiles.isSyncablePath(op.path) else { throw SyncDataError("Sync refused an unsafe path: \(op.path)") }
                try ensureInsideVault(op.path)
                if let to = op.to, VaultFiles.isSyncablePath(to) { try ensureInsideVault(to) }
            }
            switch op.kind {
            case .write:
                try expect(op.path, op.expected)
                try claim(op.path)
                known[OIKey(op.path)] = .some(SyncHash.of(op.content!))
            case .move:
                guard let to = op.to, VaultFiles.isSyncablePath(to) else { throw SyncDataError("Sync refused an unsafe path: \(op.to ?? "")") }
                try expect(op.path, op.expected)
                if !Ordinal.equalIgnoreCase(to, op.path) { try expect(to, nil) }
                release(op.path)
                try claim(to)
                known[OIKey(to)] = .some(op.expected)
                known[OIKey(op.path)] = .some(nil)
            case .delete:
                try expect(op.path, op.expected)
                release(op.path)
                known[OIKey(op.path)] = .some(nil)
            case .setConfig:
                if try readData(metaPath("config.json")).map(SyncHash.of) != op.expected { throw SyncStaleError("The settings changed while syncing.") }
            case .setOrder:
                if let expected = op.expected, SyncHash.of(Self.orderBytes(board.nowOrder)) != expected { throw SyncStaleError("The Now order changed while syncing.") }
            case .appendActivity:
                break
            }
        }
    }

    /// Writing through a link could reach outside the vault: refused.
    private func ensureInsideVault(_ rel: String) throws {
        var parts = rel.oSplit("/")
        while !parts.isEmpty {
            let path = full(parts.joined(separator: "/"))
            if let type = (try? fm.attributesOfItem(atPath: path))?[.type] as? FileAttributeType, type == .typeSymbolicLink {
                throw SyncDataError("Sync refused to write through a link: \(rel)")
            }
            parts.removeLast()
        }
    }

    // ---- File system helpers ------------------------------------------------------------

    private func withLock<T>(_ body: () throws -> T) rethrows -> T {
        lock.lock()
        defer { lock.unlock() }
        return try body()
    }

    func full(_ rel: String) -> String { rel.isEmpty ? root.path : root.path + "/" + rel }

    private func metaPath(_ name: String) -> String { full(VaultFiles.metaFolder + (name.isEmpty ? "" : "/" + name)) }

    private func dirExists(_ path: String) -> Bool {
        var isDir: ObjCBool = false
        return fm.fileExists(atPath: path, isDirectory: &isDir) && isDir.boolValue
    }

    /// Names in a vault folder (relative paths), without hidden ones or links.
    private func plainEntries(_ rel: String) -> [String] {
        guard let names = try? fm.contentsOfDirectory(atPath: full(rel)) else { return [] }
        return names.filter { !$0.oStarts(".") }.map { rel.isEmpty ? $0 : rel + "/" + $0 }
            .filter { ((try? fm.attributesOfItem(atPath: full($0)))?[.type] as? FileAttributeType) != .typeSymbolicLink }
    }

    private func isPlainDirectory(_ rel: String) -> Bool { ((try? fm.attributesOfItem(atPath: full(rel)))?[.type] as? FileAttributeType) == .typeDirectory }

    private func isRegularFile(_ rel: String) -> Bool { ((try? fm.attributesOfItem(atPath: full(rel)))?[.type] as? FileAttributeType) == .typeRegular }

    private static func isGroupFolder(_ name: String) -> Bool { !name.isEmpty && !name.oStarts(".") && !name.oStarts("_") }

    private func topFolders() -> [String] {
        ((try? fm.contentsOfDirectory(atPath: root.path)) ?? []).filter { Self.isGroupFolder($0) && dirExists(full($0)) }
    }

    private func enumerateTaskFiles() -> [String] { topFolders().flatMap { enumerateTaskFiles(in: $0) } }

    private func enumerateTaskFiles(in folder: String) -> [String] {
        guard let names = try? fm.contentsOfDirectory(atPath: full(folder)) else { return [] }
        var files: [String] = []
        var dirs: [String] = []
        for name in names {
            let rel = folder + "/" + name
            if dirExists(full(rel)) {
                if Self.isGroupFolder(name) { dirs.append(rel) }
            } else if name.oEndsIgnoreCase(".md") && !name.oStarts(".") {
                files.append(rel)
            }
        }
        return files + dirs.flatMap { enumerateTaskFiles(in: $0) }
    }

    private func stamp(_ rel: String) -> Stamp {
        guard let attributes = try? fm.attributesOfItem(atPath: full(rel)) else { return Stamp(time: nil, size: -1) }
        return Stamp(time: attributes[.modificationDate] as? Date, size: attributes[.size] as? Int64 ?? 0)
    }

    private func fileLength(_ path: String) -> Int64 { ((try? fm.attributesOfItem(atPath: path))?[.size] as? Int64) ?? 0 }

    /// A file that is there but can't be read (not downloaded yet, no permission, a disk error). It is never written over.
    public struct VaultUnreadable: Error, LocalizedError {
        public let path: String
        public var errorDescription: String? { "\"\((path as NSString).lastPathComponent)\" can't be read right now (it may not be downloaded yet). Sync tries again later." }
    }

    /// The file's bytes; nil when there is no file. A file that is there but can't be read throws.
    private func readData(_ path: String) throws -> Data? {
        if let data = fm.contents(atPath: path) { return data }
        var st = stat()
        if lstat(path, &st) != 0 && errno == ENOENT { return nil }
        throw VaultUnreadable(path: path)
    }

    /// The file's text as .NET's `File.ReadAllText` gives it (UTF-8, a leading BOM dropped); nil when there is none.
    private func readText(_ path: String) throws -> String? {
        guard var data = try readData(path) else { return nil }
        if data.starts(with: [0xEF, 0xBB, 0xBF]) { data.removeFirst(3) }
        return String(decoding: data, as: UTF8.self)
    }

    private func readOrEmpty(_ path: String) throws -> String { try readText(path) ?? "" }

    private func lastByte(_ path: String) -> UInt8? {
        guard let handle = FileHandle(forReadingAtPath: path) else { return nil }
        defer { try? handle.close() }
        guard let end = try? handle.seekToEnd(), end > 0 else { return nil }
        try? handle.seek(toOffset: end - 1)
        return (try? handle.read(upToCount: 1))?.first
    }

    private func append(_ data: Data, to path: String) throws {
        if !fm.fileExists(atPath: path) { try VaultFiles.writeFile(path, Data()) }
        let handle = try FileHandle(forWritingTo: URL(fileURLWithPath: path))
        defer { try? handle.close() }
        try handle.seekToEnd()
        try handle.write(contentsOf: data)
    }

    private func truncate(_ path: String, to length: Int64) throws {
        let handle = try FileHandle(forWritingTo: URL(fileURLWithPath: path))
        defer { try? handle.close() }
        try handle.truncate(atOffset: UInt64(length))
    }
}

extension TaskBoard {
    /// Tasks waiting for one that's done come back, with a reminder saying why (C#'s `ReleaseWaiters` after a load).
    /// Returns whether any did.
    @discardableResult
    func releaseFinishedWaits(now: Date, skipRoots: Set<UUID> = []) -> Bool {
        var released = false
        for waiter in allItems {
            guard let after = waiter.afterId, !skipRoots.contains(waiter.root.id), let other = find(after), other.isDone else { continue }
            let why = "\"\(other.title)\" is done"
            waiter.afterId = nil
            released = true
            if !waiter.isDone {
                waiter.reminders.append(Reminder(dueAt: now, message: "\(why): back to \"\(waiter.title)\"", kind: .nextAction))
                log(now, waiter.id, "scheduled", "Back: \(why)", .system)
            }
        }
        return released
    }
}
