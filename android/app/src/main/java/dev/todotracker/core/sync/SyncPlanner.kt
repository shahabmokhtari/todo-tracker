package dev.todotracker.core.sync

import dev.todotracker.core.vault.JsonEscaping
import dev.todotracker.core.vault.Net
import dev.todotracker.core.vault.NetJson
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive

/** What the planner may ask beyond the two sides. */
class SyncPlanOptions(
    /** Whether some device recorded deleting exactly this version (so it isn't brought back). */
    val deleted: (SyncEntry) -> Boolean = { false },
    /** Whether merged text is still a valid task file at a path (else both versions are kept whole). */
    val isValidTask: (String, String) -> Boolean = { _, _ -> true },
)

/**
 * Works out how to bring a peer's changes into this vault, exactly as TodoTracker.Core.Sync.SyncPlanner: a three-way
 * merge per entry against what both devices last agreed on. Whenever a choice has to be made the device whose id sorts
 * first wins, so both devices make the same choice and end up with the same files.
 */
object SyncPlanner {
    fun plan(local: SyncSide, peer: SyncSide, base: Map<String, SyncEntry>, readBase: (String) -> ByteArray?, options: SyncPlanOptions = SyncPlanOptions()): SyncPlan {
        val ctx = Context(local, peer, readBase, options)
        val files = mutableListOf<Files>()
        val keys = (local.entries.keys + peer.entries.keys + base.keys).toSortedSet(Net.ordinal)
        for (key in keys) {
            val l = local.entries[key]
            val p = peer.entries[key]
            val b = base[key]
            val kind = SyncKeys.kindOf(key)
            val isFile = kind == SyncKind.TASK || kind == SyncKind.RICH || kind == SyncKind.FILE
            if (p == null || (l != null && l.hash == p.hash && l.path == p.path)) {
                // The peer doesn't have it (or has exactly ours): a peer delete of something unchanged here matters,
                // and so does a delete some device recorded of exactly this version.
                if (p == null && l != null && isFile && ((b != null && l.hash == b.hash && l.path == b.path) || options.deleted(l))) {
                    ctx.ops.add(SyncOp(SyncOpKind.DELETE, l.path, l.hash))
                }
                continue
            }
            when (kind) {
                SyncKind.ACTIVITY -> mergeActivity(ctx, l, p)
                SyncKind.ORDER -> mergeJson(ctx, SyncOpKind.SET_ORDER, l, p, b, indented = false)
                SyncKind.CONFIG -> mergeJson(ctx, SyncOpKind.SET_CONFIG, l, p, b, indented = true)
                else -> files.add(Files(key, l, p, b))
            }
        }

        // Paths that are deleted in this plan are free for new files (a file renamed only in letter case, say).
        for (delete in ctx.ops.filter { it.kind == SyncOpKind.DELETE }) ctx.taken.remove(Net.ci(delete.path))

        // Where things live first (so moves never land on another file), then what they contain.
        val paths = planMoves(ctx, files.filter { it.l != null })
        for (f in files) mergeFile(ctx, f.key, f.l, f.p, f.b, if (f.l == null) null else paths[f.key])

        // Moves, then deletes (to the trash, freeing their paths), then writes; the activity log last.
        val ordered = ctx.moves +
            ctx.ops.filter { it.kind == SyncOpKind.DELETE } +
            ctx.ops.filter { it.kind == SyncOpKind.WRITE } +
            ctx.ops.filter { it.kind == SyncOpKind.SET_CONFIG || it.kind == SyncOpKind.SET_ORDER } +
            ctx.ops.filter { it.kind == SyncOpKind.APPEND_ACTIVITY }
        return SyncPlan(ordered, ctx.conflicts)
    }

    /** What both devices now agree on (same content, same place); everything else keeps its old base. */
    fun nextBase(local: Map<String, SyncEntry>, peer: Map<String, SyncEntry>, oldBase: Map<String, SyncEntry>): Map<String, SyncEntry> {
        val next = LinkedHashMap<String, SyncEntry>()
        for (key in LinkedHashSet(local.keys + peer.keys + oldBase.keys)) {
            val l = local[key]
            val p = peer[key]
            if (l != null && p != null && l.hash == p.hash && l.path == p.path) {
                next[key] = p
            } else if ((l != null || p != null) && oldBase.containsKey(key)) {
                next[key] = oldBase[key]!!
            }
        }
        return next
    }

