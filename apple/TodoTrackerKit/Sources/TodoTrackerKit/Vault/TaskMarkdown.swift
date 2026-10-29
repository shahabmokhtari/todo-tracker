import Foundation
import Yams

/// A root task file read from disk, plus everything needed to write it back without losing anything.
/// Port of C#'s `ParsedTaskFile`.
public final class ParsedTaskFile {
    /// The task tree (not attached to a board).
    public let root: WorkItem

    /// The file lacks metadata the app keeps (ids, exact times, sections). The app adds it the next time it saves this
    /// task for another reason; it never rewrites a file just because it read it.
    public internal(set) var needsWrite = false

    /// The file carries the app's task id (it was created or saved by Todo Tracker).
    public internal(set) var hasAppId = false

    public internal(set) var warnings: [String] = []

    var frontmatter = Frontmatter.empty
    /// Frontmatter key holding the app's id: `id`, or `tt-id` when the person's own `id` isn't a GUID.
    var idKey = "id"
    /// What each owned property meant when read; unchanged values are written back exactly as typed.
    var keySignatures: [OIKey: String] = [:]
    var blockIds: [UUID: String] = [:]
    var checkChars: [UUID: UInt16] = [:]
    var priorityEmoji: [UUID: String] = [:]
    /// Hidden subtask data this version doesn't know (written by a newer one): kept as it was.
    var unknownMeta: [UUID: JSONObject] = [:]
    var rawLinks: [UUID: (raw: String, path: String, name: String)] = [:]
    /// Headings or text right above a top-level subtask, in file order.
    var leading: [(id: UUID, text: String)] = []
    var subtasksTrailer: String?
    var notesPreamble: String?
    var attachmentsPreamble: String?
    var timePreamble: String?
    var timeTrailer: String?
    var timeExtras: [UUID: String] = [:]
    /// Sections after the first app section, in file order: an app section kind, or the raw text of another section.
    var layout: [(kind: String?, raw: String?)] = []
    var newline = "\n"
    var bom = false

    init(root: WorkItem) { self.root = root }

    func leadingText(_ id: UUID) -> String? { leading.first { $0.id == id }?.text }
}

/// Reads and writes one root task as Obsidian-friendly markdown (docs/vault-format.md), byte for byte like the C#
/// core's `TaskMarkdown`: anything the app doesn't own is written back as it was.
public enum TaskMarkdown {
    private static let subtasks = "subtasks", notes = "notes", attachments = "attachments", time = "time"

    private static let subtaskMetaKeys: Set<String> = ["id", "created", "done", "due", "next", "after", "seq", "delay", "labels", "tags", "reminders", "icon", "color", "repeat", "repeatUndo", "plan", "planMin", "energy"]

    static let canonicalKeys = ["status", "board", "priority", "icon", "color", "created", "completed", "archived", "due", "scheduled", "after", "sequential", "step-delay", "repeat", "repeat-undo", "planned", "planned-length", "energy", "tags", "labels", "reminders"]

    /// A frontmatter property the app writes (besides the id); everything else belongs to the person.
    static func isOwnedProperty(_ key: String) -> Bool { canonicalKeys.contains { Ordinal.equalIgnoreCase($0, key) } }

    public static func parse(_ text: String, fileStem: String, context: TaskMarkdownContext) throws -> ParsedTaskFile {
        try parseCore(text, fileStem, context)
    }

    public static func render(_ root: WorkItem, context: TaskMarkdownContext, previous: ParsedTaskFile? = nil) -> String {
        var sb = "---\n"
        renderFrontmatter(&sb, root, context.timeZone, previous)
        sb += "---\n# " + root.title + "\n"

        var blocks: [String] = []
        if let details = root.details, !details.oIsBlank {
            blocks.append(VaultText.closeFences(details.oRep("\r\n", "\n").oTrim("\n")))
        }
        var rendered = Set<String>()
        for (kind, raw) in previous?.layout ?? [] {
            if let kind {
                if rendered.insert(kind).inserted, let section = renderSection(kind, root, context, previous) { blocks.append(section) }
            } else {
                blocks.append(raw!)
            }
        }
        for kind in [subtasks, notes, time, attachments] where rendered.insert(kind).inserted {
            if let section = renderSection(kind, root, context, previous) { blocks.append(section) }
        }
        if !blocks.isEmpty { sb += "\n" + blocks.joined(separator: "\n\n") + "\n" }
        if previous?.newline == "\r\n" { sb = sb.oRep("\n", "\r\n") }
        return previous?.bom == true ? "\u{FEFF}" + sb : sb
    }

    // ---- Parsing ------------------------------------------------------------------------

    /// A list of lines shared by reference (C# passes `List<string>` around).
    final class Lines {
        var lines: [String] = []
        init(_ lines: [String] = []) { self.lines = lines }
        func add(_ line: String) { lines.append(line) }
        var count: Int { lines.count }
    }

