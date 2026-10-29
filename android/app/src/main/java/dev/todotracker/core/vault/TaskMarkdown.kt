package dev.todotracker.core.vault

import dev.todotracker.core.Actor
import dev.todotracker.core.Attachment
import dev.todotracker.core.Note
import dev.todotracker.core.Priority
import dev.todotracker.core.Reminder
import dev.todotracker.core.ReminderKind
import dev.todotracker.core.RepeatUndo
import dev.todotracker.core.Stage
import dev.todotracker.core.TimeEntry
import dev.todotracker.core.TimeSource
import dev.todotracker.core.WorkItem
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.charset.CharacterCodingException
import java.nio.charset.CodingErrorAction
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.util.UUID
import java.util.regex.Matcher
import java.util.regex.Pattern
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import org.yaml.snakeyaml.nodes.MappingNode

/** A task file read from disk, plus everything needed to write it back without losing anything (TodoTracker.Core's ParsedTaskFile). */
class ParsedTaskFile internal constructor(
    /** The task tree (not on a board). */
    val root: WorkItem,
) {
    /** The file lacks something the app keeps (ids, exact times): it's added the next time the app saves this task for another reason. */
    var needsWrite: Boolean = false
        internal set

    /** The file carries the app's task id. */
    var hasAppId: Boolean = false
        internal set

    val warnings: MutableList<String> = mutableListOf()

    internal var frontmatter: Frontmatter = Frontmatter.EMPTY
    internal var idKey: String = "id"

    /** What each owned property meant when read (keyed case-insensitively); unchanged values are written back as typed. */
    internal val keySignatures = HashMap<String, String>()
    internal val blockIds = HashMap<UUID, String>()
    internal val checkChars = HashMap<UUID, Char>()
    internal val priorityEmoji = HashMap<UUID, String>()
    internal val unknownMeta = HashMap<UUID, JsonObject>()
    internal val rawLinks = HashMap<UUID, Triple<String, String, String>>()
    internal val leading = LinkedHashMap<UUID, String>()
    internal var subtasksTrailer: String? = null
    internal var notesPreamble: String? = null
    internal var attachmentsPreamble: String? = null
    internal var timePreamble: String? = null
    internal var timeTrailer: String? = null
    internal val timeExtras = HashMap<UUID, String>()
    internal val layout = mutableListOf<Pair<String?, String?>>()
    internal var newline: String = "\n"
    internal var bom: Boolean = false
}

/**
 * Reads and writes one top-level task as Obsidian-friendly markdown, byte for byte as TodoTracker.Core.Vault.TaskMarkdown
 * (docs/vault-format.md): YAML frontmatter, `# Title`, details, and the Subtasks/Steps, Notes, Time and Attachments
 * sections. Anything the app doesn't own is written back as it was.
 */
object TaskMarkdown {
    private const val SUBTASKS = "subtasks"
    private const val NOTES = "notes"
    private const val ATTACHMENTS = "attachments"
    private const val TIME = "time"

    /** A group the parsed root isn't in yet (the store attaches it to one). */
    internal val NO_GROUP = UUID(0, 0)

    private val subtaskMetaKeys = setOf("id", "created", "done", "due", "next", "after", "seq", "delay", "labels", "tags", "reminders", "icon", "color", "repeat", "repeatUndo", "plan", "planMin", "energy")

    private val canonicalKeys = listOf("status", "board", "priority", "icon", "color", "created", "completed", "archived", "due", "scheduled", "after", "sequential", "step-delay", "repeat", "repeat-undo", "planned", "planned-length", "energy", "tags", "labels", "reminders")
    private val canonicalCi = canonicalKeys.map(Net::ci).toSet()

    /** A frontmatter property the app writes (besides the id); everything else belongs to the person. */
    internal fun isOwnedProperty(key: String): Boolean = Net.ci(key) in canonicalCi

    /** Reads a task file; a file that can't be read safely is a [VaultFormatException]. */
    fun parse(text: String, fileStem: String, context: TaskMarkdownContext): ParsedTaskFile = try {
        parseCore(text, fileStem, context)
    } catch (e: NetFormatException) {
        throw VaultFormatException(e.message ?: "Not a valid task file.", e)
    }

    fun render(root: WorkItem, context: TaskMarkdownContext, previous: ParsedTaskFile? = null): String {
        val sb = StringBuilder()
        sb.append("---\n")
        renderFrontmatter(sb, root, context.zone, previous)
        sb.append("---\n# ").append(root.title).append('\n')
        val blocks = mutableListOf<String>()
        val details = root.details
        if (!Net.isBlank(details)) blocks.add(VaultText.closeFences(details!!.replace("\r\n", "\n").trim('\n')))
        val rendered = HashSet<String>()
        for ((kind, raw) in previous?.layout ?: emptyList()) {
            if (kind == null) {
                blocks.add(raw!!)
            } else if (rendered.add(kind)) {
                renderSection(kind, root, context, previous)?.let(blocks::add)
            }
        }
        for (kind in listOf(SUBTASKS, NOTES, TIME, ATTACHMENTS)) {
            if (rendered.add(kind)) renderSection(kind, root, context, previous)?.let(blocks::add)
        }
        if (blocks.isNotEmpty()) sb.append('\n').append(blocks.joinToString("\n\n")).append('\n')
        var result = sb.toString()
        if (previous?.newline == "\r\n") result = result.replace("\n", "\r\n")
        return if (previous?.bom == true) "﻿" + result else result
    }

    // ---- Parsing ----

