import Foundation

/// What the planner may ask beyond the two sides.
public struct SyncPlanOptions {
    /// Whether some device recorded deleting exactly this version (so it isn't brought back).
    public var deleted: (SyncEntry) -> Bool = { _ in false }
    /// Whether merged text is still a valid task file (else both versions are kept whole).
    public var isValidTask: (String, String) -> Bool = { _, _ in true }

    public init(deleted: @escaping (SyncEntry) -> Bool = { _ in false }, isValidTask: @escaping (String, String) -> Bool = { _, _ in true }) {
        self.deleted = deleted
        self.isValidTask = isValidTask
    }
}

/// Works out how to bring a peer's changes into this vault: a three-way merge per entry against what both devices last
/// agreed on. Whenever a choice has to be made, the device whose id sorts first (ordinal) wins, so both devices make the
/// same choice and end up with the same files. Port of C#'s `SyncPlanner`, choice for choice.
public enum SyncPlanner {
    public static func plan(local: SyncSide, peer: SyncSide, base: SyncEntries, readBase: @escaping (String) -> Data?, options: SyncPlanOptions = SyncPlanOptions()) throws -> SyncPlan {
        let ctx = Context(local: local, peer: peer, base: base, readBase: readBase, options: options)
        var files: [(key: String, l: SyncEntry?, p: SyncEntry, b: SyncEntry?)] = []
        let keys = Set(local.entries.keys).union(peer.entries.keys).union(base.keys).sorted()
        for okey in keys {
            let key = okey.value
            let l = local.entries[okey], p = peer.entries[okey], b = base[okey]
            let kind = SyncKeys.kind(of: key)
            let isFile = kind == .task || kind == .rich || kind == .file
            guard let p, !(l.map { $0.samePlaceAndContent(p) } ?? false) else {
                // The peer doesn't have it (or has exactly ours): a peer delete of something unchanged here matters, and
                // so does a delete some device recorded of exactly this version.
                if p == nil, let l, isFile, (b.map { l.samePlaceAndContent($0) } ?? false) || options.deleted(l) {
                    ctx.ops.append(SyncOp(.delete, l.path, l.hash))
                }
                continue
            }
            switch kind {
            case .activity: try mergeActivity(ctx, l, p)
            case .order: try mergeJSON(ctx, .setOrder, l, p, b, indented: false)
            case .config: try mergeJSON(ctx, .setConfig, l, p, b, indented: true)
            default: files.append((key, l, p, b))
            }
        }

        // Paths that are deleted in this plan are free for new files (a file renamed only in letter case, say).
        for delete in ctx.ops where delete.kind == .delete { ctx.taken.remove(OIKey(delete.path)) }

        // Where things live first (so moves never land on another file), then what they contain.
        let paths = planMoves(ctx, files.compactMap { f in f.l.map { (f.key, $0, f.p, f.b) } })
        for f in files { try mergeFile(ctx, f.key, f.l, f.p, f.b, f.l == nil ? nil : paths[OKey(f.key)]) }

        // Moves, then deletes (to the trash, freeing their paths), then writes; the activity log last.
        var ordered: [SyncOp] = ctx.moves
        ordered += ctx.ops.filter { $0.kind == .delete }
        ordered += ctx.ops.filter { $0.kind == .write }
        ordered += ctx.ops.filter { $0.kind == .setConfig || $0.kind == .setOrder }
        ordered += ctx.ops.filter { $0.kind == .appendActivity }
        return SyncPlan(ops: ordered, conflicts: ctx.conflicts)
    }

    /// What both devices now agree on (same content, same place); everything else keeps its old base.
    public static func nextBase(local: SyncEntries, peer: SyncEntries, oldBase: SyncEntries) -> SyncEntries {
        var next: SyncEntries = [:]
        for key in Set(local.keys).union(peer.keys).union(oldBase.keys) {
            let l = local[key], p = peer[key]
            if let l, let p, l.samePlaceAndContent(p) {
                next[key] = p
            } else if l != nil || p != nil, let old = oldBase[key] {
                next[key] = old
            }
        }
        return next
    }