    private static func parseCore(_ input: String, _ fileStem: String, _ ctx: TaskMarkdownContext) throws -> ParsedTaskFile {
        var text = input
        let bom = text.utf16.first == 0xFEFF
        if bom { text = text.sub(1) }
        let newline = text.oContains("\r\n") ? "\r\n" : "\n"
        text = text.oRep("\r\n", "\n")
        let (fm, body, present) = try Frontmatter.split(text)
        let tz = ctx.timeZone
        let state = ParseState(ctx)
        state.needsWrite = !present

        // The app's id: tt-id, else a GUID id. A non-GUID id belongs to the person (a Zettelkasten id) and stays.
        var idKey = "id"
        var appId: UUID?
        if let ttId = parseGuid(fm.scalar("tt-id")) {
            idKey = "tt-id"
            appId = ttId
        } else if let plainId = parseGuid(fm.scalar("id")) {
            appId = plainId
        } else if fm.has("id") {
            idKey = "tt-id"
        }
        let id = state.newId(appId, "root")

        // Sections: text before the first app section is the details; later unknown sections keep their place.
        var title: String?
        let details = Lines()
        var sections: [String: Lines] = [subtasks: Lines(), notes: Lines(), attachments: Lines(), time: Lines()]
        var layout: [(kind: String?, lines: Lines?)] = []
        var current = details
        var fence = VaultText.Fence()
        for line in body.oSplit("\n") {
            if !fence.accept(line) {
                if title == nil && line.oStarts("# ") && layout.isEmpty {
                    title = line.sub(2).oTrim()
                    continue
                }
                if line.oStarts("## ") {
                    if let kind = sectionKind(line.sub(3)) {
                        current = sections[kind]!
                        if !layout.contains(where: { $0.kind == kind }) { layout.append((kind, nil)) }
                        continue
                    }
                    if !layout.isEmpty {
                        current = Lines()
                        layout.append((nil, current))
                    }
                }
            }
            current.add(line)
        }

        let hasAppId = appId != nil
        if !layout.contains(where: { $0.kind == subtasks }) && !hasAppId, let checklist = extractChecklist(details), !checklist.isEmpty {
            // A note typed in Obsidian: its first checkbox list is the subtasks.
            sections[subtasks] = Lines(checklist)
            state.needsWrite = true
        }

        state.needsWrite = state.needsWrite || title == nil || !hasAppId
        let created = try VaultText.parseTime(fm.scalar("created"), tz, "00:00")
        state.needsWrite = state.needsWrite || created == nil
        let rootTitle = title.flatMap { $0.oIsBlank ? nil : $0 } ?? fileStem
        let root = WorkItem(id: id, title: rootTitle, priority: parsePriority(fm.scalar("priority")), createdAt: created ?? ctx.now, groupId: emptyGuid)
        root.details = joinKeep(details.lines)
        root.deadline = try VaultText.parseTime(fm.scalar("due"), tz, VaultText.defaultDueTime)
        root.nextActionAt = try VaultText.parseTime(fm.scalar("scheduled"), tz, VaultText.defaultScheduledTime)
        root.afterId = parseGuid(fm.scalar("after"))
        root.stepDelay = try VaultText.parseDuration(fm.scalar("step-delay"))
        let result = ParsedTaskFile(root: root)
        result.frontmatter = fm
        result.newline = newline
        result.bom = bom
        result.idKey = idKey
        result.hasAppId = hasAppId
        state.file = result

        let status = (fm.scalar("status") ?? "").oTrim().oLower()
        let done = ["done", "completed", "complete", "cancelled", "canceled"].contains(status)
        state.needsWrite = state.needsWrite || status.isEmpty
        let completed = try VaultText.parseTime(fm.scalar("completed"), tz, VaultText.defaultDoneTime)
        if done {
            root.completedAt = completed ?? ctx.now
            state.needsWrite = state.needsWrite || completed == nil
        } else {
            state.needsWrite = state.needsWrite || completed != nil
        }

        // Only a done task can be archived: one reopened by hand (status: open) is back in the lists.
        let archived = parseArchived(fm.scalar("archived"), root.completedAt, tz, ctx.now)
        root.archivedAt = done ? archived : nil
        state.needsWrite = state.needsWrite || (!done && archived != nil)

        root.stage = parseStage(fm.scalar("board"))
        root.icon = cleanIcon(fm.scalar("icon"))
        root.color = cleanColor(fm.scalar("color"))
        root.repeatRule = Recurrence.parseOrNil(fm.scalar("repeat"))
        root.repeatUndo = parseRepeatUndo(fm.scalar("repeat-undo"))
        root.plannedAt = try VaultText.parseTime(fm.scalar("planned"), tz, "09:00")
        root.plannedMinutes = try VaultText.parseDuration(fm.scalar("planned-length")).flatMap { readPlanMinutes(Int64($0 / 60)) }
        if let e = fm.scalar("energy"), !e.isEmpty, e.utf16.allSatisfy({ $0 >= 0x30 && $0 <= 0x39 }), let n = Int64(e) { root.energy = readEnergy(n) }
        root.tags = readTags(fm.list("tags"))
        root.labels = fm.list("labels")
        var reminderIndex = 0
        for map in fm.maps("reminders") {
            if let reminder = try parseReminder(map, tz, state, reminderIndex) { root.reminders.append(reminder) }
            reminderIndex += 1
        }

        let ordered = parseList(sections[subtasks]!.lines, root, state)
        root.sequential = ordered ?? isTrue(fm.scalar("sequential"))
        try parseNotes(sections[notes]!.lines, root, state)
        parseAttachments(sections[attachments]!.lines, root, state)
        parseTimeEntries(sections[time]!.lines, root, state)

        for (kind, lines) in layout {
            if let kind {
                result.layout.append((kind, nil))
            } else if let raw = joinKeep(lines!.lines) {
                result.layout.append((nil, raw))
            }
        }

        let sigs = signatures(root)
        for key in canonicalKeys where fm.has(key) { result.keySignatures[OIKey(key)] = sigs[key] }
        result.needsWrite = state.needsWrite
        return result
    }

    private static func sectionKind(_ heading: String) -> String? {
        switch heading.oTrim().oLower() {
        case "subtasks", "steps", "tasks", "checklist": return subtasks
        case "notes", "log": return notes
        case "attachments", "files": return attachments
        case "time", "time log", "time spent", "time tracked": return time
        default: return nil
        }
    }

    final class ParseState {
        private var used = Set<UUID>()
        var file: ParsedTaskFile!
        let context: TaskMarkdownContext
        var needsWrite = false
        var byBlockId: [OKey: WorkItem] = [:]

        init(_ context: TaskMarkdownContext) { self.context = context }

        /// The id from the file if it's new here; else a fresh one (stable per file when the store gave a seed).
        func newId(_ candidate: UUID?, _ seedKey: String) -> UUID {
            if let c = candidate, c != emptyGuid, used.insert(c).inserted { return c }
            needsWrite = true
            var n = 0
            while true {
                let fresh = context.idSeed.map { VaultText.stableGuid("\($0)|\(seedKey)|\(n)") } ?? UUID()
                if used.insert(fresh).inserted { return fresh }
                n += 1
            }
        }
    }

    private final class Frame {
        let item: WorkItem
        let indent: Int
        let seedKey: String
        var ordered: Bool?
        let details = Lines()

        init(_ item: WorkItem, _ indent: Int, _ seedKey: String) {
            self.item = item
            self.indent = indent
            self.seedKey = seedKey
        }
    }

    /// The first top-level checkbox list in the details (for files typed without a "## Subtasks" heading).
    private static func extractChecklist(_ details: Lines) -> [String]? {
        var fence = VaultText.Fence()
        var i = 0
        while i < details.count {
            defer { i += 1 }
            if fence.accept(details.lines[i]) { continue }
            guard let m = listItem.match(details.lines[i]), m.group("ind").isEmpty, m.has("c") else { continue }
            var end = i + 1
            var inner = VaultText.Fence()
            while end < details.count {
                let line = details.lines[end]
                if inner.accept(line) {
                    end += 1
                    continue
                }
                if line.oTrim().isEmpty {
                    let next = details.lines[(end + 1)...].first { !$0.oTrim().isEmpty }
                    if next == nil || !(Ordinal.isWhite(next!.unit(0)) || listItem.isMatch(next!)) { break }
                } else if !Ordinal.isWhite(line.unit(0)) && !listItem.isMatch(line) {
                    break
                }
                end += 1
            }
            let list = Array(details.lines[i..<end])
            details.lines.removeSubrange(i..<end)
            return list
        }
        return nil
    }

    /// Parses the subtasks list into `root`; returns whether its top-level list is ordered.
    private static func parseList(_ lines: [String], _ root: WorkItem, _ state: ParseState) -> Bool? {
        var frames = [Frame(root, -1, "")]
        var created: [Frame] = []
        let leading = Lines()
        var occurrences: [OKey: Int] = [:]
        var pendingBlank = 0
        var fence = VaultText.Fence()
        var fenceTarget: Lines?
        var fenceStrip = 0
        for line in lines {
            if fence.isOpen {
                // Inside a code block: every line belongs to whoever opened it (examples never become tasks).
                fenceTarget!.add(fenceStrip == 0 ? line : stripColumns(line, fenceStrip))
                _ = fence.accept(line)
                continue
            }
            if line.oTrim().isEmpty {
                pendingBlank += 1
                continue
            }
            let indent = columns(line)
            if let m = listItem.match(line) {
                while frames.count > 1 && frames.last!.indent >= indent { frames.removeLast() }
                let parent = frames.last!
                let rest = m.group("rest")
                if m.has("c") || frames.count == 1 {
                    let titleKey = "\(parent.seedKey)/\(titleForSeed(rest))"
                    let count = (occurrences[OKey(titleKey)] ?? 0) + 1
                    occurrences[OKey(titleKey)] = count
                    let item = parseItem(m.has("c") ? m.group("c").unit(0) : 0x20, rest, state, isPlain: !m.has("c"), seedKey: "\(titleKey)#\(count)")
                    item.parent = parent.item
                    parent.item.children.append(item)
                    if parent.ordered == nil { parent.ordered = Ordinal.isDigit(m.group("m").unit(0)) }
                    if frames.count == 1, let heading = joinKeep(leading.lines) { state.file.leading.append((item.id, heading)) }
                    leading.lines.removeAll()
                    let frame = Frame(item, indent, titleKey)
                    frames.append(frame)
                    created.append(frame)
                    pendingBlank = 0
                    continue
                }
                if rest.oStarts("📎"), let attachment = parseAttachmentLine(rest.sub(2).oTrim(), state) {
                    parent.item.attachments.append(attachment)
                    pendingBlank = 0
                    continue
                }
            } else {
                while frames.count > 1 && indent <= frames.last!.indent { frames.removeLast() }
            }

            let topLevel = frames.count == 1
            let bucket = topLevel ? leading : frames.last!.details
            if bucket.count > 0 { bucket.lines.append(contentsOf: Array(repeating: "", count: pendingBlank)) }
            pendingBlank = 0
            let strip = topLevel ? 0 : frames.last!.indent + 4
            let text = topLevel ? line : stripColumns(line, strip)
            bucket.add(text)
            if fence.accept(text) {
                fenceTarget = bucket
                fenceStrip = strip
            }
        }

        for frame in created {
            frame.item.details = joinKeep(frame.details.lines)
            frame.item.sequential = frame.ordered ?? frame.item.sequential
        }
        state.file.subtasksTrailer = joinKeep(leading.lines)
        return frames[0].ordered
    }