    private fun parseCore(input: String, fileStem: String, ctx: TaskMarkdownContext): ParsedTaskFile {
        var text = input
        val bom = text.startsWith('﻿')
        if (bom) text = text.substring(1)
        val newline = if (text.contains("\r\n")) "\r\n" else "\n"
        text = text.replace("\r\n", "\n")
        val (fm, body, present) = Frontmatter.split(text)
        val tz = ctx.zone
        val state = ParseState(ctx)
        state.needsWrite = !present

        // The app's id: tt-id, else a GUID id. A non-GUID id belongs to the person (a Zettelkasten id) and stays.
        var idKey = "id"
        var appId: UUID? = null
        val ttId = Net.parseGuid(fm.scalar("tt-id"))
        val plainId = Net.parseGuid(fm.scalar("id"))
        if (ttId != null) {
            idKey = "tt-id"
            appId = ttId
        } else if (plainId != null) {
            appId = plainId
        } else if (fm.has("id")) {
            idKey = "tt-id"
        }
        val id = state.newId(appId, "root")

        var title: String? = null
        val details = mutableListOf<String>()
        val sections = linkedMapOf(SUBTASKS to mutableListOf<String>(), NOTES to mutableListOf(), ATTACHMENTS to mutableListOf(), TIME to mutableListOf())
        val layout = mutableListOf<Pair<String?, MutableList<String>?>>()
        var current = details
        val fence = VaultText.Fence()
        for (line in body.split('\n')) {
            if (!fence.accept(line)) {
                if (title == null && line.startsWith("# ") && layout.isEmpty()) {
                    title = Net.trim(line.substring(2))
                    continue
                }
                if (line.startsWith("## ")) {
                    val kind = sectionKind(line.substring(3))
                    if (kind != null) {
                        current = sections[kind]!!
                        if (layout.none { it.first == kind }) layout.add(kind to null)
                        continue
                    }
                    if (layout.isNotEmpty()) {
                        current = mutableListOf()
                        layout.add(null to current)
                    }
                }
            }
            current.add(line)
        }

        val hasAppId = appId != null
        if (layout.none { it.first == SUBTASKS } && !hasAppId) {
            val checklist = extractChecklist(details)
            if (checklist != null && checklist.isNotEmpty()) {
                // A note typed in Obsidian: its first checkbox list is the subtasks.
                sections[SUBTASKS] = checklist
                state.needsWrite = true
            }
        }

        state.needsWrite = state.needsWrite or (title == null || !hasAppId)
        val created = VaultText.parseTime(fm.scalar("created"), tz, "00:00")
        state.needsWrite = state.needsWrite or (created == null)
        val root = WorkItem(id, if (Net.isBlank(title)) fileStem else title!!, parsePriority(fm.scalar("priority")), created ?: ctx.now, NO_GROUP)
        root.details = joinKeep(details)
        root.deadline = VaultText.parseTime(fm.scalar("due"), tz, VaultText.DEFAULT_DUE_TIME)
        root.nextActionAt = VaultText.parseTime(fm.scalar("scheduled"), tz, VaultText.DEFAULT_SCHEDULED_TIME)
        root.afterId = Net.parseGuid(fm.scalar("after"))
        root.stepDelay = VaultText.parseDuration(fm.scalar("step-delay"))
        val result = ParsedTaskFile(root)
        result.frontmatter = fm
        result.newline = newline
        result.bom = bom
        result.idKey = idKey
        result.hasAppId = hasAppId
        state.file = result

        val status = Net.lower(Net.trim(fm.scalar("status") ?: ""))
        val done = status in setOf("done", "completed", "complete", "cancelled", "canceled")
        state.needsWrite = state.needsWrite or status.isEmpty()
        val completed = VaultText.parseTime(fm.scalar("completed"), tz, VaultText.DEFAULT_DONE_TIME)
        if (done) {
            root.completedAt = completed ?: ctx.now
            state.needsWrite = state.needsWrite or (completed == null)
        } else {
            state.needsWrite = state.needsWrite or (completed != null)
        }

        // Only a done task can be archived: one reopened by hand (status: open) is back in the lists.
        val archived = parseArchived(fm.scalar("archived"), root.completedAt, tz, ctx.now)
        root.archivedAt = if (done) archived else null
        state.needsWrite = state.needsWrite or (!done && archived != null)

        root.stage = parseStage(fm.scalar("board"))
        root.icon = Values.icon(fm.scalar("icon"))
        root.color = Values.color(fm.scalar("color"))
        root.repeat = Values.repeat(fm.scalar("repeat"))
        root.repeatUndo = parseRepeatUndo(fm.scalar("repeat-undo"))
        root.plannedAt = VaultText.parseTime(fm.scalar("planned"), tz, "09:00")
        root.plannedMinutes = VaultText.parseDuration(fm.scalar("planned-length"))?.let { Values.planMinutes(it.toMinutes()) }
        root.energy = fm.scalar("energy")?.takeIf { it.isNotEmpty() && it.all { c -> c in '0'..'9' } }?.toLongOrNull()?.let(Values::energy)
        root.tags.addAll(Values.readTags(fm.list("tags")))
        root.labels.addAll(fm.list("labels"))
        var reminderIndex = 0
        for (map in fm.maps("reminders")) {
            parseReminder(map, tz, state, reminderIndex++)?.let(root.reminders::add)
        }

        val ordered = parseList(sections[SUBTASKS]!!, root, state)
        root.sequential = ordered ?: isTrue(fm.scalar("sequential"))
        parseNotes(sections[NOTES]!!, root, state)
        parseAttachments(sections[ATTACHMENTS]!!, root, state)
        parseTimeEntries(sections[TIME]!!, root, state)

        for ((kind, lines) in layout) {
            if (kind != null) {
                result.layout.add(kind to null)
            } else {
                joinKeep(lines!!)?.let { result.layout.add(null to it) }
            }
        }

        val signatures = signatures(root)
        for (key in canonicalKeys) {
            if (fm.has(key)) result.keySignatures[Net.ci(key)] = signatures[key]!!
        }
        result.needsWrite = state.needsWrite
        return result
    }

    private fun sectionKind(heading: String): String? = when (Net.lower(Net.trim(heading))) {
        "subtasks", "steps", "tasks", "checklist" -> SUBTASKS
        "notes", "log" -> NOTES
        "attachments", "files" -> ATTACHMENTS
        "time", "time log", "time spent", "time tracked" -> TIME
        else -> null
    }

    private class ParseState(val context: TaskMarkdownContext) {
        private val used = HashSet<UUID>()
        lateinit var file: ParsedTaskFile
        var needsWrite = false
        val byBlockId = HashMap<String, WorkItem>()

        /** The id from the file if it's new here; else a fresh one (stable per file when the store gave a seed). */
        fun newId(candidate: UUID?, seedKey: String): UUID {
            if (candidate != null && candidate != NO_GROUP && used.add(candidate)) return candidate
            needsWrite = true
            var n = 0
            while (true) {
                val seed = context.idSeed
                val fresh = if (seed != null) Net.stableGuid("$seed|$seedKey|$n") else UUID.randomUUID()
                if (used.add(fresh)) return fresh
                n++
            }
        }
    }

    private class Frame(val item: WorkItem, val indent: Int, val seedKey: String) {
        var ordered: Boolean? = null
        val details = mutableListOf<String>()
    }

    private fun m(p: Pattern, s: String): Matcher? = p.matcher(s).let { if (it.find()) it else null }

    private fun has(m: Matcher, group: String): Boolean = m.start(group) >= 0

    private fun g(m: Matcher, group: String): String = m.group(group) ?: ""

    /** The first top-level checkbox list in the details (for files typed without a "## Subtasks" heading). */
    private fun extractChecklist(details: MutableList<String>): MutableList<String>? {
        val fence = VaultText.Fence()
        for (i in details.indices) {
            if (fence.accept(details[i])) continue
            val match = m(listItem, details[i])
            if (match == null || g(match, "ind").isNotEmpty() || !has(match, "c")) continue
            var end = i + 1
            val inner = VaultText.Fence()
            while (end < details.size) {
                val line = details[end]
                if (inner.accept(line)) {
                    end++
                    continue
                }
                if (Net.trim(line).isEmpty()) {
                    val next = details.drop(end + 1).firstOrNull { Net.trim(it).isNotEmpty() }
                    if (next == null || !(Net.isWhite(next[0]) || listItem.matcher(next).find())) break
                } else if (!Net.isWhite(line[0]) && !listItem.matcher(line).find()) {
                    break
                }
                end++
            }
            val list = details.subList(i, end).toMutableList()
            repeat(end - i) { details.removeAt(i) }
            return list
        }
        return null
    }

    /** Parses the subtasks list into [root]; returns whether its top-level list is ordered. */
    private fun parseList(lines: List<String>, root: WorkItem, state: ParseState): Boolean? {
        val frames = mutableListOf(Frame(root, -1, ""))
        val created = mutableListOf<Frame>()
        val leading = mutableListOf<String>()
        val occurrences = HashMap<String, Int>()
        var pendingBlank = 0
        val fence = VaultText.Fence()
        var fenceTarget: MutableList<String>? = null
        var fenceStrip = 0
        for (line in lines) {
            if (fence.isOpen) {
                // Inside a code block: every line belongs to whoever opened it (examples never become tasks).
                fenceTarget!!.add(if (fenceStrip == 0) line else stripColumns(line, fenceStrip))
                fence.accept(line)
                continue
            }
            if (Net.trim(line).isEmpty()) {
                pendingBlank++
                continue
            }
            val indent = columns(line)
            val match = m(listItem, line)
            if (match != null) {
                while (frames.size > 1 && frames.last().indent >= indent) frames.removeAt(frames.size - 1)
                val parent = frames.last()
                val rest = g(match, "rest")
                if (has(match, "c") || frames.size == 1) {
                    val titleKey = "${parent.seedKey}/${titleForSeed(rest)}"
                    occurrences[titleKey] = (occurrences[titleKey] ?: 0) + 1
                    val item = parseItem(if (has(match, "c")) g(match, "c")[0] else ' ', rest, state, !has(match, "c"), "$titleKey#${occurrences[titleKey]}")
                    item.parent = parent.item
                    parent.item.children.add(item)
                    if (parent.ordered == null) parent.ordered = g(match, "m")[0].isDigit()
                    if (frames.size == 1) joinKeep(leading)?.let { state.file.leading[item.id] = it }
                    leading.clear()
                    val frame = Frame(item, indent, titleKey)
                    frames.add(frame)
                    created.add(frame)
                    pendingBlank = 0
                    continue
                }
                if (rest.startsWith("📎")) {
                    val attachment = parseAttachmentLine(Net.trim(rest.substring(2)), state)
                    if (attachment != null) {
                        parent.item.attachments.add(attachment)
                        pendingBlank = 0
                        continue
                    }
                }
            } else {
                while (frames.size > 1 && indent <= frames.last().indent) frames.removeAt(frames.size - 1)
            }

            val topLevel = frames.size == 1
            val bucket = if (topLevel) leading else frames.last().details
            if (bucket.isNotEmpty()) repeat(pendingBlank) { bucket.add("") }
            pendingBlank = 0
            val strip = if (topLevel) 0 else frames.last().indent + 4
            val text = if (topLevel) line else stripColumns(line, strip)
            bucket.add(text)
            if (fence.accept(text)) {
                fenceTarget = bucket
                fenceStrip = strip
            }
        }
        for (frame in created) {
            frame.item.details = joinKeep(frame.details)
            frame.item.sequential = frame.ordered ?: frame.item.sequential
        }
        state.file.subtasksTrailer = joinKeep(leading)
        return frames[0].ordered
    }