    private class Files(val key: String, val l: SyncEntry?, val p: SyncEntry, val b: SyncEntry?)

    private class Context(val local: SyncSide, val peer: SyncSide, val readBase: (String) -> ByteArray?, val options: SyncPlanOptions) {
        val ops = mutableListOf<SyncOp>()
        val moves = mutableListOf<SyncOp>()
        val conflicts = mutableListOf<SyncConflict>()

        /** Paths in use here (and planned), compared case-insensitively, to never write over another entry's file. */
        val taken: MutableSet<String> = local.entries.values.map { Net.ci(it.path) }.toMutableSet()
        val localWins = local.device < peer.device
        val loserName: String get() = if (localWins) peer.name else local.name

        fun localAt(path: String): SyncEntry? = local.entries.values.firstOrNull { Net.equalsCi(it.path, path) }

        fun take(path: String): String {
            taken.add(Net.ci(path))
            return path
        }

        fun freePath(path: String): String {
            val dir = if (path.contains('/')) path.substring(0, path.lastIndexOf('/') + 1) else ""
            val ext = Net.extension(path)
            val stem = path.substring(dir.length, path.length - ext.length)
            var candidate = path
            var n = 2
            while (Net.ci(candidate) in taken) candidate = "$dir$stem ${n++}$ext"
            return take(candidate)
        }
    }

    /**
     * Where each file both sides have lives: a move on one side wins, moves on both go to the winner's choice. A move
     * onto a file that stays put waits; files that trade places go through a temporary name. Returns each key's path.
     */
    private fun planMoves(ctx: Context, both: List<Files>): Map<String, String> {
        val paths = HashMap<String, String>()
        val wanted = mutableListOf<Triple<String, SyncEntry, String>>()
        for (f in both) {
            val l = f.l!!
            val p = f.p
            val b = f.b
            paths[f.key] = l.path
            if (l.path != p.path) {
                val target = when {
                    b != null && p.path == b.path -> l.path
                    b != null && l.path == b.path -> p.path
                    ctx.localWins -> l.path
                    else -> p.path
                }
                if (target != l.path) wanted.add(Triple(f.key, l, target))
            }
        }

        // A move needs its target free, or freed by a move that also happens; drop blocked moves until none are.
        var active = wanted.toList()
        while (true) {
            val leavingNow = active.map { Net.ci(it.second.path) }.toSet()
            val claimedNow = HashSet<String>()
            val keep = active.filter { w ->
                (Net.equalsCi(w.third, w.second.path) || ctx.localAt(w.third) == null || Net.ci(w.third) in leavingNow) && claimedNow.add(Net.ci(w.third))
            }
            if (keep.size == active.size) break
            active = keep
        }

        val moves = mutableListOf<Triple<SyncEntry, String, Boolean>>()
        for ((key, l, target) in active) {
            val caseOnly = Net.equalsCi(target, l.path)
            paths[key] = target
            ctx.take(target)
            moves.add(Triple(l, target, !caseOnly && ctx.localAt(target) != null))
        }
        fun aside(l: SyncEntry): String {
            val ext = Net.extension(l.path)
            return "${l.path.substring(0, l.path.length - ext.length)} (moving ${l.hash.substring(0, 8)})$ext"
        }
        // Files that trade places step aside first.
        for ((l, target, swap) in moves) ctx.moves.add(SyncOp(SyncOpKind.MOVE, l.path, l.hash, to = if (swap) aside(l) else target))
        for ((l, target, swap) in moves) if (swap) ctx.moves.add(SyncOp(SyncOpKind.MOVE, aside(l), l.hash, to = target))
        return paths
    }

    private fun utf8(bytes: ByteArray) = String(bytes, Charsets.UTF_8)