    private final class Context {
        let local: SyncSide
        let peer: SyncSide
        let base: SyncEntries
        let readBase: (String) -> Data?
        let options: SyncPlanOptions
        var ops: [SyncOp] = []
        var moves: [SyncOp] = []
        var conflicts: [SyncConflict] = []
        /// Paths in use here (and planned), to never write over another entry's file.
        var taken: Set<OIKey>
        let localWins: Bool
        private let localByKey: [SyncEntry]

        init(local: SyncSide, peer: SyncSide, base: SyncEntries, readBase: @escaping (String) -> Data?, options: SyncPlanOptions) {
            self.local = local
            self.peer = peer
            self.base = base
            self.readBase = readBase
            self.options = options
            taken = Set(local.entries.values.map { OIKey($0.path) })
            localWins = Ordinal.compare(local.device, peer.device) < 0
            localByKey = local.entries.sorted { $0.key < $1.key }.map(\.value)
        }

        var loserName: String { localWins ? peer.name : local.name }

        func localAt(_ path: String) -> SyncEntry? { localByKey.first { Ordinal.equalIgnoreCase($0.path, path) } }

        func freePath(_ path: String) -> String {
            let dir = path.oLastIndex("/").map { path.sub(0, $0 + 1) } ?? ""
            let ext = VaultFiles.extensionOf(path)
            let stem = path.sub(dir.u16, path.u16 - ext.u16)
            var candidate = path
            var n = 2
            while taken.contains(OIKey(candidate)) {
                candidate = "\(dir)\(stem) \(n)\(ext)"
                n += 1
            }
            taken.insert(OIKey(candidate))
            return candidate
        }

        func take(_ path: String) -> String {
            taken.insert(OIKey(path))
            return path
        }
    }

    /// Decides where each file both sides have lives: a move on one side wins, moves on both go to the winner's choice.
    /// A move onto a file that stays put is left for later; files that trade places go through a temporary name.
    private static func planMoves(_ ctx: Context, _ both: [(key: String, l: SyncEntry, p: SyncEntry, b: SyncEntry?)]) -> [OKey: String] {
        var paths: [OKey: String] = [:]
        var wanted: [(key: String, l: SyncEntry, target: String)] = []
        for (key, l, p, b) in both {
            paths[OKey(key)] = l.path
            if !l.path.oEq(p.path) {
                let target: String
                if let b, p.path.oEq(b.path) { target = l.path } else if let b, l.path.oEq(b.path) { target = p.path } else { target = ctx.localWins ? l.path : p.path }
                if !target.oEq(l.path) { wanted.append((key, l, target)) }
            }
        }

        // A move needs its target free, or freed by a move that also happens; drop blocked moves until none are.
        var active = wanted
        while true {
            let leavingNow = Set(active.map { OIKey($0.l.path) })
            var claimedNow = Set<OIKey>()
            let keep = active.filter { w in
                (Ordinal.equalIgnoreCase(w.target, w.l.path) || ctx.localAt(w.target) == nil || leavingNow.contains(OIKey(w.target))) && claimedNow.insert(OIKey(w.target)).inserted
            }
            if keep.count == active.count { break }
            active = keep
        }

        var moves: [(l: SyncEntry, target: String, swap: Bool)] = []
        for (key, l, target) in active {
            let caseOnly = Ordinal.equalIgnoreCase(target, l.path)
            paths[OKey(key)] = target
            ctx.taken.insert(OIKey(target))
            moves.append((l, target, !caseOnly && ctx.localAt(target) != nil))
        }

        func aside(_ l: SyncEntry) -> String {
            let ext = VaultFiles.extensionOf(l.path)
            return "\(l.path.sub(0, l.path.u16 - ext.u16)) (moving \(l.hash.prefix(8)))\(ext)"
        }

        // Files that trade places step aside first.
        for m in moves { ctx.moves.append(SyncOp(.move, m.l.path, m.l.hash, to: m.swap ? aside(m.l) : m.target)) }
        for m in moves where m.swap { ctx.moves.append(SyncOp(.move, aside(m.l), m.l.hash, to: m.target)) }
        return paths
    }

