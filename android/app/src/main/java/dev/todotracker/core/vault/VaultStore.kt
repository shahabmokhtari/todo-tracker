package dev.todotracker.core.vault

import dev.todotracker.core.ActivityEntry
import dev.todotracker.core.Actor
import dev.todotracker.core.ActorKind
import dev.todotracker.core.BoardCodec
import dev.todotracker.core.BoardException
import dev.todotracker.core.Energy
import dev.todotracker.core.LabelDefinition
import dev.todotracker.core.PomodoroPhase
import dev.todotracker.core.PomodoroSettings
import dev.todotracker.core.Reminder
import dev.todotracker.core.ReminderKind
import dev.todotracker.core.TaskBoard
import dev.todotracker.core.TaskGroup
import dev.todotracker.core.WorkItem
import dev.todotracker.core.sync.NetTime
import dev.todotracker.core.sync.SyncEntry
import dev.todotracker.core.sync.SyncHash
import dev.todotracker.core.sync.SyncKeys
import dev.todotracker.core.sync.SyncLimits
import dev.todotracker.core.sync.SyncOp
import dev.todotracker.core.sync.SyncOpKind
import dev.todotracker.core.sync.SyncSnapshot
import dev.todotracker.core.sync.SyncStaleException
import java.io.File
import java.io.IOException
import java.io.RandomAccessFile
import java.nio.file.Files
import java.nio.file.LinkOption
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import java.util.UUID
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive

/** A file the app couldn't read; shown to the person and never overwritten until fixed. */
data class VaultProblem(val path: String, val message: String)

/** A map keyed the way StringComparer.OrdinalIgnoreCase compares (the first spelling of a key is kept). */
internal class CiMap<V> {
    private val map = LinkedHashMap<String, Pair<String, V>>()

    operator fun get(key: String): V? = map[Net.ci(key)]?.second

    operator fun set(key: String, value: V) {
        val k = Net.ci(key)
        map[k] = (map[k]?.first ?: key) to value
    }

    fun remove(key: String): V? = map.remove(Net.ci(key))?.second

    fun containsKey(key: String) = map.containsKey(Net.ci(key))

    val size: Int get() = map.size

    val keys: List<String> get() = map.values.map { it.first }

    val entries: List<Pair<String, V>> get() = map.values.toList()

    fun copy(): CiMap<V> = CiMap<V>().also { c -> for ((k, v) in map.values) c[k] = v }
}

/**
 * The phone's tasks folder (a slim port of TodoTracker.Core.Vault.VaultBoardStore): one markdown file per top-level
 * task in group folders, `.todo-tracker/config.json` (synced), `state.json` (this device) and `activity.jsonl`.
 *
 * * The files are the truth: the board is read from them, and reading never writes a task file.
 * * A change rewrites only the files of the tasks it touched, each checked first against what was last read; a change
 *   that fails part way is undone. Deleted tasks go to `.todo-tracker/trash`, never away.
 * * One writer at a time: every call is serialized on this object (widgets and workers must go through it).
 * * [readSync] and [applySync] are what [dev.todotracker.core.sync.SyncEngine] needs.
 */