    private fun mergeFile(ctx: Context, key: String, l: SyncEntry?, p: SyncEntry, b: SyncEntry?, path2: String?) {
        if (l == null) {
            // Deleted here and untouched there: stays deleted; so does a version some device deleted. Otherwise it's
            // new here (or was changed there: edits win).
            if ((b != null && p.hash == b.hash && p.path == b.path) || (b == null && ctx.options.deleted(p))) return
            val path = if (Net.ci(p.path) in ctx.taken) ctx.freePath(p.path) else ctx.take(p.path)
            ctx.ops.add(SyncOp(SyncOpKind.WRITE, path, null, ctx.peer.read(p)))
            return
        }
        val current = if (l.path != path2) l.copy(path = path2!!) else l
        if (l.hash == p.hash || (b != null && p.hash == b.hash)) return
        if (b != null && l.hash == b.hash) {
            ctx.ops.add(SyncOp(SyncOpKind.WRITE, current.path, l.hash, ctx.peer.read(p)))
            return
        }
        val mine = ctx.local.read(l)
        val theirs = ctx.peer.read(p)
        val winner = if (ctx.localWins) mine else theirs
        val loser = if (ctx.localWins) theirs else mine
        val kind = SyncKeys.kindOf(key)
        val baseBytes = if (b == null) null else ctx.readBase(b.hash)
        if ((kind == SyncKind.TASK || kind == SyncKind.RICH) && baseBytes != null) {
            val merged = TextMerge.merge(utf8(baseBytes), utf8(winner), utf8(loser), if (kind == SyncKind.TASK) ::taskLineKeys else null)
            // A merge that isn't a valid task any more (both changed the same frontmatter) isn't written: both
            // versions are kept whole instead.
            if (kind != SyncKind.TASK || ctx.options.isValidTask(current.path, merged.text)) {
                val text = merged.text.toByteArray(Charsets.UTF_8)
                if (merged.conflicted) ctx.conflicts.add(SyncConflict(key, current.path, ctx.peer.device, ctx.peer.name, l.hash, p.hash, b?.hash, SyncHash.of(text)))
                if (SyncHash.of(text) != l.hash) ctx.ops.add(SyncOp(SyncOpKind.WRITE, current.path, l.hash, text))
                return
            }
            ctx.conflicts.add(SyncConflict(key, current.path, ctx.peer.device, ctx.peer.name, l.hash, p.hash, b?.hash, SyncHash.of(winner)))
        }

        // Nothing to merge against (or not text, or no valid merge): the winner keeps the file, the other version is
        // kept next to it, the same on both devices.
        val copyPath = copyPath(current.path, ctx.loserName)
        val copy = if (kind == SyncKind.TASK) markTitle(utf8(loser), ctx.loserName).toByteArray(Charsets.UTF_8) else loser
        val copyHash = SyncHash.of(copy)
        val existing = ctx.localAt(copyPath)
        val planned = ctx.ops.any { it.kind == SyncOpKind.WRITE && Net.equalsCi(it.path, copyPath) && SyncHash.of(it.content!!) == copyHash }
        if (!planned && (existing == null || existing.hash != copyHash)) {
            ctx.ops.add(SyncOp(SyncOpKind.WRITE, if (existing == null && Net.ci(copyPath) !in ctx.taken) ctx.take(copyPath) else ctx.freePath(copyPath), null, copy))
        }
        if (!ctx.localWins) ctx.ops.add(SyncOp(SyncOpKind.WRITE, current.path, l.hash, winner))
    }

    /** "Trip (from Phone).md": only characters every file system takes (the name comes from another device). */
    private fun copyPath(path: String, device: String): String {
        val safe = Net.trim(device.filter { it.isLetterOrDigit() || it == ' ' || it == '-' || it == '_' || it == '\'' || it == '(' || it == ')' })
        val ext = Net.extension(path)
        return "${path.substring(0, path.length - ext.length)} (from ${if (safe.isNotEmpty()) Net.trim(safe.substring(0, minOf(40, safe.length))) else "another device"})$ext"
    }

    private val heading = Net.rx("^# (.*?)\r?$", multiline = true)

    /** "# Title" becomes "# Title (from Device)" so the copy is told apart in every list. */
    private fun markTitle(text: String, device: String): String {
        val m = heading.matcher(text)
        return if (m.find()) text.substring(0, m.end(1)) + " (from $device)" + text.substring(m.end(1)) else text
    }