    private static func mergeFile(_ ctx: Context, _ key: String, _ l: SyncEntry?, _ p: SyncEntry, _ b: SyncEntry?, _ path2: String?) throws {
        guard let l else {
            // Deleted here and untouched there: stays deleted; so does a version some device deleted. Otherwise it's new
            // here (or was changed there: edits win).
            if (b.map { p.samePlaceAndContent($0) } ?? false) || (b == nil && ctx.options.deleted(p)) { return }
            let path = ctx.taken.contains(OIKey(p.path)) ? ctx.freePath(p.path) : ctx.take(p.path)
            ctx.ops.append(SyncOp(.write, path, nil, content: try ctx.peer.read(p)))
            return
        }

        var current = l
        if !l.path.oEq(path2!) { current.path = path2! }
        if l.hash == p.hash || (b.map { p.hash == $0.hash } ?? false) { return }
        if let b, l.hash == b.hash {
            ctx.ops.append(SyncOp(.write, current.path, l.hash, content: try ctx.peer.read(p)))
            return
        }

        let mine = try ctx.local.read(l)
        let theirs = try ctx.peer.read(p)
        let (winner, loser) = ctx.localWins ? (mine, theirs) : (theirs, mine)
        let kind = SyncKeys.kind(of: key)
        let baseBytes = b.flatMap { ctx.readBase($0.hash) }
        if kind == .task || kind == .rich, let baseBytes {
            let merged = TextMerge.merge(base: utf8(baseBytes), first: utf8(winner), second: utf8(loser), lineKeys: kind == .task ? taskLineKeys : nil)

            // A merge that isn't a valid task any more isn't written: both versions are kept whole instead.
            if kind != .task || ctx.options.isValidTask(current.path, merged.text) {
                let text = Data(merged.text.utf8)
                if merged.conflicted {
                    ctx.conflicts.append(SyncConflict(key: key, path: current.path, peer: ctx.peer.device, peerName: ctx.peer.name, mine: l.hash, theirs: p.hash, base: b?.hash, result: SyncHash.of(text)))
                }
                if SyncHash.of(text) != l.hash { ctx.ops.append(SyncOp(.write, current.path, l.hash, content: text)) }
                return
            }
            ctx.conflicts.append(SyncConflict(key: key, path: current.path, peer: ctx.peer.device, peerName: ctx.peer.name, mine: l.hash, theirs: p.hash, base: b?.hash, result: SyncHash.of(winner)))
        }

        // Nothing to merge against (or not text, or no valid merge): the winner keeps the file, the other version is kept
        // next to it, the same on both devices.
        let copyPath = self.copyPath(current.path, ctx.loserName)
        let copy = kind == .task ? Data(markTitle(utf8(loser), ctx.loserName).utf8) : loser
        let copyHash = SyncHash.of(copy)
        let existing = ctx.localAt(copyPath)
        let planned = ctx.ops.contains { $0.kind == .write && Ordinal.equalIgnoreCase($0.path, copyPath) && SyncHash.of($0.content!) == copyHash }
        if !planned && (existing == nil || existing!.hash != copyHash) {
            let path = existing == nil && !ctx.taken.contains(OIKey(copyPath)) ? ctx.take(copyPath) : ctx.freePath(copyPath)
            ctx.ops.append(SyncOp(.write, path, nil, content: copy))
        }
        if !ctx.localWins { ctx.ops.append(SyncOp(.write, current.path, l.hash, content: winner)) }
    }

    /// `Encoding.UTF8.GetString`.
    static func utf8(_ data: Data) -> String { String(decoding: data, as: UTF8.self) }

    private static func copyPath(_ path: String, _ device: String) -> String {
        // The name comes from another device: only characters every file system takes.
        let allowed: Set<UInt16> = Set(" -_'()".utf16)
        let units = device.utf16.filter { Ordinal.isLetterOrDigit($0) || allowed.contains($0) }
        let safe = String(decoding: units, as: UTF16.self).oTrim()
        let ext = VaultFiles.extensionOf(path)
        let name = safe.isEmpty ? "another device" : safe.sub(0, min(40, safe.u16)).oTrim()
        return "\(path.sub(0, path.u16 - ext.u16)) (from \(name))\(ext)"
    }