    private val seedNoise = Net.rx("(?:📅|⏳|✅|➕|🛫|❌)️?[${Net.WS}]*\\p{Nd}{4}-\\p{Nd}{2}-\\p{Nd}{2}|🔺|⏫|🔼|🔽|⏬|🔁|(?<=^|[${Net.WS}])#[\\p{L}\\p{N}_\\-/]+|️")

    /** What identifies a hand-written subtask: its words, without the checkbox, dates, priority, tags, block id, case. */
    private fun titleForSeed(rest: String): String {
        var text = hiddenMeta.matcher(rest).replaceAll("")
        text = trailingBlockId.matcher(text).replaceAll("")
        text = seedNoise.matcher(text).replaceAll(" ")
        return Net.lower(Net.trim(spaces.matcher(text).replaceAll(" ")))
    }

    private fun parseItem(check: Char, input: String, state: ParseState, isPlain: Boolean, seedKey: String): WorkItem {
        val ctx = state.context
        val tz = ctx.zone
        var rest = input
        var meta: JsonObject? = null
        m(hiddenMeta, rest)?.let { hidden ->
            meta = tryParseMeta(g(hidden, "json"))
            if (meta == null) {
                state.file.warnings.add("Ignored unreadable metadata on \"$rest\".")
                state.needsWrite = true
            }
            rest = rest.substring(0, hidden.start())
        }

        var blockId: String? = null
        m(trailingBlockId, rest)?.let { block ->
            blockId = g(block, "bid")
            rest = rest.substring(0, block.start())
        }

        var due: java.time.LocalDate? = null
        var scheduled: java.time.LocalDate? = null
        var doneDate: java.time.LocalDate? = null
        rest = Net.replace(dateToken, rest) { t ->
            val date = VaultText.parseDate(g(t, "d"))
            when (g(t, "e")) {
                "📅" -> due = date
                "⏳" -> scheduled = date
                else -> doneDate = date
            }
            " "
        }

        var priority = Priority.NORMAL
        var emoji: String? = null
        rest = Net.replace(priorityToken, rest) { t ->
            emoji = t.group(1)
            priority = priorityOf(t.group(1)!!)
            " "
        }

        var title = Net.trim(spaces.matcher(rest).replaceAll(" "))
        val trailing = mutableListOf<String>()
        while (true) {
            val tag = m(trailingTag, title) ?: break
            if (!isTag(g(tag, "t"))) break
            trailing.add(0, g(tag, "t"))
            title = Net.trimEnd(title.substring(0, tag.start()))
        }
        val tags = mutableListOf<String>()
        val seenTags = HashSet<String>()
        val inline = inlineTag.matcher(title)
        val found = mutableListOf<String>()
        while (inline.find()) found.add(inline.group("t")!!)
        for (t in found.filter(::isTag) + trailing) if (seenTags.add(Net.ci(t))) tags.add(t)
        if (title.isEmpty()) title = if (trailing.isNotEmpty()) "#" + trailing[0] else "(untitled)"

        val id = state.newId(Net.parseGuid(str(meta, "id")), seedKey)
        val createdMeta = VaultText.parseInstant(str(meta, "created"))
        state.needsWrite = state.needsWrite or (createdMeta == null || isPlain)
        val item = WorkItem(id, title, priority, createdMeta ?: ctx.now, NO_GROUP)

        fun resolve(visible: java.time.LocalDate?, exact: String?, defaultTime: String): Instant? {
            val precise = VaultText.parseInstant(exact)
            if (visible == null) {
                state.needsWrite = state.needsWrite or (precise != null)
                return null
            }
            if (precise != null && VaultText.toLocal(precise, tz).toLocalDate() == visible) return precise
            state.needsWrite = true
            return VaultText.atLocal(visible, defaultTime, tz)
        }

        val md = meta
        item.deadline = resolve(due, str(md, "due"), VaultText.DEFAULT_DUE_TIME)
        item.nextActionAt = resolve(scheduled, str(md, "next"), VaultText.DEFAULT_SCHEDULED_TIME)
        item.afterId = Net.parseGuid(str(md, "after"))
        item.sequential = NetJson.bool(md?.get("seq")) == true
        item.stepDelay = NetJson.long(md?.get("delay"))?.let { Duration.ofMinutes(it) }
        item.icon = Values.icon(str(md, "icon"))
        item.color = Values.color(str(md, "color"))
        item.repeat = Values.repeat(str(md, "repeat"))
        item.plannedAt = VaultText.parseInstant(str(md, "plan"))
        item.plannedMinutes = Values.planMinutes(NetJson.long(md?.get("planMin")))
        item.energy = Values.energy(NetJson.long(md?.get("energy")))
        item.repeatUndo = (md?.get("repeatUndo") as? JsonObject)?.let { undo ->
            RepeatUndo(VaultText.parseInstant(str(undo, "due")), VaultText.parseInstant(str(undo, "next")), VaultText.parseInstant(str(undo, "plan")))
        }
        if (md != null) {
            val unknown = md.filterKeys { it !in subtaskMetaKeys }
            if (unknown.isNotEmpty()) state.file.unknownMeta[id] = JsonObject(LinkedHashMap(unknown))
        }

        item.tags.addAll(tags)
        (md?.get("tags") as? JsonArray)?.let { more ->
            val read = Values.readTags(more.filter { it is JsonPrimitive && it !is JsonNull }.map { NetJson.str(it) })
            item.tags.addAll(read.filter { t -> item.tags.none { Net.equalsCi(it, t) } })
        }
        (md?.get("labels") as? JsonArray)?.let { labels ->
            item.labels.addAll(labels.mapNotNull { NetJson.str(it) })
        }

        var reminderIndex = 0
        for (r in (md?.get("reminders") as? JsonArray) ?: JsonArray(emptyList())) {
            val o = r as? JsonObject ?: continue
            val at = VaultText.parseInstant(str(o, "at")) ?: continue
            val reminderId = state.newId(Net.parseGuid(str(o, "id")), "$seedKey|r${reminderIndex++}")
            item.reminders.add(
                Reminder(reminderId, at, str(o, "msg") ?: "", if (str(o, "kind") == "next-action") ReminderKind.NEXT_ACTION else ReminderKind.MANUAL, VaultText.parseInstant(str(o, "notified")), VaultText.parseInstant(str(o, "dismissed"))),
            )
        }

        if (check != ' ' && check != 'x') state.file.checkChars[id] = check
        if (emoji == "🔼" || emoji == "⏬") state.file.priorityEmoji[id] = emoji!!

        val doneMeta = VaultText.parseInstant(str(md, "done"))
        if (isDoneChar(check)) {
            val d = doneDate
            item.completedAt = if (d != null && (doneMeta == null || VaultText.toLocal(doneMeta, tz).toLocalDate() != d)) VaultText.atLocal(d, VaultText.DEFAULT_DONE_TIME, tz) else doneMeta ?: ctx.now
            state.needsWrite = state.needsWrite or (doneMeta == null)
        } else {
            state.needsWrite = state.needsWrite or (doneMeta != null || doneDate != null)
        }

        val bid = blockId
        if (bid != null) {
            if (!state.byBlockId.containsKey(bid)) {
                state.byBlockId[bid] = item
                if (bid != VaultText.blockId(id)) state.file.blockIds[id] = bid
            }
        } else {
            state.needsWrite = true
        }
        state.byBlockId.putIfAbsent(VaultText.blockId(id), item)
        return item
    }