    /// The part of a line that identifies a hand-written subtask: its words, without what people (or the Obsidian
    /// Tasks plugin) add as they work on it.
    private static func titleForSeed(_ rest: String) -> String {
        var text = hiddenMeta.replace(rest) { _ in "" }
        text = trailingBlockId.replace(text) { _ in "" }
        text = seedNoise.replace(text) { _ in " " }
        return spaces.replace(text) { _ in " " }.oTrim().oLower()
    }

    private static func parseItem(_ check: UInt16, _ input: String, _ state: ParseState, isPlain: Bool, seedKey: String) -> WorkItem {
        let ctx = state.context
        let tz = ctx.timeZone
        var rest = input
        var meta: JSONObject?
        if let hidden = hiddenMeta.match(rest) {
            meta = tryParseMeta(hidden.group("json"))
            if meta == nil {
                state.file.warnings.append("Ignored unreadable metadata on \"\(rest)\".")
                state.needsWrite = true
            }
            rest = rest.sub(0, hidden.index)
        }

        var blockId: String?
        if let block = trailingBlockId.match(rest) {
            blockId = block.group("bid")
            rest = rest.sub(0, block.index)
        }

        var due: LocalDay?, scheduled: LocalDay?, doneDate: LocalDay?
        rest = dateToken.replace(rest) { t in
            let date = VaultText.parseDate(t.group("d"))
            switch t.group("e") {
            case "📅": due = date
            case "⏳": scheduled = date
            default: doneDate = date
            }
            return " "
        }

        var priority = Priority.normal
        var emoji: String?
        rest = priorityToken.replace(rest) { t in
            emoji = t.group(1)
            priority = priorityOf(emoji!)
            return " "
        }

        var title = spaces.replace(rest) { _ in " " }.oTrim()
        var trailing: [String] = []
        while let tag = trailingTag.match(title), isTag(tag.group("t")) {
            trailing.insert(tag.group("t"), at: 0)
            title = title.sub(0, tag.index).oTrimEnd()
        }
        var tags: [String] = []
        for t in inlineTag.matches(title).map({ $0.group("t") }).filter(isTag) + trailing where !tags.contains(where: { Ordinal.equalIgnoreCase($0, t) }) {
            tags.append(t)
        }
        if title.isEmpty { title = trailing.first.map { "#" + $0 } ?? "(untitled)" }

        let id = state.newId(parseGuid(str(meta, "id")), seedKey)
        let createdMeta = VaultText.parseInstant(str(meta, "created"))
        state.needsWrite = state.needsWrite || createdMeta == nil || isPlain

        func resolve(_ visible: LocalDay?, _ exact: String?, _ defaultTime: String) -> Date? {
            let precise = VaultText.parseInstant(exact)
            guard let visible else {
                state.needsWrite = state.needsWrite || precise != nil
                return nil
            }
            if let p = precise, VaultText.localDay(p, tz) == visible { return p }
            state.needsWrite = true
            return VaultText.atLocal(visible, defaultTime, tz)
        }

        let item = WorkItem(id: id, title: title, priority: priority, createdAt: createdMeta ?? ctx.now, groupId: emptyGuid)
        item.deadline = resolve(due, str(meta, "due"), VaultText.defaultDueTime)
        item.nextActionAt = resolve(scheduled, str(meta, "next"), VaultText.defaultScheduledTime)
        item.afterId = parseGuid(str(meta, "after"))
        item.sequential = meta?["seq"]?.boolValue ?? false
        item.stepDelay = long(meta, "delay").map { TimeInterval($0 * 60) }
        item.icon = cleanIcon(str(meta, "icon"))
        item.color = cleanColor(str(meta, "color"))
        item.repeatRule = Recurrence.parseOrNil(str(meta, "repeat"))
        item.plannedAt = VaultText.parseInstant(str(meta, "plan"))
        item.plannedMinutes = readPlanMinutes(long(meta, "planMin"))
        item.energy = readEnergy(long(meta, "energy"))
        if let undo = meta?["repeatUndo"]?.objectValue {
            item.repeatUndo = RepeatUndo(deadline: VaultText.parseInstant(str(undo, "due")), nextActionAt: VaultText.parseInstant(str(undo, "next")), plannedAt: VaultText.parseInstant(str(undo, "plan")))
        }
        if let meta {
            let unknown = meta.pairs.filter { !subtaskMetaKeys.contains($0.0) }
            if !unknown.isEmpty { state.file.unknownMeta[id] = JSONObject(unknown) }
        }

        item.tags = tags
        if let more = meta?["tags"]?.arrayValue {
            for tag in readTags(more.compactMap(\.stringValue)) where !item.tags.contains(where: { Ordinal.equalIgnoreCase($0, tag) }) {
                item.tags.append(tag)
            }
        }
        if let labels = meta?["labels"]?.arrayValue { item.labels = labels.compactMap(\.stringValue) }

        var reminderIndex = 0
        for r in meta?["reminders"]?.arrayValue ?? [] {
            if let o = r.objectValue, let at = VaultText.parseInstant(str(o, "at")) {
                let reminderId = state.newId(parseGuid(str(o, "id")), "\(seedKey)|r\(reminderIndex)")
                reminderIndex += 1
                item.reminders.append(Reminder(id: reminderId, dueAt: at, message: str(o, "msg") ?? "", kind: str(o, "kind") == "next-action" ? .nextAction : .manual,
                                               notifiedAt: VaultText.parseInstant(str(o, "notified")), dismissedAt: VaultText.parseInstant(str(o, "dismissed"))))
            }
        }

        if check != 0x20 && check != 0x78 { state.file.checkChars[id] = check }
        if emoji == "🔼" || emoji == "⏬" { state.file.priorityEmoji[id] = emoji }

        let doneMeta = VaultText.parseInstant(str(meta, "done"))
        if isDoneChar(check) {
            if let d = doneDate, doneMeta == nil || VaultText.localDay(doneMeta!, tz) != d {
                item.completedAt = VaultText.atLocal(d, VaultText.defaultDoneTime, tz)
            } else {
                item.completedAt = doneMeta ?? ctx.now
            }
            state.needsWrite = state.needsWrite || doneMeta == nil
        } else {
            state.needsWrite = state.needsWrite || doneMeta != nil || doneDate != nil
        }

        if let blockId {
            if state.byBlockId[OKey(blockId)] == nil {
                state.byBlockId[OKey(blockId)] = item
                if blockId != VaultText.blockId(id) { state.file.blockIds[id] = blockId }
            }
        } else {
            state.needsWrite = true
        }
        if state.byBlockId[OKey(VaultText.blockId(id))] == nil { state.byBlockId[OKey(VaultText.blockId(id))] = item }
        return item
    }