    private static let heading = Rx(#"^# (.*?)\r?$"#, multiline: true)

    /// "# Title" becomes "# Title (from Device)" so the copy is told apart in every list.
    private static func markTitle(_ text: String, _ device: String) -> String {
        guard let m = heading.match(text) else { return text }
        let r = m.range(1)
        return text.oInsert(" (from \(device))", at: r.location + r.length)
    }

    private static let blockId = Rx(#"\s\^([A-Za-z0-9-]+)(?:\s+%%.*%%)?\s*$"#)
    private static let hiddenId = Rx(#"^[-*+] .*%%\{"id":"([0-9a-fA-F-]{36})""#)
    private static let frontmatterKey = Rx(#"^([A-Za-z][\w-]*):(?:\s|$)"#)

    /// A task file's line identities: frontmatter keys inside the frontmatter, steps' block ids (`^x7k2m9`) below,
    /// and the ids of time and attachment lines.
    public static func taskLineKeys(_ lines: [String]) -> [String?] {
        var keys = [String?](repeating: nil, count: lines.count)
        var inFrontmatter = !lines.isEmpty && lines[0].oTrimEnd("\r\n") == "---"
        for (i, line) in lines.enumerated() {
            let text = line.oTrimEnd("\r\n")
            if inFrontmatter && i > 0 && text == "---" {
                inFrontmatter = false
                continue
            }
            if inFrontmatter && i > 0 {
                keys[i] = frontmatterKey.match(text).map { $0.group(1) + ":" }
            } else if !inFrontmatter, let block = blockId.match(text) {
                keys[i] = "^" + block.group(1)
            } else if !inFrontmatter, let hidden = hiddenId.match(text) {
                // Time and attachment lines carry their id: two devices adding different ones never collide.
                keys[i] = "id:" + hidden.group(1)
            }
        }
        return keys
    }

    private static func mergeActivity(_ ctx: Context, _ l: SyncEntry?, _ p: SyncEntry) throws {
        func lines(_ data: Data) -> [String] { utf8(data).oSplit("\n").map { $0.oTrimEnd("\r") }.filter { !$0.isEmpty } }
        var known = Set(try l.map { lines(try ctx.local.read($0)) }?.map(OKey.init) ?? [])
        let added = try lines(ctx.peer.read(p)).filter { known.insert(OKey($0)).inserted }
        if !added.isEmpty {
            ctx.ops.append(SyncOp(.appendActivity, SyncKeys.activityPath, nil, content: Data(added.map { $0 + "\n" }.joined().utf8)))
        }
    }

    private static func mergeJSON(_ ctx: Context, _ kind: SyncOpKind, _ l: SyncEntry?, _ p: SyncEntry, _ b: SyncEntry?, indented: Bool) throws {
        let content: Data
        if l == nil || (b.map { l!.hash == $0.hash } ?? false) {
            content = try ctx.peer.read(p)
        } else if let b, p.hash == b.hash {
            return
        } else {
            guard let mine = try? JSONText.parse(ctx.local.read(l!)), let theirs = try? JSONText.parse(ctx.peer.read(p)) else { return }
            let old = b.flatMap { ctx.readBase($0.hash) }.flatMap { try? JSONText.parse($0) }
            var merged = try ctx.localWins ? mergeNode(old, mine, theirs) : mergeNode(old, theirs, mine)
            if kind == .setConfig { merged = oneTabPerFolder(merged) }
            let text = merged.map { JSONText.write($0, indented: indented) } ?? "null"
            content = Data((indented ? text + "\n" : text).utf8)
        }
        if l == nil || SyncHash.of(content) != l!.hash {
            ctx.ops.append(SyncOp(kind, kind == .setOrder ? SyncKeys.orderPath : SyncKeys.configPath, l?.hash, content: content))
        }
    }

    /// Three-way merge of JSON: objects by property, lists of objects by "id" (or "name"), plain lists as ordered sets.
    static func mergeNode(_ base: JSONValue?, _ winner: JSONValue?, _ loser: JSONValue?) throws -> JSONValue? {
        let b = base.flatMap { $0.isNull ? nil : $0 }, w = winner.flatMap { $0.isNull ? nil : $0 }, l = loser.flatMap { $0.isNull ? nil : $0 }
        if JSONValue.deepEquals(w, l) || JSONValue.deepEquals(b, l) { return w }
        if JSONValue.deepEquals(b, w) { return l }

        if case .object(let wo)? = w, case .object(let lo)? = l {
            let bo = b?.objectValue
            var result = JSONObject()
            var names: [String] = []
            for name in wo.keys + lo.keys where !names.contains(where: { $0.oEq(name) }) { names.append(name) }
            for name in names {
                let bv = bo?[name]
                let inBase = bo?.contains(name) == true
                if wo.contains(name) && lo.contains(name) {
                    result.pairs.append((name, try mergeNode(bv, wo[name], lo[name]) ?? .null))
                } else if wo.contains(name) {
                    // Removed on one side: gone unless the other side changed it since.
                    if !inBase || !JSONValue.deepEquals(bv, wo[name]) { result.pairs.append((name, wo[name]!)) }
                } else if !inBase || !JSONValue.deepEquals(bv, lo[name]) {
                    result.pairs.append((name, lo[name]!))
                }
            }
            return .object(result)
        }

        if case .array(let wa)? = w, case .array(let la)? = l {
            let keyName = ["id", "name"].first { k in (wa + la).allSatisfy { isPrimitive($0.objectValue?[k]) } }
            if let keyName { return .array(try mergeKeyed(b?.arrayValue, wa, la, keyName)) }
            return .array(mergeList(b?.arrayValue, wa, la))
        }
        return w
    }

    private static func isPrimitive(_ value: JSONValue?) -> Bool {
        switch value {
        case .string?, .verbatim?, .number?, .bool?: return true
        default: return false
        }
    }

    /// Two devices that each made a "Work" tab have one "Work" folder: the first tab for a folder stays.
    private static func oneTabPerFolder(_ config: JSONValue?) -> JSONValue? {
        guard case .object(var o)? = config, let groups = o["groups"]?.arrayValue else { return config }
        var seen = Set<OIKey>()
        o["groups"] = .array(groups.filter { g in
            guard let folder = g.objectValue?["folder"]?.stringValue else { return true }
            return seen.insert(OIKey(folder)).inserted
        })
        return .object(o)
    }

    private static func mergeList(_ base: [JSONValue]?, _ winner: [JSONValue], _ loser: [JSONValue]) -> [JSONValue] {
        func has(_ list: [JSONValue], _ x: JSONValue) -> Bool { list.contains { JSONValue.deepEquals($0, x) } }
        let removed = (base ?? []).filter { !has(winner, $0) || !has(loser, $0) }
        var result: [JSONValue] = []
        for item in winner + loser.filter({ !has(winner, $0) }) where !has(removed, item) && !has(result, item) { result.append(item) }
        return result
    }

    private static func mergeKeyed(_ base: [JSONValue]?, _ winner: [JSONValue], _ loser: [JSONValue], _ keyName: String) throws -> [JSONValue] {
        func key(_ node: JSONValue) -> String { JSONText.write(node.objectValue![keyName]!) }
        func dictionary(_ list: [JSONValue]) throws -> [(String, JSONValue)] {
            var result: [(String, JSONValue)] = []
            for item in list {
                let k = key(item)
                if result.contains(where: { $0.0.oEq(k) }) { throw SyncDataError("Two entries in the settings have the same \(keyName): \(k).") }
                result.append((k, item))
            }
            return result
        }
        func find(_ list: [(String, JSONValue)], _ k: String) -> JSONValue? { list.first { $0.0.oEq(k) }?.1 }
        let old = try dictionary((base ?? []).filter { isPrimitive($0.objectValue?[keyName]) })
        let w = try dictionary(winner), l = try dictionary(loser)
        var result: [JSONValue] = []
        for k in w.map(\.0) + l.map(\.0).filter({ k in find(w, k) == nil }) {
            let bv = find(old, k)
            let merged: JSONValue?
            switch (find(w, k), find(l, k)) {
            case let (wv?, lv?): merged = try mergeNode(bv, wv, lv)
            // Removed on one side: gone unless the other side changed it since.
            case let (wv?, nil): merged = bv != nil && JSONValue.deepEquals(bv, wv) ? nil : wv
            case let (nil, lv?): merged = bv != nil && JSONValue.deepEquals(bv, lv) ? nil : lv
            default: merged = nil
            }
            if let merged { result.append(merged) }
        }
        return result
    }
}