    private fun parseNotes(lines: List<String>, root: WorkItem, state: ParseState) {
        val tz = state.context.zone
        val preamble = mutableListOf<String>()
        var i = 0
        var pendingBlank = 0
        var index = 0
        while (i < lines.size) {
            val header = m(calloutHeader, lines[i])
            if (header == null) {
                if (Net.trim(lines[i]).isEmpty()) {
                    pendingBlank++
                } else {
                    if (preamble.isNotEmpty()) repeat(pendingBlank) { preamble.add("") }
                    pendingBlank = 0
                    preamble.add(lines[i])
                }
                i++
                continue
            }
            var head = g(header, "head")
            val seedKey = "note|${index++}|$head"
            var meta: JsonObject? = null
            m(hiddenMeta, head)?.let { hidden ->
                meta = tryParseMeta(g(hidden, "json"))
                head = head.substring(0, hidden.start())
            }
            val body = mutableListOf<String>()
            i++
            while (i < lines.size && lines[i].startsWith('>')) {
                body.add(if (lines[i].length > 1 && lines[i][1] == ' ') lines[i].substring(2) else lines[i].substring(1))
                i++
            }
            val parts = head.split('·').map(Net::trim).filter { it.isNotEmpty() }.toMutableList()
            var visible: Instant? = null
            if (parts.isNotEmpty() && noteTime.matcher(parts[0]).find()) {
                visible = VaultText.parseTime(parts[0], tz, "00:00")
                parts.removeAt(0)
            }
            var target = root
            val link = parts.indexOfFirst { blockLink.matcher(it).find() }
            if (link >= 0) {
                val bid = g(m(blockLink, parts[link])!!, "bid")
                target = state.byBlockId[bid] ?: root
                parts.removeAt(link)
            }
            var author = VaultText.parseActor(parts.firstOrNull())
            if (visible == null && parts.isNotEmpty() && author.kind == dev.todotracker.core.ActorKind.USER && author.name != null) {
                // "> [!note] Meeting with Ana" is a heading, not an author.
                body.add(0, parts.joinToString(" · "))
                author = Actor.USER
            }
            var sourceUrl: String? = null
            var sourceTitle: String? = null
            if (body.isNotEmpty()) {
                m(sourceLine, body.last())?.let { source ->
                    sourceUrl = if (has(source, "u1")) g(source, "u1") else g(source, "u2")
                    sourceTitle = if (has(source, "t") && g(source, "t").isNotEmpty()) g(source, "t") else null
                    body.removeAt(body.size - 1)
                }
            }
            val text = joinKeep(body)
            val exact = VaultText.parseInstant(str(meta, "at"))
            val at = if (exact != null && (visible == null || VaultText.localMinute(exact, tz) == VaultText.localMinute(visible, tz))) exact else visible ?: state.context.now
            state.needsWrite = state.needsWrite or (meta == null || exact != at)
            if (text == null) {
                state.needsWrite = true
                continue
            }
            val id = state.newId(Net.parseGuid(str(meta, "id")), seedKey)
            target.notes.add(Note(id, at, text, author, sourceUrl, sourceTitle))
        }
        state.file.notesPreamble = joinKeep(preamble)
    }

    /** `archived:` as people may write it: a time, true/yes (when it was finished), or anything else (not archived). */
    private fun parseArchived(text: String?, completed: Instant?, zone: ZoneId, now: Instant): Instant? {
        val value = Net.trim(text ?: "")
        if (Net.lower(value) == "true" || Net.lower(value) == "yes") return completed ?: now
        return try {
            if (value.isEmpty()) null else VaultText.parseTime(value, zone, VaultText.DEFAULT_DONE_TIME)
        } catch (e: NetFormatException) {
            null
        }
    }

    private fun parseStage(text: String?): Stage = when (Net.lower(Net.trim(text ?: ""))) {
        "inbox" -> Stage.INBOX
        "doing", "in progress", "in-progress", "wip" -> Stage.DOING
        else -> Stage.NEXT
    }

    private fun parseTimeEntries(lines: List<String>, root: WorkItem, state: ParseState) {
        val tz = state.context.zone
        val preamble = mutableListOf<String>()
        val trailer = mutableListOf<String>()
        var seenEntry = false
        var index = 0
        val touched = LinkedHashSet<WorkItem>()

        // A mistyped time (24:00, 09:75, Feb 30) leaves the line as text.
        fun tryTime(day: String, time: String): Instant? = try {
            VaultText.parseTime("$day ${time.padStart(5, '0')}", tz, "00:00")
        } catch (e: NetFormatException) {
            null
        }

        for (line in lines) {
            val match = m(timeLine, line)
            var start = if (match != null) tryTime(g(match, "sd"), g(match, "st")) else null
            var shown: Instant? = null
            if (start != null && !has(match!!, "run")) {
                shown = tryTime(if (has(match, "ed")) g(match, "ed") else g(match, "sd"), g(match, "et"))
                if (shown == null) start = null
            }
            if (start == null) {
                val text = if (seenEntry) trailer else preamble
                if (Net.trim(line).isNotEmpty() || text.isNotEmpty()) text.add(line)
                continue
            }
            seenEntry = true
            match!!
            var rest = g(match, "rest")
            var meta: JsonObject? = null
            m(hiddenMeta, rest)?.let { hidden ->
                meta = tryParseMeta(g(hidden, "json"))
                rest = rest.substring(0, hidden.start())
            }
            // The exact times (hidden) unless the visible ones were changed. An end earlier on the clock than the start,
            // without a date, is past midnight.
            val exactStart = VaultText.parseInstant(str(meta, "start"))
            val exactEnd = VaultText.parseInstant(str(meta, "end"))
            val startAt = if (exactStart != null && VaultText.localMinute(exactStart, tz) == VaultText.localMinute(start, tz)) exactStart else start
            var endAt: Instant? = null
            if (shown != null) {
                val pastMidnight = if (!has(match, "ed") && shown.isBefore(start)) shown.plus(Duration.ofDays(1)) else shown
                endAt = if (exactEnd != null && (VaultText.localMinute(exactEnd, tz) == VaultText.localMinute(shown, tz) || VaultText.localMinute(exactEnd, tz) == VaultText.localMinute(pastMidnight, tz))) exactEnd else pastMidnight
            }
            state.needsWrite = state.needsWrite or (meta == null || exactStart != startAt || exactEnd != endAt)

            var target = root
            var source = TimeSource.MANUAL
            val extras = mutableListOf<String>()
            for (part in rest.split('·').map(Net::trim).filter { it.isNotEmpty() }) {
                val link = m(blockLink, part)
                when {
                    Net.equalsCi(part, "focus") -> source = TimeSource.FOCUS
                    link != null -> target = state.byBlockId[g(link, "bid")] ?: root
                    !durationText.matcher(part).find() -> extras.add(part)
                }
            }
            val id = state.newId(Net.parseGuid(str(meta, "id")), "time|${index++}|${g(match, "sd")} ${g(match, "st")}")
            if (target.timeEntries.none { it.id == id }) target.timeEntries.add(TimeEntry(id, startAt, endAt, source, str(meta, "device")))
            touched.add(target)
            if (extras.isNotEmpty()) state.file.timeExtras[id] = extras.joinToString(" · ")
        }
        // Oldest first (lines merged from two devices can arrive out of order).
        for (item in touched) {
            val sorted = item.timeEntries.sortedBy { it.start }
            item.timeEntries.clear()
            item.timeEntries.addAll(sorted)
        }
        state.file.timePreamble = joinKeep(preamble)
        state.file.timeTrailer = joinKeep(trailer)
    }