    private val blockId = Net.rx("[${Net.WS}]\\^([A-Za-z0-9-]+)(?:[${Net.WS}]+%%.*%%)?[${Net.WS}]*$")
    private val hiddenId = Net.rx("^[-*+] .*%%\\{\"id\":\"([0-9a-fA-F-]{36})\"")
    private val frontmatterKey = Net.rx("^([A-Za-z][\\p{L}\\p{Mn}\\p{Nd}\\p{Pc}-]*):(?:[${Net.WS}]|$)")

    /** A task file's line identities: frontmatter keys inside the frontmatter, steps' block ids (`^x7k2m9`) below. */
    fun taskLineKeys(lines: List<String>): List<String?> {
        val keys = arrayOfNulls<String>(lines.size)
        var inFrontmatter = lines.isNotEmpty() && lines[0].trimEnd('\r', '\n') == "---"
        for (i in lines.indices) {
            val text = lines[i].trimEnd('\r', '\n')
            if (inFrontmatter && i > 0 && text == "---") {
                inFrontmatter = false
                continue
            }
            if (inFrontmatter && i > 0) {
                val m = frontmatterKey.matcher(text)
                keys[i] = if (m.find()) m.group(1)!! + ":" else null
            } else if (!inFrontmatter) {
                val block = blockId.matcher(text)
                if (block.find()) {
                    keys[i] = "^" + block.group(1)
                } else {
                    // Time and attachment lines carry their id: two devices adding different ones never collide.
                    val hidden = hiddenId.matcher(text)
                    if (hidden.find()) keys[i] = "id:" + hidden.group(1)
                }
            }
        }
        return keys.toList()
    }

    private fun mergeActivity(ctx: Context, l: SyncEntry?, p: SyncEntry) {
        fun lines(bytes: ByteArray) = utf8(bytes).split('\n').map { it.trimEnd('\r') }.filter { it.isNotEmpty() }
        val known = if (l == null) HashSet() else lines(ctx.local.read(l)).toHashSet()
        val added = lines(ctx.peer.read(p)).filter { known.add(it) }
        if (added.isNotEmpty()) ctx.ops.add(SyncOp(SyncOpKind.APPEND_ACTIVITY, SyncKeys.ACTIVITY_PATH, null, added.joinToString("") { "$it\n" }.toByteArray(Charsets.UTF_8)))
    }

    private fun mergeJson(ctx: Context, kind: SyncOpKind, l: SyncEntry?, p: SyncEntry, b: SyncEntry?, indented: Boolean) {
        val content: ByteArray
        if (l == null || (b != null && l.hash == b.hash)) {
            content = ctx.peer.read(p)
        } else if (b != null && p.hash == b.hash) {
            return
        } else {
            val mine = parse(ctx.local.read(l)) ?: return
            val theirs = parse(ctx.peer.read(p)) ?: return
            val baseBytes = if (b == null) null else ctx.readBase(b.hash)
            val old = baseBytes?.let(::parse)
            val node = if (ctx.localWins) mergeNode(nn(old), nn(mine), nn(theirs)) else mergeNode(nn(old), nn(theirs), nn(mine))
            val merged = if (kind == SyncOpKind.SET_CONFIG) oneTabPerFolder(node) else node
            val text = NetJson.write(merged ?: JsonNull, JsonEscaping.DEFAULT, indented)
            content = (if (indented) text + "\n" else text).toByteArray(Charsets.UTF_8)
        }
        if (l == null || SyncHash.of(content) != l.hash) {
            ctx.ops.add(SyncOp(kind, if (kind == SyncOpKind.SET_ORDER) SyncKeys.ORDER_PATH else SyncKeys.CONFIG_PATH, l?.hash, content))
        }
    }

    private fun parse(bytes: ByteArray): JsonElement? = NetJson.parseOrNull(String(bytes, Charsets.UTF_8).removePrefix("﻿"))

    /** JSON null is the same as nothing (as .NET's JsonNode). */
    private fun nn(e: JsonElement?): JsonElement? = if (e is JsonNull) null else e

    private fun clone(e: JsonElement?): JsonElement? = nn(e)?.let { deepCopy(it) }

    private fun deepCopy(e: JsonElement): JsonElement = when (e) {
        is JsonObject -> JsonObject(LinkedHashMap(e.mapValues { deepCopy(it.value) }))
        is JsonArray -> JsonArray(e.map(::deepCopy))
        else -> e
    }