    private static func parseNotes(_ lines: [String], _ root: WorkItem, _ state: ParseState) throws {
        let tz = state.context.timeZone
        var preamble: [String] = []
        var i = 0
        var pendingBlank = 0
        var index = 0
        while i < lines.count {
            guard let header = calloutHeader.match(lines[i]) else {
                if lines[i].oTrim().isEmpty {
                    pendingBlank += 1
                } else {
                    if !preamble.isEmpty { preamble.append(contentsOf: Array(repeating: "", count: pendingBlank)) }
                    pendingBlank = 0
                    preamble.append(lines[i])
                }
                i += 1
                continue
            }

            var head = header.group("head")
            let seedKey = "note|\(index)|\(head)"
            index += 1
            var meta: JSONObject?
            if let hidden = hiddenMeta.match(head) {
                meta = tryParseMeta(hidden.group("json"))
                head = head.sub(0, hidden.index)
            }

            var body: [String] = []
            i += 1
            while i < lines.count && lines[i].oStarts(">") {
                let l = lines[i]
                body.append(l.u16 > 1 && l.unit(1) == 0x20 ? l.sub(2) : l.sub(1))
                i += 1
            }

            var parts = head.oSplit("·").map { $0.oTrim() }.filter { !$0.isEmpty }
            var visible: Date?
            if let first = parts.first, noteTime.isMatch(first) {
                visible = try VaultText.parseTime(first, tz, "00:00")
                parts.removeFirst()
            }

            var target = root
            if let link = parts.firstIndex(where: { blockLink.isMatch($0) }) {
                let bid = blockLink.match(parts[link])!.group("bid")
                target = state.byBlockId[OKey(bid)] ?? root
                parts.remove(at: link)
            }

            var author = VaultText.parseActor(parts.first)
            if visible == nil && !parts.isEmpty && author.kind == .user && author.name != nil {
                // "> [!note] Meeting with Ana" is a heading, not an author.
                body.insert(parts.joined(separator: " · "), at: 0)
                author = .user
            }

            var sourceUrl: String?, sourceTitle: String?
            if let last = body.last, let source = sourceLine.match(last) {
                sourceUrl = source.has("u1") ? source.group("u1") : source.group("u2")
                sourceTitle = source.has("t") && !source.group("t").isEmpty ? source.group("t") : nil
                body.removeLast()
            }

            let text = joinKeep(body)
            let exact = VaultText.parseInstant(str(meta, "at"))
            let at: Date
            if let e = exact, visible == nil || VaultText.localMinute(e, tz) == VaultText.localMinute(visible!, tz) {
                at = e
            } else {
                at = visible ?? state.context.now
            }
            state.needsWrite = state.needsWrite || meta == nil || exact != at
            guard let text else {
                state.needsWrite = true
                continue
            }
            let id = state.newId(parseGuid(str(meta, "id")), seedKey)
            target.notes.append(Note(id: id, at: at, text: text, author: author, sourceUrl: sourceUrl, sourceTitle: sourceTitle))
        }
        state.file.notesPreamble = joinKeep(preamble)
    }

    /// `archived:` as people may write it: a time, `true`/`yes` (archived when it was finished), or anything else
    /// (not archived). Never fails.
    private static func parseArchived(_ text: String?, _ completed: Date?, _ tz: TimeZone, _ now: Date) -> Date? {
        let value = (text ?? "").oTrim()
        if ["true", "yes"].contains(value.oLower()) { return completed ?? now }
        if value.isEmpty { return nil }
        return (try? VaultText.parseTime(value, tz, VaultText.defaultDoneTime)) ?? nil
    }

    private static func parseStage(_ text: String?) -> Stage {
        switch (text ?? "").oTrim().oLower() {
        case "inbox": return .inbox
        case "doing", "in progress", "in-progress", "wip": return .doing
        default: return .next
        }
    }

    /// `## Time`: one line per stretch of time; lines typed by hand count too; other text stays put.
    private static func parseTimeEntries(_ lines: [String], _ root: WorkItem, _ state: ParseState) {
        let tz = state.context.timeZone
        var preamble: [String] = []
        var trailer: [String] = []
        var seenEntry = false
        var index = 0
        var touched: [WorkItem] = []

        // A mistyped time (24:00, 09:75, Feb 30) leaves the line as text: one typo never breaks the whole task.
        func tryTime(_ day: String, _ time: String) -> Date? {
            let padded = String(repeating: "0", count: max(0, 5 - time.u16)) + time
            return (try? VaultText.parseTime("\(day) \(padded)", tz, "00:00")) ?? nil
        }

        for line in lines {
            let m = timeLine.match(line)
            var start = m.flatMap { tryTime($0.group("sd"), $0.group("st")) }
            var shown: Date?
            if let m, start != nil, !m.has("run") {
                shown = tryTime(m.has("ed") ? m.group("ed") : m.group("sd"), m.group("et"))
                if shown == nil { start = nil }
            }
            guard let m, let startShown = start else {
                if seenEntry {
                    if !line.oTrim().isEmpty || !trailer.isEmpty { trailer.append(line) }
                } else if !line.oTrim().isEmpty || !preamble.isEmpty {
                    preamble.append(line)
                }
                continue
            }

            seenEntry = true
            var rest = m.group("rest")
            var meta: JSONObject?
            if let hidden = hiddenMeta.match(rest) {
                meta = tryParseMeta(hidden.group("json"))
                rest = rest.sub(0, hidden.index)
            }

            // The exact times (hidden) unless the visible ones were changed. An end earlier on the clock than the
            // start, without a date, is past midnight.
            let exactStart = VaultText.parseInstant(str(meta, "start"))
            let exactEnd = VaultText.parseInstant(str(meta, "end"))
            let startAt = exactStart.flatMap { VaultText.localMinute($0, tz) == VaultText.localMinute(startShown, tz) ? $0 : nil } ?? startShown
            var endAt: Date?
            if let visible = shown {
                let pastMidnight = !m.has("ed") && visible < startShown ? visible.addingTimeInterval(86_400) : visible
                if let ee = exactEnd, VaultText.localMinute(ee, tz) == VaultText.localMinute(visible, tz) || VaultText.localMinute(ee, tz) == VaultText.localMinute(pastMidnight, tz) {
                    endAt = ee
                } else {
                    endAt = pastMidnight
                }
            }
            state.needsWrite = state.needsWrite || meta == nil || exactStart != startAt || exactEnd != endAt

            var target = root
            var source = TimeSource.manual
            var extras: [String] = []
            for part in rest.oSplit("·").map({ $0.oTrim() }).filter({ !$0.isEmpty }) {
                if Ordinal.equalIgnoreCase(part, "focus") {
                    source = .focus
                } else if let link = blockLink.match(part) {
                    target = state.byBlockId[OKey(link.group("bid"))] ?? root
                } else if !durationText.isMatch(part) {
                    extras.append(part)
                }
            }

            let id = state.newId(parseGuid(str(meta, "id")), "time|\(index)|\(m.group("sd")) \(m.group("st"))")
            index += 1
            if !target.timeEntries.contains(where: { $0.id == id }) {
                target.timeEntries.append(TimeEntry(id: id, start: startAt, end: endAt, source: source, device: str(meta, "device")))
            }
            if !touched.contains(where: { $0 === target }) { touched.append(target) }
            if !extras.isEmpty { state.file.timeExtras[id] = extras.joined(separator: " · ") }
        }

        // Oldest first (lines merged from two devices can arrive out of order).
        for item in touched { item.timeEntries.sort { $0.start < $1.start } }
        state.file.timePreamble = joinKeep(preamble)
        state.file.timeTrailer = joinKeep(trailer)
    }