    private fun parseAttachments(lines: List<String>, root: WorkItem, state: ParseState) {
        val preamble = mutableListOf<String>()
        for (line in lines) {
            val match = m(listItem, line)
            val attachment = if (match != null && !has(match, "c") && g(match, "ind").isEmpty()) parseAttachmentLine(g(match, "rest"), state) else null
            if (attachment != null) {
                root.attachments.add(attachment)
            } else if (Net.trim(line).isNotEmpty() || preamble.isNotEmpty()) {
                preamble.add(line)
            }
        }
        state.file.attachmentsPreamble = joinKeep(preamble)
    }

    private fun parseAttachmentLine(input: String, state: ParseState): Attachment? {
        var rest = input
        var meta: JsonObject? = null
        m(hiddenMeta, rest)?.let { hidden ->
            meta = tryParseMeta(g(hidden, "json"))
            rest = rest.substring(0, hidden.start())
        }
        rest = Net.trim(rest)
        val name: String
        val path: String
        val md = m(markdownLink, rest)
        val wiki = m(wikiLink, rest)
        if (md != null && md.end() - md.start() == rest.length) {
            val url = g(md, "url").trim('<', '>')
            if (url.contains("://")) return null
            path = resolveRelative(state.context.fileDirectory, unescapeDataString(url))
            name = g(md, "name").ifEmpty { path.substring(path.lastIndexOf('/') + 1) }
        } else if (wiki != null && wiki.end() - wiki.start() == rest.length) {
            path = Net.trim(g(wiki, "target"))
            name = path.substring(path.lastIndexOf('/') + 1)
        } else {
            return null
        }
        val id = state.newId(Net.parseGuid(str(meta, "id")), "attachment|$path")
        val at = VaultText.parseInstant(str(meta, "at"))
        state.needsWrite = state.needsWrite or (at == null)
        val attachment = Attachment(id, name, path, NetJson.long(meta?.get("size")) ?: 0, at ?: state.context.now, VaultText.parseActor(str(meta, "by")))
        state.file.rawLinks[id] = Triple(rest, path, name)
        return attachment
    }

    private fun parseReminder(map: MappingNode, zone: ZoneId, state: ParseState, index: Int): Reminder? {
        val at = VaultText.parseTime(Frontmatter.get(map, "at"), zone, "09:00") ?: return null
        val kind = if (Net.equalsCi((Frontmatter.get(map, "kind") ?: "").replace("-", ""), "nextaction")) ReminderKind.NEXT_ACTION else ReminderKind.MANUAL
        val id = state.newId(Net.parseGuid(Frontmatter.get(map, "id")), "reminder|$index")
        return Reminder(id, at, Frontmatter.get(map, "message") ?: "", kind, VaultText.parseTime(Frontmatter.get(map, "notified"), zone, "00:00"), VaultText.parseTime(Frontmatter.get(map, "dismissed"), zone, "00:00"))
    }

    // ---- Rendering ----

    private fun priorityName(p: Priority) = p.wire.replaceFirstChar { it.uppercaseChar() }

    private fun stageName(s: Stage) = s.wire.replaceFirstChar { it.uppercaseChar() }

    /** What each owned property means, to tell whether its value changed since it was read. */
    private fun signatures(root: WorkItem): Map<String, String> = mapOf(
        "status" to if (root.isDone) "done" else "open",
        "board" to stageName(root.stage),
        "archived" to (root.archivedAt?.let(VaultText::utc) ?: ""),
        "priority" to priorityName(root.priority),
        "icon" to (root.icon ?: ""),
        "color" to (root.color ?: ""),
        "created" to VaultText.utc(root.createdAt),
        "completed" to (root.completedAt?.let(VaultText::utc) ?: ""),
        "due" to (root.deadline?.let(VaultText::utc) ?: ""),
        "scheduled" to (root.nextActionAt?.let(VaultText::utc) ?: ""),
        "after" to (root.afterId?.toString() ?: ""),
        "sequential" to if (root.sequential) "true" else "false",
        "step-delay" to (root.stepDelay?.toMinutes()?.toString() ?: ""),
        "repeat" to (root.repeat?.toString() ?: ""),
        "repeat-undo" to (repeatUndoText(root.repeatUndo) ?: ""),
        "planned" to (root.plannedAt?.let(VaultText::utc) ?: ""),
        "planned-length" to (root.plannedMinutes?.toString() ?: ""),
        "energy" to (root.energy?.toString() ?: ""),
        "tags" to root.tags.joinToString("\u001f"),
        "labels" to root.labels.joinToString("\u001f"),
        "reminders" to root.reminders.joinToString("\u001e") { r -> "${r.id}|${VaultText.utc(r.dueAt)}|${r.message}|${r.kind}|${r.notifiedAt ?: ""}|${r.dismissedAt ?: ""}" },
    )

    private fun renderFrontmatter(sb: StringBuilder, root: WorkItem, zone: ZoneId, previous: ParsedTaskFile?) {
        val idKey = previous?.idKey ?: "id"
        val signatures = signatures(root)
        val emitted = HashSet<String>()
        for ((key, raw) in previous?.frontmatter?.blocks ?: emptyList()) {
            if (key == null) {
                sb.append(raw)
                continue
            }
            val owned = Net.equalsCi(key, idKey) || isOwnedProperty(key)
            if (!owned) {
                sb.append(raw)
            } else if (emitted.add(Net.ci(key))) {
                // An unchanged value is written exactly as the person typed it (date-only due, flow lists, quotes…).
                val before = previous!!.keySignatures[Net.ci(key)]
                val now = signatures.entries.firstOrNull { Net.equalsCi(it.key, key) }?.value
                sb.append(if (before != null && before == now) raw else canonicalProperty(key, idKey, root, zone) ?: "")
            }
        }
        for (key in listOf(idKey) + canonicalKeys) {
            if (emitted.add(Net.ci(key))) sb.append(canonicalProperty(key, idKey, root, zone) ?: "")
        }
    }

    private fun canonicalProperty(key: String, idKey: String, root: WorkItem, zone: ZoneId): String? {
        if (Net.equalsCi(key, idKey)) return "$idKey: ${root.id}\n"
        return when (Net.lower(key)) {
            "status" -> "status: ${if (root.isDone) "done" else "open"}\n"
            "board" -> if (root.stage == Stage.NEXT) null else "board: ${root.stage.wire}\n"
            "archived" -> root.archivedAt?.let { "archived: ${VaultText.utc(it)}\n" }
            "priority" -> if (root.priority == Priority.NORMAL) null else "priority: ${root.priority.wire}\n"
            "icon" -> root.icon?.let { "icon: ${iconScalar(it)}\n" }
            // Quoted: a bare # starts a comment in YAML.
            "color" -> root.color?.let { "color: \"$it\"\n" }
            "created" -> "created: ${VaultText.utc(root.createdAt)}\n"
            "completed" -> root.completedAt?.let { "completed: ${VaultText.utc(it)}\n" }
            "due" -> root.deadline?.let { "due: ${VaultText.localDateTime(it, zone)}\n" }
            "scheduled" -> root.nextActionAt?.let { "scheduled: ${VaultText.localDateTime(it, zone)}\n" }
            "after" -> root.afterId?.let { "after: $it\n" }
            "sequential" -> if (root.sequential) "sequential: true\n" else null
            "step-delay" -> root.stepDelay?.let { "step-delay: ${VaultText.duration(it)}\n" }
            "repeat" -> root.repeat?.let { "repeat: $it\n" }
            "repeat-undo" -> repeatUndoText(root.repeatUndo)?.let { "repeat-undo: $it\n" }
            "planned" -> root.plannedAt?.let { "planned: ${VaultText.localDateTime(it, zone)}\n" }
            "planned-length" -> if (root.plannedAt != null) root.plannedMinutes?.let { "planned-length: ${VaultText.duration(Duration.ofMinutes(it.toLong()))}\n" } else null
            "energy" -> root.energy?.let { "energy: $it\n" }
            "tags" -> yamlList("tags", root.tags)
            "labels" -> yamlList("labels", root.labels)
            "reminders" -> reminders(root.reminders)
            else -> null
        }
    }