class VaultStore(
    /** The tasks folder (e.g. `filesDir/vault`), never a shared one: sync meets elsewhere. */
    val root: File,
    val zone: ZoneId = ZoneId.systemDefault(),
    private val clock: () -> Instant = Instant::now,
) {
    private val lock = Any()
    private var caches = Caches()
    private var board: TaskBoard = TaskBoard(seedDefaultGroups = false)
    private var dormantGroups: List<ConfigGroup> = emptyList()
    private var configText = ""
    private var stateText = ""
    private var activityLines = mutableListOf<ActivityEntry>()
    private var activityBytes = -1L
    private var loadedOnce = false
    private var forceContent = true
    private val problemList = mutableListOf<VaultProblem>()
    private val syncHashes = HashMap<String, Pair<Pair<Long, Long>, String>>()
    private val pendingStamps = CiMap<Pair<Long, Long>>()

    private val now: Instant get() = clock()
    private val meta: File get() = File(root, VaultFiles.META_FOLDER)

    /** Files that couldn't be read (their last good version stays and they are never overwritten). */
    val problems: List<VaultProblem>
        get() = synchronized(lock) {
            refreshIfNeeded()
            problemList.toList()
        }

    companion object {
        /**
         * Opens (creating when new) the tasks folder in [root]. A new folder gets the default tabs (Work, Personal),
         * with ids from their names so two new vaults meet with the same tabs; with a [legacyBoard] (this phone's
         * board.json, read by [BoardCodec]) a new folder is made from that board instead.
         */
        fun open(root: File, zone: ZoneId = ZoneId.systemDefault(), clock: () -> Instant = Instant::now, legacyBoard: TaskBoard? = null): VaultStore =
            VaultStore(root, zone, clock).also { it.initialize(legacyBoard) }

        /** The ids of a new vault's default tabs (and of any folder not in config.json): StableGuid("group|" + FOLDER). */
        fun groupId(folder: String): UUID = Net.stableGuid("group|" + Net.upper(folder))

        /** Trash is emptied this many days after it was trashed (the time in the trash file's name). */
        const val TRASH_DAYS = 30L
    }

    // ---- Startup ----

    private fun initialize(legacy: TaskBoard?) = synchronized(lock) {
        root.mkdirs()
        val fresh = !File(meta, "config.json").exists() && enumerateTaskFiles().isEmpty()
        if (fresh && legacy != null) {
            migrate(legacy)
        } else if (fresh) {
            val seeded = TaskBoard()
            val groups = seeded.groups.map { ConfigGroup(groupId(it.name), it.name, it.color) }
            for (g in seeded.groups) File(root, g.name).mkdirs()
            VaultFiles.writeAtomic(File(meta, "config.json"), configJson(groups, emptyList(), emptyList(), null))
        }
        purgeOldTrash()
        reload()
    }

    private fun migrate(legacy: TaskBoard) {
        board = legacy
        board.zone = zone
        val folders = LinkedHashMap<UUID, String>()
        for (group in board.groups) {
            val folder = VaultFiles.uniqueStem(VaultFiles.safeFolderName(group.name)) { f -> folders.values.any { Net.equalsCi(it, f) } }
            folders[group.id] = folder
            File(root, folder).mkdirs()
        }
        caches = Caches(groupFolders = folders)
        loadedOnce = true
        persist()
        loadedOnce = false
    }

    /**
     * Brings the tasks an older version of the app kept on its own (board.json) in beside what's here, the first time
     * this phone joins sync (after the first pull): a top-level task whose id, or any of its subtasks' ids, is already
     * here is skipped, never replaced; the others go to the tab of the same name, else the first tab. Returns how many came in.
     * All or nothing, like any change: a failure part way puts every file back. [legacy] itself is left as it is.
     */
    fun importBeside(legacy: TaskBoard): Int {
        val json = BoardCodec.encode(legacy)
        return update { b ->
            // A fresh copy each attempt (a change that's retried starts again from the board read from disk).
            val copy = BoardCodec.decode(json)
            var count = 0
            for (top in copy.items.toList()) {
                if (hasAnyId(b, top)) continue
                val tab = copy.groups.firstOrNull { it.id == top.groupId }?.name
                val group = b.groups.firstOrNull { Net.equalsCi(it.name, tab) } ?: b.groups[0]
                attachLoaded(b, top, group.id)
                count++
            }
            count
        }
    }

    // ---- Reading and changing ----

    /** Runs [read] on the board as the files are now (re-read when they changed). */
    fun <T> read(read: (TaskBoard) -> T): T = synchronized(lock) {
        refreshIfNeeded()
        read(board)
    }

    /**
     * Applies a change and saves the files it touched (all or nothing). The change must say who made it (the board's
     * methods take an Actor). A file changed underneath in between is re-read and the change applied again.
     */
    fun <T> update(mutate: (TaskBoard) -> T): T = synchronized(lock) { updateLocked(mutate) }

    private fun <T> updateLocked(mutate: (TaskBoard) -> T): T {
        var attempt = 1
        while (true) {
            refreshIfNeeded()
            val snapshot = BoardCodec.encode(board)
            try {
                val result = mutate(board)
                persist()
                return result
            } catch (e: VaultConflictException) {
                board = decode(snapshot)
                forceContent = true
                if (attempt++ >= 3) throw e
            } catch (e: Exception) {
                board = decode(snapshot)
                forceContent = true
                throw e
            }
        }
    }

    private fun decode(json: String): TaskBoard = BoardCodec.decode(json).also { it.zone = zone }

    /** Vault-relative path of the file holding the task (its top-level task's file). */
    fun pathOf(id: UUID): String? = synchronized(lock) {
        val top = board.find(id)?.root?.id ?: id
        caches.paths[top]
    }

    /** Something may have changed on disk: the next read re-reads every file's content. */
    fun markDirty() = synchronized(lock) { forceContent = true }

    private fun refreshIfNeeded() {
        if (!loadedOnce || forceContent || diskDiffers()) reload()
    }

    private fun diskDiffers(): Boolean {
        val c = caches
        var seen = 0
        for (file in enumerateTaskFiles()) {
            seen++
            val state = c.files[relative(file)]
            if (state == null || state.stamp != stamp(file)) return true
        }
        val folders = topFolders().map(Net::ci).toSet()
        if (seen != c.files.size || folders != c.groupFolders.values.map(Net::ci).toSet()) return true
        return readOrEmpty(File(meta, "config.json")) != configText || readOrEmpty(File(meta, "state.json")) != stateText ||
            fileLength(File(meta, "activity.jsonl")) != activityBytes
    }

    /** Rebuilds the board from disk. Returns whether anything changed. */
    private fun reload(): Boolean {
        val force = forceContent
        forceContent = false
        loadedOnce = true
        val before = BoardCodec.encode(board)
        val oldBoard = board
        val oldCaches = caches
        val config = readConfig()
        val folders = topFolders()

        val files = CiMap<FileState>()
        val parsedByFolder = CiMap<MutableList<Pair<String, ParsedTaskFile>>>()
        for (f in folders) parsedByFolder[f] = mutableListOf()
        val problems = mutableListOf<VaultProblem>()
        for (folder in folders) {
            for (file in enumerateTaskFiles(File(root, folder))) {
                val rel = relative(file)
                val state = readFile(file, rel, oldCaches, force)
                files[rel] = state
                state.error?.let { problems.add(VaultProblem(rel, it)) }
                state.good?.let { parsedByFolder[folder]!!.add(rel to parseText(it, rel)) }
            }
        }

        // Groups: config order; a renamed folder keeps its group (recognized by the tasks inside); new folders append.
        // Config groups whose folder is missing stay dormant (sync may bring the folder later).
        val groups = mutableListOf<Triple<UUID, String, String?>>()
        val claimed = HashSet<String>()
        val dormant = mutableListOf<ConfigGroup>()
        for (g in config.groups) {
            var folder = folders.firstOrNull { Net.equalsCi(it, g.folder) && Net.ci(it) !in claimed }
            if (folder == null && groups.none { it.first == g.id }) {
                folder = folders.firstOrNull { f ->
                    Net.ci(f) !in claimed && config.groups.none { Net.equalsCi(it.folder, f) } &&
                        parsedByFolder[f]!!.any { p -> oldBoard.find(p.second.root.id)?.groupId == g.id }
                }
            }
            if (folder == null || groups.any { it.first == g.id }) {
                if (folder == null && groups.none { it.first == g.id }) dormant.add(g)
                continue
            }
            claimed.add(Net.ci(folder))
            groups.add(Triple(g.id, folder, g.color))
        }
        for (folder in folders.filter { Net.ci(it) !in claimed }.sortedWith(Net.ordinalIgnoreCase)) {
            claimed.add(Net.ci(folder))
            groups.add(Triple(groupId(folder), folder, null))
        }
        if (groups.isEmpty()) {
            File(root, "Inbox").mkdirs()
            groups.add(Triple(groupId("Inbox"), "Inbox", null))
            parsedByFolder["Inbox"] = mutableListOf()
        }

        val next = TaskBoard(seedDefaultGroups = false)
        next.zone = zone
        val groupFolders = LinkedHashMap<UUID, String>()
        for ((id, folder, color) in groups) {
            next.groups.add(TaskGroup(id, folder, color))
            groupFolders[id] = folder
        }
        for (label in config.labels) if (next.findLabel(label.first) == null) next.labels.add(LabelDefinition(label.first, label.second))
        next.energyPerDay = config.energyPerDay?.takeIf { it in 1..Energy.MAX_PER_DAY }

        // Tasks in the remembered order. A copied file's ids are taken: it gets ids derived from its path (stable,
        // without rewriting it); the file that had them before keeps them.
        val order = HashMap<UUID, Int>()
        config.order.forEachIndexed { i, id -> order[id] = i }
        val paths = LinkedHashMap<UUID, String>()
        val fileIds = CiMap<Set<UUID>>()
        val layouts = CiMap<ParsedTaskFile>()
        val candidates = groups.flatMap { g -> parsedByFolder[g.second]!!.map { Triple(g.first, it.first, it.second) } }
            .sortedWith(compareByDescending<Triple<UUID, String, ParsedTaskFile>> { c -> oldCaches.paths[c.third.root.id]?.let { Net.equalsCi(it, c.second) } == true }.thenBy(Net.ordinalIgnoreCase) { it.second })
        for ((groupId, rel, parsed) in candidates) {
            val top = parsed.root
            if (hasAnyId(next, top)) {
                for (item in top.selfAndDescendants) {
                    if (item === top || next.find(item.id) != null) item.id = Net.stableGuid("$rel|copy|${item.id}")
                }
                parsed.blockIds.clear()
            }
            attachLoaded(next, top, groupId)
            paths[top.id] = rel
            fileIds[rel] = top.selfAndDescendants.map { it.id }.toSet()
            layouts[rel] = parsed
        }
        val sorted = next.items.withIndex().sortedWith(compareBy<IndexedValue<WorkItem>> { order[it.value.id] ?: Int.MAX_VALUE }.thenBy { it.index }).map { it.value }
        next.items.clear()
        next.items.addAll(sorted)

        loadActivity(next)
        loadState(next)

        val blocked = HashSet<UUID>()
        for ((rel, state) in files.entries) {
            if (state.error != null) paths.entries.firstOrNull { Net.equalsCi(it.value, rel) }?.let { blocked.add(it.key) }
        }

        board = next
        dormantGroups = dormant
        caches = Caches(files, paths, groupFolders, fileIds, layouts, next.items.associate { it.id to signature(it) }.toMutableMap(), blocked)
        problemList.clear()
        problemList.addAll(problems)

        // Tasks waiting for one finished or deleted elsewhere (by sync, say) come back with a reminder.
        val changed = if (releaseWaiters(board, now, caches.blocked)) persist() else writeMetadataIfChanged(null)
        return changed or (BoardCodec.encode(board) != before)
    }

    /** TaskBoard.ReleaseWaiters after a load: a task waiting for one that's done comes back (files that can't be saved now wait). */
    private fun releaseWaiters(b: TaskBoard, at: Instant, skipRoots: Set<UUID>): Boolean {
        var released = false
        for (waiter in b.allItems.filter { w -> w.afterId?.let { b.find(it)?.isDone } == true && w.root.id !in skipRoots }) {
            val other = b.find(waiter.afterId!!)!!
            val why = "\"${other.title}\" is done"
            waiter.afterId = null
            released = true
            if (!waiter.isDone) {
                waiter.reminders.add(Reminder(UUID.randomUUID(), at, "$why: back to \"${waiter.title}\"", ReminderKind.NEXT_ACTION))
                b.activity.add(ActivityEntry(at, waiter.id, "scheduled", "Back: $why", Actor.SYSTEM))
            }
        }
        return released
    }

    private fun readFile(file: File, rel: String, old: Caches, force: Boolean): FileState {
        val stamp = stamp(file)
        val cached = old.files[rel]
        if (cached != null && cached.stamp == stamp && !force) return cached
        val text = VaultFiles.readText(file)
        if (cached != null && cached.text == text) return cached.copy(stamp = stamp)
        return try {
            parseText(text, rel)
            FileState(text, text, stamp, null)
        } catch (e: VaultFormatException) {
            FileState(text, cached?.good, stamp, e.message)
        }
    }

    private fun parseText(text: String, rel: String): ParsedTaskFile = TaskMarkdown.parse(text, Net.stem(rel), context(rel))

    private fun context(rel: String): TaskMarkdownContext {
        val slash = rel.lastIndexOf('/')
        return TaskMarkdownContext(zone, now, if (slash < 0) "" else rel.substring(0, slash), rel)
    }

    private fun signature(top: WorkItem): String = "${top.groupId}|${NetJson.write(BoardCodec.encodeItem(top))}"

    // ---- Writing ----

    /** Saves what changed: plans every file operation, checks each touched file is still what was read, then commits with an undo log. */
    private fun persist(): Boolean {
        val plan = plan()
        if (plan.operations.isEmpty()) return writeMetadataIfChanged(null)
        for (rel in plan.touched) {
            val full = full(rel)
            val known = caches.files[rel]
            if (if (known == null) full.exists() else !full.exists() || VaultFiles.readText(full) != known.text) throw VaultConflictException(rel)
        }
        val undo = ArrayDeque<() -> Unit>()
        val previous = caches
        try {
            for (op in plan.operations) op(undo)
            caches = plan.caches
            writeMetadataIfChanged(undo)
        } catch (e: Exception) {
            caches = previous
            while (undo.isNotEmpty()) {
                try {
                    undo.removeLast()()
                } catch (ignored: Exception) {
                    // Best effort: keep undoing the rest.
                }
            }
            pendingStamps.entries.map { it.first }.forEach { pendingStamps.remove(it) }
            throw e
        }
        for ((rel, stamp) in pendingStamps.entries) caches.files[rel]?.let { caches.files[rel] = it.copy(stamp = stamp) }
        pendingStamps.entries.map { it.first }.forEach { pendingStamps.remove(it) }
        return true
    }

    private class SavePlan(val caches: Caches) {
        val operations = mutableListOf<(ArrayDeque<() -> Unit>) -> Unit>()
        val touched = LinkedHashSet<String>()
    }

    private fun plan(): SavePlan {
        val current = caches
        val next = current.copy()
        val plan = SavePlan(next)
        val folders = next.groupFolders

        // Groups: create, rename (moving files and links with them), or retire their folders.
        val renames = mutableListOf<Pair<String, String>>()
        for (group in board.groups) {
            val wanted = VaultFiles.safeFolderName(group.name)
            val folder = folders[group.id]
            if (folder != null) {
                if (VaultFiles.safeFolderName(folder) != wanted) {
                    val target = VaultFiles.uniqueStem(wanted) { f -> (folders.values.any { Net.equalsCi(it, f) } || File(root, f).isDirectory) && !Net.equalsCi(f, folder) }
                    renames.add(folder to target)
                    folders[group.id] = target
                }
            } else {
                val target = VaultFiles.uniqueStem(wanted) { f -> folders.values.any { Net.equalsCi(it, f) } || File(root, f).isDirectory }
                folders[group.id] = target
                plan.operations.add { undo -> createFolder(target, undo) }
            }
        }
        val retired = folders.entries.filter { g -> board.groups.none { it.id == g.key } }.map { it.key to it.value }
        val relocations = mutableListOf<Pair<String, String>>()
        for ((id, folder) in retired) {
            folders.remove(id)
            relocations.add(folder to VaultFiles.uniqueStem("${VaultFiles.ATTACHMENTS_FOLDER}/$folder") { full(it).isDirectory })
        }
        for ((from, to) in renames) remapPrefix(next, from, to, includeTasks = true)
        for ((from, to) in relocations) remapPrefix(next, from, to, includeTasks = false)
        for ((from, to) in renames) plan.operations.add(0) { undo -> moveFolder(from, to, undo) }

        // Tasks: write the ones that changed (or moved); a different group keeps the file name and subfolder.
        val writes = mutableListOf<Triple<String, String, Boolean>>()
        val reserved = HashSet(next.paths.values.map(Net::ci))
        for (top in board.items) {
            val folder = folders[top.groupId]!!
            val signature = signature(top)
            var rel = next.paths[top.id]
            val known = rel != null
            if (rel == null) {
                rel = newFilePath(folder, VaultFiles.safeName(top.title), reserved)
                reserved.add(Net.ci(rel))
            } else if (!Net.equalsCi(rel.split('/')[0], folder)) {
                val source = rel
                val name = Net.stem(rel)
                val dir = rel.substring(0, rel.lastIndexOf('/'))
                val sub = if (dir.contains('/')) dir.substring(dir.indexOf('/') + 1) + "/" else ""
                rel = newFilePath(if (sub.isEmpty()) folder else folder + "/" + sub.trimEnd('/'), name, reserved)
                reserved.add(Net.ci(rel))
                moveEntry(next, source, rel)
                val target = rel
                plan.touched.add(source)
                plan.operations.add { undo -> moveTaskFile(source, target, undo) }
            }
            if (top.id in current.blocked) {
                if (current.signatures[top.id] != signature) {
                    throw BoardException("\"$rel\" can't be read right now (${problemList.firstOrNull { it.path == rel }?.message}). Fix the file, then try again.")
                }
                continue
            }
            next.paths[top.id] = rel
            if (known && current.signatures[top.id] == signature && rel == current.paths[top.id]) continue
            val text = TaskMarkdown.render(top, context(rel), layout(next, rel))
            val ids = top.selfAndDescendants.map { it.id }.toSet()
            val had = next.fileIds[rel]
            val gained = had == null || !had.containsAll(ids)
            next.signatures[top.id] = signature
            next.fileIds[rel] = ids
            if (next.files[rel]?.text != text) writes.add(Triple(rel, text, gained))
        }

        // Files that gain tasks are written before files that lose them: a crash in between duplicates, never loses.
        for ((rel, text, _) in writes.sortedByDescending { it.third }) {
            plan.touched.add(rel)
            next.files[rel] = FileState(text, text, 0L to 0L, null)
            next.layouts.remove(rel)
            plan.operations.add { undo -> writeTaskFile(rel, text, undo) }
        }

        // Top-level tasks that are gone (deleted, or now a subtask) go to the trash.
        val live = board.items.map { it.id }.toSet()
        for ((id, rel) in next.paths.entries.filter { it.key !in live }.map { it.key to it.value }) {
            if (id in current.blocked) throw BoardException("\"$rel\" can't be read right now, so it can't be removed. Fix the file first.")
            if (board.find(id) != null && hasFileOnlyContent(rel)) {
                throw BoardException("\"$rel\" has content that only a top-level task can keep (a rich version, extra properties, or sections). Move that content first.")
            }
            next.paths.remove(id)
            next.signatures.remove(id)
            next.fileIds.remove(rel)
            next.files.remove(rel)
            next.layouts.remove(rel)
            plan.touched.add(rel)
            plan.operations.add { undo -> trashTaskFile(rel, undo) }
        }
        for ((from, to) in relocations) plan.operations.add { undo -> retireFolder(from, to, undo) }
        return plan
    }

    private fun remapPrefix(c: Caches, from: String, to: String, includeTasks: Boolean) {
        fun remap(path: String): String? = if (Net.startsWithCi(path, "$from/")) to + path.substring(from.length) else null
        if (includeTasks) {
            for ((id, rel) in c.paths.entries.map { it.key to it.value }) {
                remap(rel)?.let { moved ->
                    moveEntry(c, rel, moved)
                    c.paths[id] = moved
                }
            }
        }
        for (item in board.allItems) for (a in item.attachments) remap(a.path)?.let { a.path = it }
    }

    private fun moveEntry(c: Caches, from: String, to: String) {
        c.files.remove(from)?.let { c.files[to] = it }
        c.fileIds.remove(from)?.let { c.fileIds[to] = it }
        c.layouts.remove(from)?.let { c.layouts[to] = it }
    }

    private fun layout(c: Caches, rel: String): ParsedTaskFile? {
        c.layouts[rel]?.let { return it }
        val good = c.files[rel]?.good ?: return null
        return try {
            parseText(good, rel).also { c.layouts[rel] = it }
        } catch (e: VaultFormatException) {
            null
        }
    }

    /** Content that would be lost if the file stopped being a top-level task. */
    private fun hasFileOnlyContent(rel: String): Boolean {
        if (htmlOf(full(rel)).exists()) return true
        val l = layout(caches, rel) ?: return false
        return l.layout.any { it.first == null } || l.leading.isNotEmpty() || l.subtasksTrailer != null || l.notesPreamble != null ||
            l.attachmentsPreamble != null || l.timePreamble != null ||
            l.frontmatter.blocks.any { it.first == null || !(Net.equalsCi(it.first, l.idKey) || TaskMarkdown.isOwnedProperty(it.first!!)) }
    }

    private fun newFilePath(folder: String, stem: String, reserved: Set<String>): String {
        val unique = VaultFiles.uniqueStem(stem) { s ->
            val rel = "$folder/$s.md"
            Net.ci(rel) in reserved || full(rel).exists() || full("$folder/$s.html").exists()
        }
        return "$folder/$unique.md"
    }

    private fun createFolder(folder: String, undo: ArrayDeque<() -> Unit>) {
        val dir = File(root, folder)
        if (!dir.isDirectory) {
            if (!dir.mkdirs()) throw IOException("Couldn't make the folder $folder.")
            undo.addLast { dir.delete() }
        }
    }

    private fun moveFolder(from: String, to: String, undo: ArrayDeque<() -> Unit>) {
        val source = File(root, from)
        val target = File(root, to)
        if (!source.isDirectory) {
            createFolder(to, undo)
            return
        }
        rename(source, target)
        undo.addLast { rename(target, source) }
    }

    /** A deleted group's folder: whatever is left in it (images, other notes) moves under _attachments. */
    private fun retireFolder(from: String, to: String, undo: ArrayDeque<() -> Unit>) {
        val source = File(root, from)
        if (!source.isDirectory) return
        if (source.list().isNullOrEmpty()) {
            source.delete()
            undo.addLast { source.mkdirs() }
            return
        }
        val target = full(to)
        target.parentFile?.mkdirs()
        rename(source, target)
        undo.addLast { rename(target, source) }
    }

    private fun writeTaskFile(rel: String, text: String, undo: ArrayDeque<() -> Unit>) {
        val file = full(rel)
        val previous = if (file.exists()) file.readBytes() else null
        VaultFiles.writeAtomic(file, text, replace = previous != null)
        undo.addLast { if (previous == null) file.delete() else VaultFiles.writeAtomic(file, previous) }
        pendingStamps[rel] = stamp(file)
    }

    private fun moveTaskFile(from: String, to: String, undo: ArrayDeque<() -> Unit>) {
        val source = full(from)
        val target = full(to)
        target.parentFile?.mkdirs()
        if (source.exists()) {
            rename(source, target)
            undo.addLast { rename(target, source) }
        }
        val html = htmlOf(source)
        if (html.exists()) {
            val htmlTarget = htmlOf(target)
            rename(html, htmlTarget)
            undo.addLast { rename(htmlTarget, html) }
        }
        pendingStamps[to] = stamp(target)
    }

    private fun trashTaskFile(rel: String, undo: ArrayDeque<() -> Unit>) {
        val file = full(rel)
        for (f in listOf(file, htmlOf(file))) {
            if (f.exists()) {
                val trash = trashPath(f.name)
                rename(f, trash)
                undo.addLast { rename(trash, f) }
            }
        }
    }

    private val trashStamp = DateTimeFormatter.ofPattern("uuuuMMdd-HHmmss").withZone(ZoneOffset.UTC)

    private fun trashPath(name: String): File {
        val trash = File(meta, "trash")
        trash.mkdirs()
        val stamp = trashStamp.format(now)
        var candidate = File(trash, "$stamp $name")
        var n = 2
        while (candidate.exists()) candidate = File(trash, "$stamp ${n++} $name")
        return candidate
    }

    /**
     * Empties trash older than 30 days. A file's age is the time in its name (`yyyyMMdd-HHmmss <name>`, UTC: when it was
     * trashed), never its modification time (a move keeps that: a task last edited long ago would go at once). An
     * entry whose name has no such time is never purged.
     */
    private fun purgeOldTrash() {
        val cutoff = now.minus(Duration.ofDays(TRASH_DAYS))
        for (entry in File(meta, "trash").listFiles() ?: return) {
            val trashed = trashedAt(entry.name) ?: continue
            if (trashed.isBefore(cutoff)) {
                try {
                    entry.deleteRecursively()
                } catch (ignored: IOException) {
                    // Next start.
                }
            }
        }
    }

    private val trashName = Regex("^([0-9]{8}-[0-9]{6}) ")

    /** When a trash entry was trashed (the time its name starts with), or null when its name doesn't say. */
    private fun trashedAt(name: String): Instant? {
        val stamp = trashName.find(name)?.groupValues?.get(1) ?: return null
        return try {
            java.time.LocalDateTime.parse(stamp, DateTimeFormatter.ofPattern("uuuuMMdd-HHmmss").withResolverStyle(java.time.format.ResolverStyle.STRICT)).toInstant(ZoneOffset.UTC)
        } catch (e: java.time.DateTimeException) {
            null
        }
    }

    /** A rename that never replaces another file (and works for a change of letter case on case-insensitive disks). */
    private fun rename(from: File, to: File) {
        if (from.path != to.path && Net.equalsCi(from.path, to.path)) {
            val temp = File(to.parentFile, to.name + ".rename-" + UUID.randomUUID().toString().substring(0, 6))
            Files.move(from.toPath(), temp.toPath())
            Files.move(temp.toPath(), to.toPath())
        } else {
            Files.move(from.toPath(), to.toPath())
        }
    }

    // ---- Settings, timer state, activity ----

    internal data class ConfigGroup(val id: UUID, val folder: String, val color: String?)

    private class Config(val groups: List<ConfigGroup>, val labels: List<Pair<String, String>>, val order: List<UUID>, val energyPerDay: Int?)

    private fun readConfig(): Config {
        val text = readOrEmpty(File(meta, "config.json"))
        configText = text
        val empty = Config(emptyList(), emptyList(), emptyList(), null)
        if (text.isEmpty()) return empty
        // Like System.Text.Json: anything that isn't the right shape makes the whole file unreadable (the defaults).
        return try {
            val doc = NetJson.parse(text) as? JsonObject ?: return empty
            fun prop(o: JsonObject, name: String): JsonElement? = o.entries.firstOrNull { Net.equalsCi(it.key, name) }?.value?.takeIf { it !is kotlinx.serialization.json.JsonNull }
            fun guid(e: JsonElement?): UUID = Net.parseGuid(NetJson.str(e) ?: throw IllegalStateException()) ?: throw IllegalStateException()
            val groups = (prop(doc, "groups") as JsonArray?)?.map { g ->
                val o = g as JsonObject
                ConfigGroup(guid(prop(o, "id")), NetJson.str(prop(o, "folder")) ?: "", prop(o, "color")?.let { NetJson.str(it) ?: throw IllegalStateException() })
            } ?: emptyList()
            val labels = (prop(doc, "labels") as JsonArray?)?.map { l ->
                val o = l as JsonObject
                (NetJson.str(prop(o, "name")) ?: "") to (NetJson.str(prop(o, "color")) ?: TaskBoard.LABEL_PALETTE[0])
            } ?: emptyList()
            val order = (prop(doc, "order") as JsonArray?)?.map(::guid) ?: emptyList()
            val energy = prop(doc, "energyPerDay")?.let { NetJson.long(it)?.toInt() ?: throw IllegalStateException() }
            Config(groups, labels, order, energy)
        } catch (e: RuntimeException) {
            empty
        }
    }

    private fun configJson(groups: List<ConfigGroup>, labels: List<LabelDefinition>, order: List<UUID>, energyPerDay: Int?): String {
        val doc = LinkedHashMap<String, JsonElement>()
        doc["schemaVersion"] = JsonPrimitive(2)
        doc["groups"] = JsonArray(
            groups.map { g ->
                val o = linkedMapOf<String, JsonElement>("id" to JsonPrimitive(g.id.toString()), "folder" to JsonPrimitive(g.folder))
                g.color?.let { o["color"] = JsonPrimitive(it) }
                JsonObject(o)
            },
        )
        doc["labels"] = JsonArray(labels.map { JsonObject(linkedMapOf("name" to JsonPrimitive(it.name), "color" to JsonPrimitive(it.color))) })
        doc["order"] = JsonArray(order.map { JsonPrimitive(it.toString()) })
        energyPerDay?.let { doc["energyPerDay"] = JsonPrimitive(it) }
        return NetJson.write(JsonObject(doc), JsonEscaping.DEFAULT, indented = true) + "\n"
    }

    private fun buildConfigText(): String {
        val folders = caches.groupFolders
        val groups = board.groups.filter { folders.containsKey(it.id) }.map { ConfigGroup(it.id, folders[it.id]!!, it.color) } + dormantGroups
        return configJson(groups, board.labels, board.items.map { it.id }, board.energyPerDay)
    }

    private fun writeMetadataIfChanged(undo: ArrayDeque<() -> Unit>?): Boolean {
        val config = buildConfigText()
        var changed = writeIfChanged(File(meta, "config.json"), config, configText, undo).also { if (it) configText = config }
        val state = stateText()
        if (state != stateText) {
            writeIfChanged(File(meta, "state.json"), state, stateText, undo)
            stateText = state
            changed = true
        }
        return changed or appendActivity(undo)
    }

    private fun writeIfChanged(file: File, text: String, known: String, undo: ArrayDeque<() -> Unit>?): Boolean {
        if (text == known) return false
        VaultFiles.writeAtomic(file, text)
        undo?.addLast { if (known.isEmpty()) file.delete() else VaultFiles.writeAtomic(file, known) }
        return true
    }

    private fun loadState(b: TaskBoard) {
        val text = readOrEmpty(File(meta, "state.json"))
        stateText = text
        if (text.isEmpty()) return
        try {
            val o = NetJson.parse(text) as? JsonObject ?: return
            fun prop(name: String): JsonElement? = o.entries.firstOrNull { Net.equalsCi(it.key, name) }?.value?.takeIf { it !is kotlinx.serialization.json.JsonNull }
            (prop("settings") as? JsonObject)?.let { s ->
                fun minutes(name: String, default: Int) = s.entries.firstOrNull { Net.equalsCi(it.key, name) }?.value?.let(NetJson::long)?.toInt() ?: default
                b.pomodoro.settings = PomodoroSettings(minutes("focusMinutes", 25), minutes("shortBreakMinutes", 5), minutes("longBreakMinutes", 15), minutes("focusesBeforeLongBreak", 4))
            }
            val phase = PomodoroPhase.entries.firstOrNull { Net.equalsCi(it.wire, NetJson.str(prop("phase")) ?: "") } ?: PomodoroPhase.IDLE
            val itemId = NetJson.str(prop("itemId"))?.let(Net::parseGuid)?.takeIf { b.find(it) != null }
            b.pomodoro.restore(
                phase,
                NetTime.parse(NetJson.str(prop("endsAt"))),
                NetJson.long(prop("pausedRemainingSeconds"))?.let { Duration.ofSeconds(it) },
                itemId,
                NetJson.long(prop("completedFocusCount"))?.toInt() ?: 0,
                NetTime.parse(NetJson.str(prop("breakEndedAt"))),
            )
            b.nowOrder.clear()
            for (id in (prop("nowOrder") as? JsonArray) ?: JsonArray(emptyList())) {
                val g = Net.parseGuid(NetJson.str(id)) ?: continue
                if (b.find(g) != null && g !in b.nowOrder) b.nowOrder.add(g)
            }
            b.nowArrangedAt = NetTime.parse(NetJson.str(prop("nowArrangedAt")))
        } catch (e: RuntimeException) {
            // A damaged timer state just means an idle timer.
        }
    }

    private fun stateText(): String {
        val p = board.pomodoro
        val doc = LinkedHashMap<String, JsonElement>()
        doc["settings"] = JsonObject(
            linkedMapOf(
                "focusMinutes" to JsonPrimitive(p.settings.focusMinutes),
                "shortBreakMinutes" to JsonPrimitive(p.settings.shortBreakMinutes),
                "longBreakMinutes" to JsonPrimitive(p.settings.longBreakMinutes),
                "focusesBeforeLongBreak" to JsonPrimitive(p.settings.focusesBeforeLongBreak),
            ),
        )
        doc["phase"] = JsonPrimitive(p.phase.wire)
        p.endsAt?.let { doc["endsAt"] = NetTime.json(it) }
        p.pausedRemaining?.let { doc["pausedRemainingSeconds"] = JsonPrimitive(Math.rint(it.toMillis() / 1000.0).toInt()) }
        p.itemId?.let { doc["itemId"] = JsonPrimitive(it.toString()) }
        doc["completedFocusCount"] = JsonPrimitive(p.completedFocusCount)
        if (board.nowOrder.isNotEmpty()) doc["nowOrder"] = JsonArray(board.nowOrder.map { JsonPrimitive(it.toString()) })
        p.breakEndedAt?.let { doc["breakEndedAt"] = NetTime.json(it) }
        board.nowArrangedAt?.let { doc["nowArrangedAt"] = NetTime.json(it) }
        return NetJson.write(JsonObject(doc), JsonEscaping.DEFAULT, indented = true) + "\n"
    }

    private val activityKinds = setOf(
        "created", "updated", "noteAdded", "completed", "reopened", "scheduled", "reminderAdded", "reminderFired", "reminderDismissed", "deleted",
        "moved", "focusStarted", "focusCompleted", "groupChanged", "attachmentAdded", "attachmentRemoved", "labelChanged", "archived", "timeLogged",
    ).associateBy(Net::ci)

    /** Reads the whole lines of activity.jsonl (a torn last line, from a crash mid-append, is left for later). */
    private fun loadActivity(b: TaskBoard) {
        val file = File(meta, "activity.jsonl")
        val bytes = if (file.exists()) file.readBytes() else ByteArray(0)
        activityBytes = bytes.size.toLong()
        val text = String(bytes, Charsets.UTF_8)
        val complete = text.lastIndexOf('\n')
        val entries = mutableListOf<ActivityEntry>()
        if (complete >= 0) {
            for (line in text.substring(0, complete).split('\n')) {
                if (line.isEmpty()) continue
                parseActivity(line)?.let(entries::add)
            }
        }
        activityLines = entries
        b.activity.clear()
        b.activity.addAll(entries)
    }

    private fun parseActivity(line: String): ActivityEntry? = try {
        val o = NetJson.parse(line) as? JsonObject
        fun prop(name: String): JsonElement? = o?.entries?.firstOrNull { Net.equalsCi(it.key, name) }?.value
        val at = NetTime.parse(NetJson.str(prop("at")))
        val itemId = Net.parseGuid(NetJson.str(prop("itemId")))
        val kind = NetJson.str(prop("kind"))?.let { activityKinds[Net.ci(it)] }
        val actorKind = NetJson.str(prop("actorKind"))?.let { k -> ActorKind.entries.firstOrNull { Net.equalsCi(it.wire, k) } }
        if (o == null || at == null || itemId == null || kind == null || actorKind == null) null else ActivityEntry(at, itemId, kind, NetJson.str(prop("summary")) ?: "", Actor(actorKind, NetJson.str(prop("actor"))))
    } catch (e: RuntimeException) {
        null
    }

    private fun activityLine(a: ActivityEntry): String {
        val o = linkedMapOf<String, JsonElement>(
            "at" to NetTime.json(a.at),
            "itemId" to JsonPrimitive(a.itemId.toString()),
            "kind" to JsonPrimitive(a.kind),
            "summary" to JsonPrimitive(a.summary),
            "actorKind" to JsonPrimitive(a.actor.kind.wire),
        )
        a.actor.name?.let { o["actor"] = JsonPrimitive(it) }
        return NetJson.write(JsonObject(o))
    }

    private fun appendActivity(undo: ArrayDeque<() -> Unit>?): Boolean {
        val pending = board.activity.drop(activityLines.size)
        if (pending.isEmpty()) return false
        val file = File(meta, "activity.jsonl")
        meta.mkdirs()
        val length = fileLength(file)
        val sb = StringBuilder()
        if (length > 0 && lastByte(file) != '\n'.code) sb.append('\n') // never glued onto a torn last line
        for (a in pending) sb.append(activityLine(a)).append('\n')
        file.appendBytes(sb.toString().toByteArray(Charsets.UTF_8))
        undo?.addLast { RandomAccessFile(file, "rw").use { it.setLength(length) } }
        activityLines.addAll(pending)
        activityBytes = fileLength(file)
        return true
    }

    private fun lastByte(file: File): Int = RandomAccessFile(file, "r").use {
        it.seek(it.length() - 1)
        it.read()
    }

    // ---- Sync ----

    /** Everything that syncs, read after taking in any changes (docs/design/phone-sync.md, "What syncs"). */
    fun readSync(): SyncSnapshot = synchronized(lock) {
        refreshIfNeeded()
        buildSyncSnapshot()
    }

    /** Whether [text] would be read as a task file at [rel] (merged text is checked before it's written). */
    fun isValidTaskText(rel: String, text: String): Boolean = try {
        parseText(text, rel)
        true
    } catch (e: VaultFormatException) {
        false
    }

    private fun buildSyncSnapshot(): SyncSnapshot {
        // A task file that can't be read has no task id to list it under: left out (or listed as a plain file), the other
        // devices would take it as deleted and trash their copy. Sync waits until it's fixed.
        caches.files.entries.firstOrNull { it.second.good == null }?.let { (rel, state) ->
            throw IOException("\"$rel\" can't be read (${state.error}). Sync waits until it's fixed, so your other devices keep the task.")
        }
        val entries = LinkedHashMap<String, SyncEntry>()
        val memory = HashMap<String, ByteArray>()
        val covered = HashSet<String>()
        val seen = HashSet<String>()
        fun add(key: String, rel: String) {
            val file = full(rel)
            if (file.isFile && file.length() <= SyncLimits.MAX_FILE_BYTES && !Files.isSymbolicLink(file.toPath())) {
                val stamp = stamp(file)
                var known = syncHashes[Net.ci(rel)]
                if (known == null || known.first != stamp) {
                    known = stamp to SyncHash.of(file.readBytes())
                    syncHashes[Net.ci(rel)] = known
                }
                entries[key] = SyncEntry(key, rel, known.second)
                covered.add(Net.ci(rel))
                seen.add(Net.ci(rel))
            }
        }
        for ((id, rel) in caches.paths) {
            add(SyncKeys.task(id), rel)
            add(SyncKeys.rich(id), rel.substring(0, rel.length - Net.extension(rel).length) + ".html")
        }
        for (rel in syncableFiles()) if (Net.ci(rel) !in covered) add(SyncKeys.file(rel), rel)
        fun remember(key: String, path: String, bytes: ByteArray) {
            entries[key] = SyncEntry(key, path, SyncHash.of(bytes))
            memory[key] = bytes
        }
        File(meta, "config.json").takeIf { it.exists() }?.let { remember(SyncKeys.CONFIG, SyncKeys.CONFIG_PATH, it.readBytes()) }
        remember(SyncKeys.ORDER, SyncKeys.ORDER_PATH, orderBytes(board.nowOrder))
        File(meta, "activity.jsonl").takeIf { it.exists() }?.let { remember(SyncKeys.ACTIVITY, SyncKeys.ACTIVITY_PATH, it.readBytes()) }
        syncHashes.keys.retainAll(seen)
        val hashes = syncHashes
        return SyncSnapshot(entries) { e ->
            memory[e.key] ?: run {
                val file = full(e.path)
                val content = if (file.exists()) file.readBytes() else ByteArray(0)
                if (SyncHash.of(content) == e.hash) {
                    content
                } else {
                    synchronized(lock) { hashes.remove(Net.ci(e.path)) }
                    throw SyncStaleException("${e.path} changed.")
                }
            }
        }
    }

    /** Files in the vault's folders (never root files like AGENTS.md, hidden ones, or links that lead elsewhere). */
    private fun syncableFiles(): List<String> {
        fun plain(f: File) = !f.name.startsWith('.') && !Files.isSymbolicLink(f.toPath())
        val result = mutableListOf<String>()
        val pending = ArrayDeque((root.listFiles() ?: emptyArray()).filter { it.isDirectory && plain(it) }.sortedBy { it.name })
        while (pending.isNotEmpty()) {
            val dir = pending.removeLast()
            val children = (dir.listFiles() ?: emptyArray()).sortedBy { it.name }
            for (f in children) if (f.isFile && plain(f)) relative(f).takeIf(VaultFiles::isSyncablePath)?.let(result::add)
            for (d in children) if (d.isDirectory && plain(d)) pending.addLast(d)
        }
        return result
    }

    private fun orderBytes(ids: List<UUID>): ByteArray = NetJson.write(JsonArray(ids.map { JsonPrimitive(it.toString()) })).toByteArray(Charsets.UTF_8)

    private fun parseOrder(content: ByteArray): List<UUID> = try {
        (NetJson.parse(String(content, Charsets.UTF_8)) as? JsonArray)?.mapNotNull { Net.parseGuid(NetJson.str(it)) } ?: emptyList()
    } catch (e: RuntimeException) {
        emptyList()
    }

    /**
     * Applies a sync plan. Every file it touches must still be what the plan was worked out from (else
     * [SyncStaleException] and nothing changes); deleted files go to the trash; a failure part way is undone.
     */
    fun applySync(ops: List<SyncOp>) = synchronized(lock) {
        refreshIfNeeded()
        verify(ops)
        var order: List<UUID>? = null
        val undo = ArrayDeque<() -> Unit>()
        try {
            for (op in ops) {
                val file = if (op.kind == SyncOpKind.SET_CONFIG || op.kind == SyncOpKind.SET_ORDER || op.kind == SyncOpKind.APPEND_ACTIVITY) null else full(op.path)
                when (op.kind) {
                    SyncOpKind.APPEND_ACTIVITY -> {
                        val activity = File(meta, "activity.jsonl")
                        val length = fileLength(activity)
                        appendActivityLines(op.content!!)
                        undo.addLast { RandomAccessFile(activity, "rw").use { it.setLength(length) } }
                    }
                    SyncOpKind.WRITE -> {
                        val before = if (file!!.exists()) file.readBytes() else null
                        VaultFiles.writeAtomic(file, op.content!!, replace = before != null)
                        undo.addLast { if (before == null) file.delete() else VaultFiles.writeAtomic(file, before) }
                    }
                    SyncOpKind.MOVE -> {
                        val target = full(op.to!!)
                        target.parentFile?.mkdirs()
                        rename(file!!, target)
                        undo.addLast { rename(target, file) }
                    }
                    SyncOpKind.DELETE -> {
                        val trash = trashPath(file!!.name)
                        rename(file, trash)
                        undo.addLast { rename(trash, file) }
                    }
                    SyncOpKind.SET_CONFIG -> {
                        val config = File(meta, "config.json")
                        val previous = if (config.exists()) config.readBytes() else null
                        VaultFiles.writeAtomic(config, op.content!!)
                        undo.addLast { if (previous != null) VaultFiles.writeAtomic(config, previous) else config.delete() }
                    }
                    SyncOpKind.SET_ORDER -> order = parseOrder(op.content!!)
                }
            }
        } catch (e: Exception) {
            // Whatever went wrong, everything done so far is undone: the vault is never left half synced.
            while (undo.isNotEmpty()) {
                try {
                    undo.removeLast()()
                } catch (ignored: Exception) {
                    // Best effort: the next sync sees whatever is there.
                }
            }
            forceContent = true
            reload()
            throw e
        }
        forceContent = true
        reload()
        order?.let { ids ->
            board.nowOrder.clear()
            for (id in ids) if (board.find(id) != null && id !in board.nowOrder) board.nowOrder.add(id)
            writeMetadataIfChanged(null)
        }
        Unit
    }

    /** Writing through a link (a symlink) could reach outside the vault: refused. */
    private fun ensureInsideVault(rel: String) {
        val rootPath = root.absoluteFile.toPath().normalize()
        var dir: java.nio.file.Path? = full(rel).absoluteFile.toPath().normalize().parent
        while (dir != null && dir.startsWith(rootPath) && dir != rootPath) {
            if (Files.isSymbolicLink(dir)) throw IOException("Sync refused to write through a link: $rel")
            dir = dir.parent
        }
        if (Files.isSymbolicLink(full(rel).toPath())) throw IOException("Sync refused to write through a link: $rel")
    }

    /** Checks the plan against the files as they are now (moves and writes in order); nothing is written yet. */
    private fun verify(ops: List<SyncOp>) {
        val known = HashMap<String, String?>()
        fun current(rel: String): String? {
            val k = Net.ci(rel)
            if (!known.containsKey(k)) {
                val f = full(rel)
                known[k] = if (f.isFile) SyncHash.of(f.readBytes()) else null
            }
            return known[k]
        }
        fun expect(rel: String, expected: String?) {
            if (current(rel) != expected) throw SyncStaleException("$rel changed while syncing.")
        }
        for (op in ops) {
            val fileOp = op.kind == SyncOpKind.WRITE || op.kind == SyncOpKind.MOVE || op.kind == SyncOpKind.DELETE
            if (fileOp && !VaultFiles.isSyncablePath(op.path)) throw IOException("Sync refused an unsafe path: ${op.path}")
            if (fileOp) {
                ensureInsideVault(op.path)
                if (op.to != null && VaultFiles.isSyncablePath(op.to)) ensureInsideVault(op.to)
            }
            when (op.kind) {
                SyncOpKind.WRITE -> {
                    expect(op.path, op.expected)
                    known[Net.ci(op.path)] = SyncHash.of(op.content!!)
                }
                SyncOpKind.MOVE -> {
                    if (op.to == null || !VaultFiles.isSyncablePath(op.to)) throw IOException("Sync refused an unsafe path: ${op.to}")
                    expect(op.path, op.expected)
                    if (!Net.equalsCi(op.to, op.path)) expect(op.to, null)
                    known[Net.ci(op.to)] = op.expected
                    known[Net.ci(op.path)] = null
                }
                SyncOpKind.DELETE -> {
                    expect(op.path, op.expected)
                    known[Net.ci(op.path)] = null
                }
                SyncOpKind.SET_CONFIG -> {
                    val config = File(meta, "config.json")
                    if ((if (config.exists()) SyncHash.of(config.readBytes()) else null) != op.expected) throw SyncStaleException("The settings changed while syncing.")
                }
                SyncOpKind.SET_ORDER -> {
                    if (op.expected != null && SyncHash.of(orderBytes(board.nowOrder)) != op.expected) throw SyncStaleException("The Now order changed while syncing.")
                }
                SyncOpKind.APPEND_ACTIVITY -> {}
            }
        }
    }

    /** Appends whole lines another device logged (never glued onto a torn last line). */
    private fun appendActivityLines(lines: ByteArray) {
        val file = File(meta, "activity.jsonl")
        meta.mkdirs()
        val prefix = if (fileLength(file) > 0 && lastByte(file) != '\n'.code) byteArrayOf('\n'.code.toByte()) else ByteArray(0)
        file.appendBytes(prefix + lines)
    }

    // ---- File system helpers ----

    private fun full(rel: String): File = File(root, rel)

    private fun htmlOf(file: File): File = File(file.parentFile, Net.stem(file.name) + ".html")

    private fun relative(file: File): String = file.absoluteFile.relativeTo(root.absoluteFile).invariantSeparatorsPath

    private fun isGroupFolder(name: String) = name.isNotEmpty() && name[0] != '.' && name[0] != '_'

    private fun topFolders(): List<String> = (root.listFiles() ?: emptyArray()).filter { it.isDirectory && isGroupFolder(it.name) }.map { it.name }.sorted()

    private fun enumerateTaskFiles(): List<File> = topFolders().flatMap { enumerateTaskFiles(File(root, it)) }

    private fun enumerateTaskFiles(folder: File): List<File> {
        val children = (folder.listFiles() ?: return emptyList()).sortedBy { it.name }
        return children.filter { it.isFile && Net.endsWithCi(it.name, ".md") && !it.name.startsWith('.') } +
            children.filter { it.isDirectory && isGroupFolder(it.name) }.flatMap { enumerateTaskFiles(it) }
    }

    private fun stamp(file: File): Pair<Long, Long> = if (file.exists()) file.lastModified() to file.length() else Long.MIN_VALUE to -1L

    private fun fileLength(file: File): Long = if (file.exists()) file.length() else 0L

    private fun readOrEmpty(file: File): String = if (file.exists()) VaultFiles.readText(file) else ""

    private fun hasAnyId(b: TaskBoard, top: WorkItem): Boolean = top.selfAndDescendants.any { b.find(it.id) != null }

    /** Adds a parsed (or another board's) task tree as a top-level task in [groupId], its subtasks with it. */
    private fun attachLoaded(b: TaskBoard, top: WorkItem, groupId: UUID) {
        val tree = top.selfAndDescendants
        for (item in tree) if (b.find(item.id) != null) throw BoardException("Duplicate task id ${item.id}.")
        val children = tree.associateWith { it.children.toList() }
        for (item in tree) item.children.clear()
        top.ownGroupId = groupId
        fun add(item: WorkItem, parent: WorkItem?) {
            b.attach(item, parent)
            for (child in children[item]!!) add(child, item)
        }
        add(top, null)
        for (label in tree.flatMap { it.labels }) b.ensureLabel(label)
    }

    private data class FileState(val text: String, val good: String?, val stamp: Pair<Long, Long>, val error: String?)

    /** Everything the store remembers about the files; replaced as a whole after a successful save. */
    private class Caches(
        val files: CiMap<FileState> = CiMap(),
        val paths: LinkedHashMap<UUID, String> = LinkedHashMap(),
        val groupFolders: LinkedHashMap<UUID, String> = LinkedHashMap(),
        val fileIds: CiMap<Set<UUID>> = CiMap(),
        val layouts: CiMap<ParsedTaskFile> = CiMap(),
        val signatures: MutableMap<UUID, String> = HashMap(),
        val blocked: Set<UUID> = emptySet(),
    ) {
        fun copy() = Caches(files.copy(), LinkedHashMap(paths), LinkedHashMap(groupFolders), fileIds.copy(), layouts.copy(), HashMap(signatures), HashSet(blocked))
    }

    private class VaultConflictException(path: String) : IOException("\"$path\" changed while saving.")
}