    private static func parseAttachments(_ lines: [String], _ root: WorkItem, _ state: ParseState) {
        var preamble: [String] = []
        for line in lines {
            if let m = listItem.match(line), !m.has("c"), m.group("ind").isEmpty, let attachment = parseAttachmentLine(m.group("rest"), state) {
                root.attachments.append(attachment)
            } else if !line.oTrim().isEmpty || !preamble.isEmpty {
                preamble.append(line)
            }
        }
        state.file.attachmentsPreamble = joinKeep(preamble)
    }

    private static func parseAttachmentLine(_ input: String, _ state: ParseState) -> Attachment? {
        var rest = input
        var meta: JSONObject?
        if let hidden = hiddenMeta.match(rest) {
            meta = tryParseMeta(hidden.group("json"))
            rest = rest.sub(0, hidden.index)
        }
        rest = rest.oTrim()
        let name: String, path: String
        if let md = markdownLink.match(rest), md.length == rest.u16 {
            let url = md.group("url").oTrim("<>")
            if url.oContains("://") { return nil }
            path = resolveRelative(state.context.fileDirectory, url.removingPercentEncoding ?? url)
            name = md.group("name").isEmpty ? VaultFiles.fileName(path) : md.group("name")
        } else if let w = wikiLink.match(rest), w.length == rest.u16 {
            path = w.group("target").oTrim()
            name = VaultFiles.fileName(path)
        } else {
            return nil
        }
        let id = state.newId(parseGuid(str(meta, "id")), "attachment|\(path)")
        let at = VaultText.parseInstant(str(meta, "at"))
        state.needsWrite = state.needsWrite || at == nil
        let attachment = Attachment(id: id, fileName: name, path: path, size: long(meta, "size") ?? 0, addedAt: at ?? state.context.now, addedBy: VaultText.parseActor(str(meta, "by")))
        state.file.rawLinks[id] = (rest, path, name)
        return attachment
    }

    private static func parseReminder(_ map: [(Node, Node)], _ tz: TimeZone, _ state: ParseState, _ index: Int) throws -> Reminder? {
        guard let at = try VaultText.parseTime(Frontmatter.get(map, "at"), tz, "09:00") else { return nil }
        let kind: ReminderKind = Ordinal.equalIgnoreCase((Frontmatter.get(map, "kind") ?? "").oRep("-", ""), "nextaction") ? .nextAction : .manual
        let id = state.newId(parseGuid(Frontmatter.get(map, "id")), "reminder|\(index)")
        return Reminder(id: id, dueAt: at, message: Frontmatter.get(map, "message") ?? "", kind: kind,
                        notifiedAt: try VaultText.parseTime(Frontmatter.get(map, "notified"), tz, "00:00"),
                        dismissedAt: try VaultText.parseTime(Frontmatter.get(map, "dismissed"), tz, "00:00"))
    }

    // ---- Rendering ----------------------------------------------------------------------

    /// What each owned property means, to tell whether the value changed since it was read.
    private static func signatures(_ root: WorkItem) -> [String: String] {
        func t(_ d: Date?) -> String { d.map(VaultText.utc) ?? "" }
        return [
            "status": root.isDone ? "done" : "open",
            "board": root.stage.rawValue,
            "archived": t(root.archivedAt),
            "priority": root.priority.rawValue,
            "icon": root.icon ?? "",
            "color": root.color ?? "",
            "created": VaultText.utc(root.createdAt),
            "completed": t(root.completedAt),
            "due": t(root.deadline),
            "scheduled": t(root.nextActionAt),
            "after": root.afterId.map(guidD) ?? "",
            "sequential": root.sequential ? "true" : "false",
            "step-delay": root.stepDelay.map { String(Int64($0 / 60)) } ?? "",
            "repeat": root.repeatRule?.description ?? "",
            "repeat-undo": repeatUndoText(root.repeatUndo) ?? "",
            "planned": t(root.plannedAt),
            "planned-length": root.plannedMinutes.map(String.init) ?? "",
            "energy": root.energy.map(String.init) ?? "",
            "tags": root.tags.joined(separator: "\u{1F}"),
            "labels": root.labels.joined(separator: "\u{1F}"),
            "reminders": root.reminders.map { "\(guidD($0.id))|\(VaultText.utc($0.dueAt))|\($0.message)|\($0.kind.rawValue)|\(t($0.notifiedAt))|\(t($0.dismissedAt))" }.joined(separator: "\u{1E}"),
        ]
    }

    private static func renderFrontmatter(_ sb: inout String, _ root: WorkItem, _ tz: TimeZone, _ previous: ParsedTaskFile?) {
        let idKey = previous?.idKey ?? "id"
        let sigs = signatures(root)
        var emitted = Set<OIKey>()
        for (key, raw) in previous?.frontmatter.blocks ?? [] {
            guard let key else {
                sb += raw
                continue
            }
            let owned = Ordinal.equalIgnoreCase(key, idKey) || isOwnedProperty(key)
            if !owned {
                sb += raw
            } else if emitted.insert(OIKey(key)).inserted {
                // An unchanged value is written exactly as the person typed it (date-only due, flow lists, quotes…).
                let canonical = canonicalKeys.first { Ordinal.equalIgnoreCase($0, key) }
                let before = previous!.keySignatures[OIKey(key)]
                let unchanged = before != nil && before == canonical.flatMap { sigs[$0] }
                sb += unchanged ? raw : (canonicalProperty(key, idKey, root, tz) ?? "")
            }
        }
        for key in [idKey] + canonicalKeys where emitted.insert(OIKey(key)).inserted {
            sb += canonicalProperty(key, idKey, root, tz) ?? ""
        }
    }

    private static func canonicalProperty(_ key: String, _ idKey: String, _ root: WorkItem, _ tz: TimeZone) -> String? {
        if Ordinal.equalIgnoreCase(key, idKey) { return "\(idKey): \(guidD(root.id))\n" }
        func list(_ key: String, _ values: [String]) -> String? {
            values.isEmpty ? nil : "\(key):\n" + values.map { "  - \(VaultText.yamlScalar($0))\n" }.joined()
        }
        switch key.oLower() {
        case "status": return "status: \(root.isDone ? "done" : "open")\n"
        case "board": return root.stage == .next ? nil : "board: \(root.stage.rawValue)\n"
        case "archived": return root.archivedAt.map { "archived: \(VaultText.utc($0))\n" }
        case "priority": return root.priority == .normal ? nil : "priority: \(root.priority.rawValue)\n"
        case "icon": return root.icon.map { "icon: \(iconScalar($0))\n" }
        // Quoted: a bare # starts a comment in YAML.
        case "color": return root.color.map { "color: \"\($0)\"\n" }
        case "created": return "created: \(VaultText.utc(root.createdAt))\n"
        case "completed": return root.completedAt.map { "completed: \(VaultText.utc($0))\n" }
        case "due": return root.deadline.map { "due: \(VaultText.localDateTime($0, tz))\n" }
        case "scheduled": return root.nextActionAt.map { "scheduled: \(VaultText.localDateTime($0, tz))\n" }
        case "after": return root.afterId.map { "after: \(guidD($0))\n" }
        case "sequential": return root.sequential ? "sequential: true\n" : nil
        case "step-delay": return root.stepDelay.map { "step-delay: \(VaultText.duration($0))\n" }
        case "repeat": return root.repeatRule.map { "repeat: \($0.description)\n" }
        case "repeat-undo": return repeatUndoText(root.repeatUndo).map { "repeat-undo: \($0)\n" }
        case "planned": return root.plannedAt.map { "planned: \(VaultText.localDateTime($0, tz))\n" }
        case "planned-length":
            guard root.plannedAt != nil, let length = root.plannedMinutes else { return nil }
            return "planned-length: \(VaultText.duration(TimeInterval(length * 60)))\n"
        case "energy": return root.energy.map { "energy: \($0)\n" }
        case "tags": return list("tags", root.tags)
        case "labels": return list("labels", root.labels)
        case "reminders":
            if root.reminders.isEmpty { return nil }
            var sb = "reminders:\n"
            for r in root.reminders {
                sb += "  - id: \(guidD(r.id))\n"
                sb += "    at: \(VaultText.utc(r.dueAt))\n"
                sb += "    message: \(JSONText.quote(r.message, .relaxed))\n"
                sb += "    kind: \(r.kind == .nextAction ? "next-action" : "manual")\n"
                if let n = r.notifiedAt { sb += "    notified: \(VaultText.utc(n))\n" }
                if let d = r.dismissedAt { sb += "    dismissed: \(VaultText.utc(d))\n" }
            }
            return sb
        default: return nil
        }
    }