    private fun yamlList(key: String, values: List<String>): String? =
        if (values.isEmpty()) null else "$key:\n" + values.joinToString("") { "  - ${VaultText.yamlScalar(it)}\n" }

    // An emoji is written as itself (readable in Obsidian); only text YAML would misread is quoted.
    private fun iconScalar(icon: String): String =
        if ("-?:,[]{}#&*!|>'\"%@`".contains(icon[0]) || icon.contains(": ") || icon.contains(" #") || icon.endsWith(':') || icon != Net.trim(icon)) {
            "\"" + icon.replace("\\", "\\\\").replace("\"", "\\\"") + "\""
        } else {
            icon
        }

    private fun reminders(reminders: List<Reminder>): String? {
        if (reminders.isEmpty()) return null
        val sb = StringBuilder("reminders:\n")
        for (r in reminders) {
            sb.append("  - id: ").append(r.id).append('\n')
            sb.append("    at: ").append(VaultText.utc(r.dueAt)).append('\n')
            sb.append("    message: ").append(NetJson.string(r.message, JsonEscaping.RELAXED)).append('\n')
            sb.append("    kind: ").append(if (r.kind == ReminderKind.NEXT_ACTION) "next-action" else "manual").append('\n')
            r.notifiedAt?.let { sb.append("    notified: ").append(VaultText.utc(it)).append('\n') }
            r.dismissedAt?.let { sb.append("    dismissed: ").append(VaultText.utc(it)).append('\n') }
        }
        return sb.toString()
    }

    private fun renderSection(kind: String, root: WorkItem, context: TaskMarkdownContext, previous: ParsedTaskFile?): String? {
        when (kind) {
            SUBTASKS -> {
                val list = StringBuilder()
                val current = root.children.map { it.id }.toSet()
                for ((i, item) in root.children.withIndex()) {
                    previous?.leading?.get(item.id)?.let { heading ->
                        if (list.isNotEmpty()) list.append('\n')
                        list.append(VaultText.closeFences(heading)).append("\n\n")
                    }
                    renderItem(list, item, i, root.sequential, 0, context, previous)
                }
                // Text that sat above subtasks that are gone now, and text after the list, stays at the end.
                val rest = (previous?.leading?.filter { it.key !in current }?.values?.toList() ?: emptyList()) + listOfNotNull(previous?.subtasksTrailer)
                if (rest.isNotEmpty()) list.append(if (list.isNotEmpty()) "\n" else "").append(VaultText.closeFences(rest.joinToString("\n\n"))).append('\n')
                return if (list.isEmpty()) null else "## ${if (root.sequential) "Steps" else "Subtasks"}\n\n${list.toString().trimEnd('\n')}"
            }
            NOTES -> {
                val notes = root.selfAndDescendants.flatMap { i -> i.notes.map { i to it } }.sortedBy { it.second.at }
                if (notes.isEmpty() && previous?.notesPreamble == null) return null
                return section("Notes", previous?.notesPreamble, notes.joinToString("\n\n") { renderNote(it.first, it.second, root, context.zone, previous) })
            }
            TIME -> {
                val entries = root.selfAndDescendants.flatMap { i -> i.timeEntries.map { i to it } }.sortedBy { it.second.start }
                if (entries.isEmpty() && previous?.timePreamble == null) return null
                val section = section("Time", previous?.timePreamble, entries.joinToString("\n") { renderTimeEntry(it.first, it.second, root, context.zone, previous) })
                return previous?.timeTrailer?.let { "$section\n\n$it" } ?: section
            }
            else -> {
                if (root.attachments.isEmpty() && previous?.attachmentsPreamble == null) return null
                return section("Attachments", previous?.attachmentsPreamble, root.attachments.joinToString("\n") { "- " + renderAttachment(it, context, previous) })
            }
        }
    }

    private fun renderItem(sb: StringBuilder, item: WorkItem, index: Int, ordered: Boolean, depth: Int, ctx: TaskMarkdownContext, previous: ParsedTaskFile?) {
        val indent = "\t".repeat(depth)
        sb.append(indent).append(if (ordered) "${index + 1}." else "-").append(" [").append(checkChar(item, previous)).append("] ")
            .append(itemLine(item, ctx.zone, previous)).append('\n')
        val details = item.details
        if (!details.isNullOrEmpty()) {
            for (line in VaultText.closeFences(details.replace("\r\n", "\n")).split('\n')) {
                sb.append(if (line.isEmpty()) "" else indent + "\t" + line).append('\n')
            }
        }
        for (attachment in item.attachments) sb.append(indent).append("\t- 📎 ").append(renderAttachment(attachment, ctx, previous)).append('\n')
        for ((i, child) in item.children.withIndex()) renderItem(sb, child, i, item.sequential, depth + 1, ctx, previous)
    }

    private fun checkChar(item: WorkItem, previous: ParsedTaskFile?): Char {
        val c = previous?.checkChars?.get(item.id)
        return if (c != null && isDoneChar(c) == item.isDone) c else if (item.isDone) 'x' else ' '
    }

    private fun itemLine(item: WorkItem, zone: ZoneId, previous: ParsedTaskFile?): String {
        val sb = StringBuilder(item.title)
        // Single-word tags are Obsidian tags on the line; a multi-word one can't be, so it goes in the hidden data.
        for (tag in item.tags.filter { !it.contains(' ') }) {
            if (!Net.rx("(^|[${Net.WS}])#" + Pattern.quote(tag) + "([${Net.WS}]|$)", ignoreCase = true).matcher(item.title).find()) sb.append(" #").append(tag)
        }
        val kept = previous?.priorityEmoji?.get(item.id)
        val emoji = if (kept != null && priorityOf(kept) == item.priority) {
            kept
        } else {
            when (item.priority) {
                Priority.CRITICAL -> "🔺"
                Priority.HIGH -> "⏫"
                Priority.LOW -> "🔽"
                else -> null
            }
        }
        if (emoji != null) sb.append(' ').append(emoji)
        item.deadline?.let { sb.append(" 📅 ").append(VaultText.localDate(it, zone)) }
        item.nextActionAt?.let { sb.append(" ⏳ ").append(VaultText.localDate(it, zone)) }
        item.completedAt?.let { sb.append(" ✅ ").append(VaultText.localDate(it, zone)) }
        sb.append(" ^").append(blockIdOf(item, previous))

        val meta = LinkedHashMap<String, JsonElement>()
        meta["id"] = JsonPrimitive(item.id.toString())
        meta["created"] = JsonPrimitive(VaultText.utc(item.createdAt))
        item.completedAt?.let { meta["done"] = JsonPrimitive(VaultText.utc(it)) }
        item.deadline?.let { meta["due"] = JsonPrimitive(VaultText.utc(it)) }
        item.nextActionAt?.let { meta["next"] = JsonPrimitive(VaultText.utc(it)) }
        item.afterId?.let { meta["after"] = JsonPrimitive(it.toString()) }
        if (item.sequential && item.children.isEmpty()) meta["seq"] = JsonPrimitive(true)
        item.stepDelay?.let { meta["delay"] = JsonPrimitive(Math.rint(it.toNanos() / 60e9).toLong()) }
        item.icon?.let { meta["icon"] = JsonPrimitive(it) }
        item.color?.let { meta["color"] = JsonPrimitive(it) }
        item.repeat?.let { meta["repeat"] = JsonPrimitive(it.toString()) }
        item.plannedAt?.let { plan ->
            meta["plan"] = JsonPrimitive(VaultText.utc(plan))
            item.plannedMinutes?.let { meta["planMin"] = JsonPrimitive(it) }
        }
        item.energy?.let { meta["energy"] = JsonPrimitive(it) }
        item.repeatUndo?.let { undo ->
            fun at(i: Instant?): JsonElement = i?.let { JsonPrimitive(VaultText.utc(it)) } ?: JsonNull
            meta["repeatUndo"] = JsonObject(linkedMapOf("due" to at(undo.deadline), "next" to at(undo.nextActionAt), "plan" to at(undo.plannedAt)))
        }
        if (item.labels.isNotEmpty()) meta["labels"] = JsonArray(item.labels.map(::JsonPrimitive))
        val multiWord = item.tags.filter { it.contains(' ') }
        if (multiWord.isNotEmpty()) meta["tags"] = JsonArray(multiWord.map(::JsonPrimitive))
        if (item.reminders.isNotEmpty()) {
            meta["reminders"] = JsonArray(
                item.reminders.map { r ->
                    val o = linkedMapOf<String, JsonElement>(
                        "id" to JsonPrimitive(r.id.toString()),
                        "at" to JsonPrimitive(VaultText.utc(r.dueAt)),
                        "msg" to JsonPrimitive(r.message),
                        "kind" to JsonPrimitive(if (r.kind == ReminderKind.NEXT_ACTION) "next-action" else "manual"),
                    )
                    r.notifiedAt?.let { o["notified"] = JsonPrimitive(VaultText.utc(it)) }
                    r.dismissedAt?.let { o["dismissed"] = JsonPrimitive(VaultText.utc(it)) }
                    JsonObject(o)
                },
            )
        }
        previous?.unknownMeta?.get(item.id)?.forEach { (key, value) -> meta[key] = value }
        sb.append(" %%").append(VaultText.hidden(JsonObject(meta))).append("%%")
        return sb.toString()
    }