    private fun eq(a: JsonElement?, b: JsonElement?) = NetJson.deepEquals(a, b)

    /** Three-way merge of JSON: objects by property, lists of objects by "id" (or "name"), plain lists as ordered sets. */
    internal fun mergeNode(b: JsonElement?, winner: JsonElement?, loser: JsonElement?): JsonElement? {
        if (eq(winner, loser) || eq(b, loser)) return clone(winner)
        if (eq(b, winner)) return clone(loser)
        if (winner is JsonObject && loser is JsonObject) {
            val bo = b as? JsonObject
            val result = LinkedHashMap<String, JsonElement>()
            for (name in LinkedHashSet(winner.keys + loser.keys)) {
                val bv = nn(bo?.get(name))
                val inBase = bo?.containsKey(name) == true
                if (winner.containsKey(name) && loser.containsKey(name)) {
                    result[name] = mergeNode(bv, nn(winner[name]), nn(loser[name])) ?: JsonNull
                } else if (winner.containsKey(name)) {
                    // Removed on one side: gone unless the other side changed it since.
                    if (!inBase || !eq(bv, winner[name])) result[name] = clone(winner[name]) ?: JsonNull
                } else if (!inBase || !eq(bv, loser[name])) {
                    result[name] = clone(loser[name]) ?: JsonNull
                }
            }
            return JsonObject(result)
        }
        if (winner is JsonArray && loser is JsonArray) {
            val keyName = listOf("id", "name").firstOrNull { k -> (winner + loser).all { x -> x is JsonObject && x[k].let { it is JsonPrimitive && it !is JsonNull } } }
            return if (keyName == null) mergeList(b as? JsonArray, winner, loser) else mergeKeyed(b as? JsonArray, winner, loser, keyName)
        }
        return clone(winner)
    }

    /** Two devices that each made a "Work" tab have one "Work" folder: the first tab for a folder stays. */
    private fun oneTabPerFolder(config: JsonElement?): JsonElement? {
        val o = config as? JsonObject ?: return config
        val groups = o["groups"] as? JsonArray ?: return config
        val seen = HashSet<String>()
        val kept = groups.filter { g ->
            val folder = ((g as? JsonObject)?.get("folder") as? JsonPrimitive)?.takeIf { it.isString }?.content
            folder == null || seen.add(Net.ci(folder))
        }
        return JsonObject(LinkedHashMap(o).also { it["groups"] = JsonArray(kept) })
    }

    private fun mergeList(b: JsonArray?, winner: JsonArray, loser: JsonArray): JsonArray {
        val removed = (b ?: JsonArray(emptyList())).filter { x -> winner.none { eq(it, x) } || loser.none { eq(it, x) } }
        val result = mutableListOf<JsonElement>()
        for (item in winner + loser.filter { l -> winner.none { eq(it, l) } }) {
            if (removed.none { eq(it, item) } && result.none { eq(it, item) }) result.add(clone(item) ?: JsonNull)
        }
        return JsonArray(result)
    }

    private fun mergeKeyed(b: JsonArray?, winner: JsonArray, loser: JsonArray, keyName: String): JsonArray {
        fun key(node: JsonElement) = NetJson.write((node as JsonObject)[keyName])
        fun index(list: List<JsonElement>): LinkedHashMap<String, JsonElement> {
            val map = LinkedHashMap<String, JsonElement>()
            for (x in list) require(map.put(key(x), x) == null) { "An item with the same key has already been added." }
            return map
        }
        val old = index((b ?: JsonArray(emptyList())).filter { x -> x is JsonObject && x[keyName].let { it is JsonPrimitive && it !is JsonNull } })
        val w = index(winner)
        val l = index(loser)
        val result = mutableListOf<JsonElement>()
        for (k in w.keys + l.keys.filter { it !in w }) {
            val bv = old[k]
            val wv = w[k]
            val lv = l[k]
            val merged = when {
                wv != null && lv != null -> mergeNode(bv, wv, lv)
                // Removed on one side: gone unless the other side changed it since.
                wv != null -> if (bv != null && eq(bv, wv)) null else clone(wv)
                else -> if (bv != null && eq(bv, lv)) null else clone(lv)
            }
            if (merged != null) result.add(merged)
        }
        return JsonArray(result)
    }
}