    /// An emoji is written as itself (readable in Obsidian); only text YAML would misread is quoted.
    private static func iconScalar(_ icon: String) -> String {
        let first = icon.utf16.first ?? 0
        let needsQuotes = "-?:,[]{}#&*!|>'\"%@`".utf16.contains(first) || icon.oContains(": ") || icon.oContains(" #") || icon.oEnds(":") || !icon.oEq(icon.oTrim())
        return needsQuotes ? "\"" + icon.oRep("\\", "\\\\").oRep("\"", "\\\"") + "\"" : icon
    }

    private static func renderSection(_ kind: String, _ root: WorkItem, _ context: TaskMarkdownContext, _ previous: ParsedTaskFile?) -> String? {
        switch kind {
        case subtasks:
            var list = ""
            let current = Set(root.children.map(\.id))
            for (i, item) in root.children.enumerated() {
                if let heading = previous?.leadingText(item.id) {
                    if !list.isEmpty { list += "\n" }
                    list += VaultText.closeFences(heading) + "\n\n"
                }
                renderItem(&list, item, i, root.sequential, 0, context, previous)
            }
            // Text that sat above subtasks that are gone now, and text after the list, stays at the end.
            var rest = (previous?.leading ?? []).filter { !current.contains($0.id) }.map(\.text)
            if let t = previous?.subtasksTrailer { rest.append(t) }
            if !rest.isEmpty { list += (list.isEmpty ? "" : "\n") + VaultText.closeFences(rest.joined(separator: "\n\n")) + "\n" }
            return list.isEmpty ? nil : "## \(root.sequential ? "Steps" : "Subtasks")\n\n\(list.oTrimEnd("\n"))"

        case notes:
            var all: [(item: WorkItem, note: Note)] = []
            for item in root.selfAndDescendants { for note in item.notes { all.append((item, note)) } }
            let sorted = stableSorted(all) { $0.note.at < $1.note.at }
            if sorted.isEmpty && previous?.notesPreamble == nil { return nil }
            return section("Notes", previous?.notesPreamble, sorted.map { renderNote($0.item, $0.note, root, context.timeZone, previous) }.joined(separator: "\n\n"))

        case time:
            var all: [(item: WorkItem, entry: TimeEntry)] = []
            for item in root.selfAndDescendants { for entry in item.timeEntries { all.append((item, entry)) } }
            let sorted = stableSorted(all) { $0.entry.start < $1.entry.start }
            if sorted.isEmpty && previous?.timePreamble == nil { return nil }
            let text = section("Time", previous?.timePreamble, sorted.map { renderTimeEntry($0.item, $0.entry, root, context.timeZone, previous) }.joined(separator: "\n"))
            return previous?.timeTrailer.map { "\(text)\n\n\($0)" } ?? text

        default:
            if root.attachments.isEmpty && previous?.attachmentsPreamble == nil { return nil }
            return section("Attachments", previous?.attachmentsPreamble, root.attachments.map { "- " + renderAttachment($0, context, previous) }.joined(separator: "\n"))
        }
    }

    /// `OrderBy` (stable).
    static func stableSorted<T>(_ items: [T], _ less: (T, T) -> Bool) -> [T] {
        items.enumerated().sorted { a, b in less(a.element, b.element) || (!less(b.element, a.element) && a.offset < b.offset) }.map(\.element)
    }

    private static func renderItem(_ sb: inout String, _ item: WorkItem, _ index: Int, _ ordered: Bool, _ depth: Int, _ ctx: TaskMarkdownContext, _ previous: ParsedTaskFile?) {
        let indent = String(repeating: "\t", count: depth)
        sb += indent + (ordered ? "\(index + 1)." : "-") + " [" + String(decoding: [checkChar(item, previous)], as: UTF16.self) + "] " + itemLine(item, ctx.timeZone, previous) + "\n"
        if let details = item.details, !details.isEmpty {
            for line in VaultText.closeFences(details.oRep("\r\n", "\n")).oSplit("\n") {
                sb += (line.isEmpty ? "" : indent + "\t" + line) + "\n"
            }
        }
        for attachment in item.attachments { sb += indent + "\t- 📎 " + renderAttachment(attachment, ctx, previous) + "\n" }
        for (i, child) in item.children.enumerated() { renderItem(&sb, child, i, item.sequential, depth + 1, ctx, previous) }
    }

    private static func checkChar(_ item: WorkItem, _ previous: ParsedTaskFile?) -> UInt16 {
        if let c = previous?.checkChars[item.id], isDoneChar(c) == item.isDone { return c }
        return item.isDone ? 0x78 : 0x20
    }

    private static func itemLine(_ item: WorkItem, _ tz: TimeZone, _ previous: ParsedTaskFile?) -> String {
        var sb = item.title
        // Single-word tags are Obsidian tags on the line; a multi-word one can't be, so it goes in the hidden data.
        for tag in item.tags where !tag.oContains(" ") {
            let onLine = Rx("(^|\\s)#" + NSRegularExpression.escapedPattern(for: tag) + "(\\s|$)", ignoreCase: true)
            if !onLine.isMatch(item.title) { sb += " #" + tag }
        }
        var emoji: String?
        if let kept = previous?.priorityEmoji[item.id], priorityOf(kept) == item.priority {
            emoji = kept
        } else {
            switch item.priority {
            case .critical: emoji = "🔺"
            case .high: emoji = "⏫"
            case .low: emoji = "🔽"
            case .normal: emoji = nil
            }
        }
        if let emoji { sb += " " + emoji }
        if let due = item.deadline { sb += " 📅 " + VaultText.localDate(due, tz) }
        if let next = item.nextActionAt { sb += " ⏳ " + VaultText.localDate(next, tz) }
        if let done = item.completedAt { sb += " ✅ " + VaultText.localDate(done, tz) }
        sb += " ^" + blockIdOf(item, previous)

        var meta = JSONObject([("id", .string(guidD(item.id))), ("created", .string(VaultText.utc(item.createdAt)))])
        if let c = item.completedAt { meta["done"] = .string(VaultText.utc(c)) }
        if let d = item.deadline { meta["due"] = .string(VaultText.utc(d)) }
        if let n = item.nextActionAt { meta["next"] = .string(VaultText.utc(n)) }
        if let after = item.afterId { meta["after"] = .string(guidD(after)) }
        if item.sequential && item.children.isEmpty { meta["seq"] = .bool(true) }
        if let delay = item.stepDelay { meta["delay"] = .int(Int64((delay / 60).rounded(.toNearestOrEven))) }
        if let icon = item.icon { meta["icon"] = .string(icon) }
        if let color = item.color { meta["color"] = .string(color) }
        if let rule = item.repeatRule { meta["repeat"] = .string(rule.description) }
        if let plan = item.plannedAt {
            meta["plan"] = .string(VaultText.utc(plan))
            if let planMin = item.plannedMinutes { meta["planMin"] = .int(planMin) }
        }
        if let energy = item.energy { meta["energy"] = .int(energy) }
        if let undo = item.repeatUndo {
            func v(_ d: Date?) -> JSONValue { d.map { .string(VaultText.utc($0)) } ?? .null }
            meta["repeatUndo"] = .object(JSONObject([("due", v(undo.deadline)), ("next", v(undo.nextActionAt)), ("plan", v(undo.plannedAt))]))
        }
        if !item.labels.isEmpty { meta["labels"] = .array(item.labels.map { .string($0) }) }
        let multiWord = item.tags.filter { $0.oContains(" ") }
        if !multiWord.isEmpty { meta["tags"] = .array(multiWord.map { .string($0) }) }
        if !item.reminders.isEmpty {
            meta["reminders"] = .array(item.reminders.map { r in
                var o = JSONObject([("id", .string(guidD(r.id))), ("at", .string(VaultText.utc(r.dueAt))), ("msg", .string(r.message)), ("kind", .string(r.kind == .nextAction ? "next-action" : "manual"))])
                if let nt = r.notifiedAt { o["notified"] = .string(VaultText.utc(nt)) }
                if let dm = r.dismissedAt { o["dismissed"] = .string(VaultText.utc(dm)) }
                return .object(o)
            })
        }
        for (key, value) in previous?.unknownMeta[item.id]?.pairs ?? [] { meta[key] = value }
        return sb + " %%" + VaultText.hidden(.object(meta)) + "%%"
    }