    private fun renderNote(item: WorkItem, note: Note, root: WorkItem, zone: ZoneId, previous: ParsedTaskFile?): String {
        val sb = StringBuilder("> [!note] ")
        sb.append(VaultText.localMinute(note.at, zone)).append(" · ").append(VaultText.displayName(note.author))
        if (item !== root) sb.append(" · [[#^").append(blockIdOf(item, previous)).append('|').append(linkAlias.matcher(item.title).replaceAll(" ")).append("]]")
        sb.append(" %%").append(VaultText.hidden(JsonObject(linkedMapOf("id" to JsonPrimitive(note.id.toString()), "at" to JsonPrimitive(VaultText.utc(note.at)))))).append("%%")
        for (line in note.text.replace("\r\n", "\n").split('\n')) sb.append('\n').append(if (line.isEmpty()) ">" else "> $line")
        note.sourceUrl?.let { url ->
            val t = note.sourceTitle
            sb.append("\n> 🔗 ").append(if (!t.isNullOrEmpty()) "[${linkAlias.matcher(t).replaceAll(" ")}]($url)" else "<$url>")
        }
        return sb.toString()
    }

    private fun renderTimeEntry(item: WorkItem, entry: TimeEntry, root: WorkItem, zone: ZoneId, previous: ParsedTaskFile?): String {
        val start = VaultText.toLocal(entry.start, zone)
        val sb = StringBuilder("- ").append(VaultText.localMinute(start))
        val endAt = entry.end
        if (endAt != null) {
            // The end's date is shown when it's another day, or earlier on the clock, so it can't be read as the next day.
            val end = VaultText.toLocal(endAt, zone)
            val sameDay = end.toLocalDate() == start.toLocalDate() && end.hour * 60 + end.minute >= start.hour * 60 + start.minute
            sb.append('–').append(if (sameDay) VaultText.localMinute(end).substring(11) else VaultText.localMinute(end))
            sb.append(" · ").append(VaultText.durationLabel(Duration.between(entry.start, endAt)))
        } else {
            sb.append(" → running")
        }
        if (entry.source == TimeSource.FOCUS) sb.append(" · focus")
        previous?.timeExtras?.get(entry.id)?.let { sb.append(" · ").append(it) }
        if (item !== root) sb.append(" · [[#^").append(blockIdOf(item, previous)).append('|').append(linkAlias.matcher(item.title).replaceAll(" ")).append("]]")
        val meta = linkedMapOf<String, JsonElement>("id" to JsonPrimitive(entry.id.toString()), "start" to JsonPrimitive(VaultText.utc(entry.start)))
        entry.end?.let { meta["end"] = JsonPrimitive(VaultText.utc(it)) }
        entry.device?.takeIf { it.isNotEmpty() }?.let { meta["device"] = JsonPrimitive(it) }
        return sb.append(" %%").append(VaultText.hidden(JsonObject(meta))).append("%%").toString()
    }

    private fun renderAttachment(attachment: Attachment, ctx: TaskMarkdownContext, previous: ParsedTaskFile?): String {
        // The link exactly as written (wiki link, embed, alias/size) as long as it still points to the same file.
        val raw = previous?.rawLinks?.get(attachment.id)
        val link = if (raw != null && raw.second == attachment.path && raw.third == attachment.fileName) {
            raw.first
        } else {
            "[${linkAlias.matcher(attachment.fileName).replaceAll(" ")}](${escapeUrl(relativeTo(ctx.fileDirectory, attachment.path))})"
        }
        val meta = linkedMapOf<String, JsonElement>(
            "id" to JsonPrimitive(attachment.id.toString()),
            "size" to JsonPrimitive(attachment.size),
            "at" to JsonPrimitive(VaultText.utc(attachment.addedAt)),
            "by" to JsonPrimitive(VaultText.displayName(attachment.addedBy)),
        )
        return link + " %%" + VaultText.hidden(JsonObject(meta)) + "%%"
    }

    private fun section(heading: String, preamble: String?, content: String): String =
        "## $heading\n\n" + listOf(preamble, content).filter { !it.isNullOrEmpty() }.joinToString("\n\n")

    private fun blockIdOf(item: WorkItem, previous: ParsedTaskFile?): String = previous?.blockIds?.get(item.id) ?: VaultText.blockId(item.id)

    // ---- Helpers ----

    /** The lines without leading and trailing blank lines, otherwise exactly as written. */
    private fun joinKeep(lines: List<String>): String? {
        val start = lines.indexOfFirst { Net.trim(it).isNotEmpty() }
        if (start < 0) return null
        val end = lines.indexOfLast { Net.trim(it).isNotEmpty() }
        return lines.subList(start, end + 1).joinToString("\n")
    }

    private fun columns(line: String): Int {
        var col = 0
        for (c in line) {
            when (c) {
                '\t' -> col = (col / 4 + 1) * 4
                ' ' -> col++
                else -> return col
            }
        }
        return col
    }

    private fun stripColumns(line: String, columns: Int): String {
        var col = 0
        var i = 0
        while (i < line.length && col < columns && (line[i] == ' ' || line[i] == '\t')) {
            col = if (line[i] == '\t') (col / 4 + 1) * 4 else col + 1
            i++
        }
        return line.substring(i)
    }

    private fun resolveRelative(directory: String, relative: String): String {
        val parts = directory.split('/').filter { it.isNotEmpty() }.toMutableList()
        for (segment in relative.replace('\\', '/').split('/')) {
            if (segment == "..") {
                // Keep ".." that climbs above the vault so the store can refuse it instead of silently clamping.
                if (parts.isNotEmpty() && parts.last() != "..") parts.removeAt(parts.size - 1) else parts.add("..")
            } else if (segment != "." && segment != "") {
                parts.add(segment)
            }
        }
        return parts.joinToString("/")
    }