    private static func renderNote(_ item: WorkItem, _ note: Note, _ root: WorkItem, _ tz: TimeZone, _ previous: ParsedTaskFile?) -> String {
        var sb = "> [!note] " + VaultText.localMinute(note.at, tz) + " · " + VaultText.displayName(note.author)
        if item !== root { sb += " · [[#^" + blockIdOf(item, previous) + "|" + linkAlias.replace(item.title) { _ in " " } + "]]" }
        sb += " %%" + VaultText.hidden(.object(JSONObject([("id", .string(guidD(note.id))), ("at", .string(VaultText.utc(note.at)))]))) + "%%"
        for line in note.text.oRep("\r\n", "\n").oSplit("\n") { sb += "\n" + (line.isEmpty ? ">" : "> " + line) }
        if let url = note.sourceUrl {
            if let t = note.sourceTitle, !t.isEmpty {
                sb += "\n> 🔗 [" + linkAlias.replace(t) { _ in " " } + "](" + url + ")"
            } else {
                sb += "\n> 🔗 <" + url + ">"
            }
        }
        return sb
    }

    private static func renderTimeEntry(_ item: WorkItem, _ entry: TimeEntry, _ root: WorkItem, _ tz: TimeZone, _ previous: ParsedTaskFile?) -> String {
        let start = VaultText.toLocal(entry.start, tz)
        var sb = "- " + VaultText.localMinute(entry.start, tz)
        if let endAt = entry.end {
            // The end's date is shown when it's another day, or earlier on the clock, so it can't be read as the next day.
            let end = VaultText.toLocal(endAt, tz)
            let sameDay = end.date == start.date && end.hour * 60 + end.minute >= start.hour * 60 + start.minute
            let endText = VaultText.localMinute(endAt, tz)
            sb += "–" + (sameDay ? endText.sub(11) : endText)
            sb += " · " + durationLabel(endAt.timeIntervalSince(entry.start))
        } else {
            sb += " → running"
        }
        if entry.source == .focus { sb += " · focus" }
        if let extra = previous?.timeExtras[entry.id] { sb += " · " + extra }
        if item !== root { sb += " · [[#^" + blockIdOf(item, previous) + "|" + linkAlias.replace(item.title) { _ in " " } + "]]" }
        var meta = JSONObject([("id", .string(guidD(entry.id))), ("start", .string(VaultText.utc(entry.start)))])
        if let e = entry.end { meta["end"] = .string(VaultText.utc(e)) }
        if let device = entry.device, !device.isEmpty { meta["device"] = .string(device) }
        return sb + " %%" + VaultText.hidden(.object(meta)) + "%%"
    }

    /// "25 min", or "2 h 05 min".
    public static func durationLabel(_ span: TimeInterval) -> String {
        let minutes = Int((span / 60).rounded(.toNearestOrEven))
        return minutes < 60 ? "\(minutes) min" : "\(minutes / 60) h \(String(format: "%02d", minutes % 60)) min"
    }

    private static func renderAttachment(_ attachment: Attachment, _ ctx: TaskMarkdownContext, _ previous: ParsedTaskFile?) -> String {
        // The link exactly as written (wiki link, embed, alias/size) as long as it still points to the same file.
        let link: String
        if let raw = previous?.rawLinks[attachment.id], raw.path.oEq(attachment.path), raw.name.oEq(attachment.fileName) {
            link = raw.raw
        } else {
            link = "[" + linkAlias.replace(attachment.fileName) { _ in " " } + "](" + escapeUrl(relativeTo(ctx.fileDirectory, attachment.path)) + ")"
        }
        let meta = JSONObject([("id", .string(guidD(attachment.id))), ("size", .int(attachment.size)), ("at", .string(VaultText.utc(attachment.addedAt))), ("by", .string(VaultText.displayName(attachment.addedBy)))])
        return link + " %%" + VaultText.hidden(.object(meta)) + "%%"
    }

    private static func section(_ heading: String, _ preamble: String?, _ content: String) -> String {
        "## \(heading)\n\n" + [preamble, content].compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: "\n\n")
    }

    private static func blockIdOf(_ item: WorkItem, _ previous: ParsedTaskFile?) -> String {
        previous?.blockIds[item.id] ?? VaultText.blockId(item.id)
    }

    // ---- Helpers ------------------------------------------------------------------------

    /// The lines without leading/trailing blank lines, otherwise exactly as written.
    static func joinKeep(_ lines: [String]) -> String? {
        guard let start = lines.firstIndex(where: { !$0.oTrim().isEmpty }) else { return nil }
        let end = lines.lastIndex { !$0.oTrim().isEmpty }!
        return lines[start...end].joined(separator: "\n")
    }

    private static func columns(_ line: String) -> Int {
        var col = 0
        for c in line.utf16 {
            if c == 0x09 { col = (col / 4 + 1) * 4 } else if c == 0x20 { col += 1 } else { break }
        }
        return col
    }

    private static func stripColumns(_ line: String, _ columns: Int) -> String {
        let units = Array(line.utf16)
        var col = 0, i = 0
        while i < units.count && col < columns && (units[i] == 0x20 || units[i] == 0x09) {
            col = units[i] == 0x09 ? (col / 4 + 1) * 4 : col + 1
            i += 1
        }
        return line.sub(i)
    }

    private static func resolveRelative(_ directory: String, _ relative: String) -> String {
        var parts = directory.oSplit("/").filter { !$0.isEmpty }
        for segment in relative.oRep("\\", "/").oSplit("/") {
            if segment == ".." {
                // Keep ".." that climbs above the vault so the store can refuse it instead of silently clamping.
                if let last = parts.last, last != ".." { parts.removeLast() } else { parts.append("..") }
            } else if segment != "." && !segment.isEmpty {
                parts.append(segment)
            }
        }
        return parts.joined(separator: "/")
    }

    private static func relativeTo(_ directory: String, _ path: String) -> String {
        let from = directory.oSplit("/").filter { !$0.isEmpty }
        let to = path.oSplit("/").filter { !$0.isEmpty }
        var common = 0
        while common < from.count && common < to.count - 1 && Ordinal.equalIgnoreCase(from[common], to[common]) { common += 1 }
        return (Array(repeating: "..", count: from.count - common) + to[common...]).joined(separator: "/")
    }

    private static func escapeUrl(_ path: String) -> String {
        path.oRep("%", "%25").oRep(" ", "%20").oRep("(", "%28").oRep(")", "%29")
    }

    private static func parsePriority(_ text: String?) -> Priority {
        switch (text ?? "").oTrim().oLower() {
        case "critical", "urgent", "highest": return .critical
        case "high": return .high
        case "low", "lowest": return .low
        default: return .normal
        }
    }

    private static func priorityOf(_ emoji: String) -> Priority {
        switch emoji {
        case "🔺": return .critical
        case "⏫": return .high
        case "🔽", "⏬": return .low
        default: return .normal
        }
    }

    private static func isDoneChar(_ c: UInt16) -> Bool { c == 0x78 || c == 0x58 || c == 0x2D }

    private static func isTrue(_ text: String?) -> Bool { ["true", "yes"].contains((text ?? "").oTrim().oLower()) }

    private static func isTag(_ tag: String) -> Bool {
        tag.utf16.contains { !Ordinal.isDigit($0) && $0 != 0x2F } && !tag.oEnds("/")
    }

    private static func str(_ meta: JSONObject?, _ key: String) -> String? { meta?[key]?.stringValue }

    private static func long(_ meta: JSONObject?, _ key: String) -> Int64? { meta?[key]?.int64Value }

    /// `due 2026-01-02T17:00:00Z, scheduled -` (a dash: none), and `, planned …` when it had a block.
    private static func repeatUndoText(_ undo: RepeatUndo?) -> String? {
        guard let undo else { return nil }
        return "due \(undo.deadline.map(VaultText.utc) ?? "-"), scheduled \(undo.nextActionAt.map(VaultText.utc) ?? "-")" + (undo.plannedAt.map { ", planned \(VaultText.utc($0))" } ?? "")
    }

    private static let repeatUndoPattern = Rx(#"^\s*due\s+(?<due>[^\s,]+)\s*,\s*scheduled\s+(?<next>[^\s,]+)\s*(?:,\s*planned\s+(?<plan>[^\s,]+)\s*)?$"#)

    private static func parseRepeatUndo(_ text: String?) -> RepeatUndo? {
        guard let text, let m = repeatUndoPattern.match(text) else { return nil }
        func read(_ v: String) -> Date? { v == "-" ? nil : VaultText.parseInstant(v) }
        return RepeatUndo(deadline: read(m.group("due")), nextActionAt: read(m.group("next")), plannedAt: m.has("plan") ? read(m.group("plan")) : nil)
    }

    private static func tryParseMeta(_ json: String) -> JSONObject? { (try? JSONText.parse(json))?.objectValue }

    // ---- What the board accepts (C#'s TaskBoard helpers) ---------------------------------

    static func cleanIcon(_ icon: String?) -> String? {
        guard let clean = icon?.oTrim(), !clean.isEmpty else { return nil }
        // Line and paragraph separators would break the line it's written on.
        return clean.u16 <= 16 && !clean.utf16.contains(where: { Ordinal.isControl($0) || $0 == 0x2028 || $0 == 0x2029 }) ? clean : nil
    }

    static func cleanColor(_ color: String?) -> String? {
        guard let clean = color?.oTrim(), !clean.isEmpty else { return nil }
        let hex = clean.utf16.dropFirst()
        return clean.u16 == 7 && clean.oStarts("#") && hex.allSatisfy({ ($0 >= 0x30 && $0 <= 0x39) || ($0 >= 0x41 && $0 <= 0x46) || ($0 >= 0x61 && $0 <= 0x66) }) ? clean.lowercased() : nil
    }

    static func readPlanMinutes(_ minutes: Int64?) -> Int? {
        guard let m = minutes, m >= 5, m <= 720, m != 30 else { return nil }
        return Int(m)
    }

    static func readEnergy(_ level: Int64?) -> Int? {
        guard let l = level, l >= 1, l <= 3 else { return nil }
        return Int(l)
    }

    private static let whiteRun = Rx(#"\s+"#)
    private static let tagPattern = Rx(#"^[\p{L}\p{N}_\-/]+( [\p{L}\p{N}_\-/]+)*$"#)

    /// A tag as typed: no #, no spaces at the edges, one space between words.
    static func cleanTag(_ raw: String?) -> String { whiteRun.replace((raw ?? "").oTrim().oTrimStart("#").oTrim()) { _ in " " } }

    static func isValidTag(_ tag: String) -> Bool {
        tag.u16 > 0 && tag.u16 <= 50 && tagPattern.isMatch(tag) && !tag.utf16.allSatisfy { Ordinal.isDigit($0) || $0 == 0x2F || $0 == 0x20 }
    }

    /// Tags read from a file: cleaned, invalid ones left out, each once (ignoring case).
    static func readTags(_ tags: [String]) -> [String] {
        var result: [String] = []
        for tag in tags.map(cleanTag) where isValidTag(tag) && !result.contains(where: { Ordinal.equalIgnoreCase($0, tag) }) { result.append(tag) }
        return result
    }

    // ---- Patterns -------------------------------------------------------------------------

    static let listItem = Rx(#"^(?<ind>[ \t]*)(?<m>[-*+]|\d+[.)])[ \t]+(?:\[(?<c>[^\]\x{10000}-\x{10FFFF}])\][ \t]?)?(?<rest>.*)$"#)
    static let hiddenMeta = Rx(#"\s*%%(?<json>\{.*\})%%\s*$"#)
    static let trailingBlockId = Rx(#"(^|\s+)\^(?<bid>[A-Za-z0-9-]+)\s*$"#)
    private static let dateToken = Rx(#"(?<e>📅|⏳|✅)\x{FE0F}?\s*(?<d>\d{4}-\d{2}-\d{2})"#)
    private static let priorityToken = Rx(#"(🔺|⏫|🔼|🔽|⏬)\x{FE0F}?"#)
    private static let spaces = Rx(#"\s{2,}"#)
    private static let trailingTag = Rx(#"(^|\s+)#(?<t>[\p{L}\p{N}_\-/]+)$"#)
    private static let inlineTag = Rx(#"(?<=^|\s)#(?<t>[\p{L}\p{N}_\-/]+)"#)
    private static let calloutHeader = Rx(#"^>\s*\[!(?<type>[^\]]+)\][+-]?\s*(?<head>.*)$"#)
    private static let noteTime = Rx(#"^\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}"#)
    private static let blockLink = Rx(#"\[\[#\^(?<bid>[^|\]]+)(\|[^\]]*)?\]\]"#)
    private static let sourceLine = Rx(#"^🔗\s*(?:\[(?<t>[^\]]*)\]\((?<u1>https?://[^)\s]+)\)|<?(?<u2>https?://[^>\s]+)>?)\s*$"#)
    private static let markdownLink = Rx(#"^!?\[(?<name>[^\]]*)\]\((?<url><[^>]+>|[^)\s]+)\)"#)
    private static let wikiLink = Rx(#"^!?\[\[(?<target>[^\]|#]+)(?:\|(?<alias>[^\]]*))?\]\]"#)
    private static let linkAlias = Rx(#"[\[\]|]"#)
    private static let timeLine = Rx(#"^[-*+] (?<sd>\d{4}-\d{2}-\d{2}) (?<st>\d{1,2}:\d{2})\s*(?:(?:–|—|-|to)\s*(?:(?<ed>\d{4}-\d{2}-\d{2})\s+)?(?<et>\d{1,2}:\d{2})|(?<run>→\s*(?:running|now)))(?<rest>.*)$"#, ignoreCase: true)
    private static let durationText = Rx(#"^\d+\s*(?:min|m|h)(?:\s*\d{1,2}\s*min)?$"#, ignoreCase: true)
    private static let seedNoise = Rx(#"(?:📅|⏳|✅|➕|🛫|❌)\x{FE0F}?\s*\d{4}-\d{2}-\d{2}|🔺|⏫|🔼|🔽|⏬|🔁|(?<=^|\s)#[\p{L}\p{N}_\-/]+|\x{FE0F}"#)
}