    private fun relativeTo(directory: String, path: String): String {
        val from = directory.split('/').filter { it.isNotEmpty() }
        val to = path.split('/').filter { it.isNotEmpty() }
        var common = 0
        while (common < from.size && common < to.size - 1 && Net.equalsCi(from[common], to[common])) common++
        return (List(from.size - common) { ".." } + to.drop(common)).joinToString("/")
    }

    private fun escapeUrl(path: String): String = path.replace("%", "%25").replace(" ", "%20").replace("(", "%28").replace(")", "%29")

    /** Uri.UnescapeDataString: %XX sequences that make UTF-8 are decoded; anything else stays as written. */
    private fun unescapeDataString(s: String): String {
        if (!s.contains('%')) return s
        val sb = StringBuilder()
        var i = 0
        while (i < s.length) {
            if (s[i] == '%' && i + 2 < s.length + 0 && isHexPair(s, i + 1)) {
                val start = i
                val bytes = ByteArrayOutputStream()
                while (i + 2 < s.length + 0 && s[i] == '%' && isHexPair(s, i + 1)) {
                    bytes.write(s.substring(i + 1, i + 3).toInt(16))
                    i += 3
                }
                val decoder = Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT)
                try {
                    sb.append(decoder.decode(ByteBuffer.wrap(bytes.toByteArray())))
                } catch (e: CharacterCodingException) {
                    sb.append(s, start, i)
                }
            } else {
                sb.append(s[i])
                i++
            }
        }
        return sb.toString()
    }

    private fun isHexPair(s: String, at: Int): Boolean = at + 1 < s.length && s[at].isHexDigitAscii() && s[at + 1].isHexDigitAscii()

    private fun Char.isHexDigitAscii() = this in '0'..'9' || this in 'a'..'f' || this in 'A'..'F'

    private fun parsePriority(text: String?): Priority = when (Net.lower(Net.trim(text ?: ""))) {
        "critical", "urgent", "highest" -> Priority.CRITICAL
        "high" -> Priority.HIGH
        "low", "lowest" -> Priority.LOW
        else -> Priority.NORMAL
    }

    private fun priorityOf(emoji: String): Priority = when (emoji) {
        "🔺" -> Priority.CRITICAL
        "⏫" -> Priority.HIGH
        "🔽", "⏬" -> Priority.LOW
        else -> Priority.NORMAL
    }

    private fun isDoneChar(c: Char) = c == 'x' || c == 'X' || c == '-'

    private fun isTrue(text: String?) = Net.lower(Net.trim(text ?: "")).let { it == "true" || it == "yes" }

    private fun isTag(tag: String): Boolean = tag.any { !it.isDigit() && it != '/' } && !tag.endsWith('/')

    private fun str(meta: JsonObject?, key: String): String? = NetJson.str(meta?.get(key))

    /** `due 2026-01-02T17:00:00Z, scheduled -` (a dash: none), and `, planned …` when it had a block. */
    private fun repeatUndoText(undo: RepeatUndo?): String? = undo?.let {
        "due ${it.deadline?.let(VaultText::utc) ?: "-"}, scheduled ${it.nextActionAt?.let(VaultText::utc) ?: "-"}" + (it.plannedAt?.let { p -> ", planned ${VaultText.utc(p)}" } ?: "")
    }

    private val repeatUndoPattern = Net.rx("^[${Net.WS}]*due[${Net.WS}]+(?<due>[^${Net.WS},]+)[${Net.WS}]*,[${Net.WS}]*scheduled[${Net.WS}]+(?<next>[^${Net.WS},]+)[${Net.WS}]*(?:,[${Net.WS}]*planned[${Net.WS}]+(?<plan>[^${Net.WS},]+)[${Net.WS}]*)?$")

    private fun parseRepeatUndo(text: String?): RepeatUndo? {
        val match = m(repeatUndoPattern, text ?: return null) ?: return null
        fun read(value: String): Instant? = if (value == "-") null else VaultText.parseInstant(value)
        return RepeatUndo(read(g(match, "due")), read(g(match, "next")), if (has(match, "plan")) read(g(match, "plan")) else null)
    }

    private fun tryParseMeta(json: String): JsonObject? = NetJson.parseOrNull(json) as? JsonObject

    private val listItem = Net.rx("^(?<ind>[ \\t]*)(?<m>[-*+]|\\p{Nd}+[.)])[ \\t]+(?:\\[(?<c>[^\\]])\\][ \\t]?)?(?<rest>.*)$")
    private val hiddenMeta = Net.rx("[${Net.WS}]*%%(?<json>\\{.*\\})%%[${Net.WS}]*$")
    private val trailingBlockId = Net.rx("(^|[${Net.WS}]+)\\^(?<bid>[A-Za-z0-9-]+)[${Net.WS}]*$")
    private val dateToken = Net.rx("(?<e>📅|⏳|✅)️?[${Net.WS}]*(?<d>\\p{Nd}{4}-\\p{Nd}{2}-\\p{Nd}{2})")
    private val priorityToken = Net.rx("(🔺|⏫|🔼|🔽|⏬)️?")
    private val spaces = Net.rx("[${Net.WS}]{2,}")
    private val trailingTag = Net.rx("(^|[${Net.WS}]+)#(?<t>[\\p{L}\\p{N}_\\-/]+)$")
    private val inlineTag = Net.rx("(?<=^|[${Net.WS}])#(?<t>[\\p{L}\\p{N}_\\-/]+)")
    private val calloutHeader = Net.rx("^>[${Net.WS}]*\\[!(?<type>[^\\]]+)\\][+-]?[${Net.WS}]*(?<head>.*)$")
    private val noteTime = Net.rx("^\\p{Nd}{4}-\\p{Nd}{2}-\\p{Nd}{2}[ T]\\p{Nd}{2}:\\p{Nd}{2}")
    private val blockLink = Net.rx("\\[\\[#\\^(?<bid>[^|\\]]+)(\\|[^\\]]*)?\\]\\]")
    private val sourceLine = Net.rx("^🔗[${Net.WS}]*(?:\\[(?<t>[^\\]]*)\\]\\((?<u1>https?://[^)${Net.WS}]+)\\)|<?(?<u2>https?://[^>${Net.WS}]+)>?)[${Net.WS}]*$")
    private val markdownLink = Net.rx("^!?\\[(?<name>[^\\]]*)\\]\\((?<url><[^>]+>|[^)${Net.WS}]+)\\)")
    private val wikiLink = Net.rx("^!?\\[\\[(?<target>[^\\]|#]+)(?:\\|(?<alias>[^\\]]*))?\\]\\]")
    private val linkAlias = Net.rx("[\\[\\]|]")
    private val timeLine = Net.rx(
        "^[-*+] (?<sd>\\p{Nd}{4}-\\p{Nd}{2}-\\p{Nd}{2}) (?<st>\\p{Nd}{1,2}:\\p{Nd}{2})[${Net.WS}]*(?:(?:–|—|-|to)[${Net.WS}]*(?:(?<ed>\\p{Nd}{4}-\\p{Nd}{2}-\\p{Nd}{2})[${Net.WS}]+)?(?<et>\\p{Nd}{1,2}:\\p{Nd}{2})|(?<run>→[${Net.WS}]*(?:running|now)))(?<rest>.*)$",
        ignoreCase = true,
    )
    private val durationText = Net.rx("^\\p{Nd}+[${Net.WS}]*(?:min|m|h)(?:[${Net.WS}]*\\p{Nd}{1,2}[${Net.WS}]*min)?$", ignoreCase = true)

    /** Whether [text] would be read as a task file at [path] (merged text is checked before it's written). */
    fun isValidTask(path: String, text: String, zone: ZoneId, now: Instant): Boolean = try {
        val slash = path.lastIndexOf('/')
        parse(text, Net.stem(path), TaskMarkdownContext(zone, now, if (slash < 0) "" else path.substring(0, slash), path))
        true
    } catch (e: VaultFormatException) {
        false
    }
}
