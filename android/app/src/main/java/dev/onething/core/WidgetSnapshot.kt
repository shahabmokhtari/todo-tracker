package dev.onething.core

import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.util.UUID
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put

/** A task as a home-screen widget shows it: its title and group, nothing else (no details, notes or tokens). */
data class WidgetItem(
    val id: UUID,
    val title: String,
    val group: String?,
    /** The group's color (#rrggbb). */
    val color: String?,
    /** It has open subtasks: the widget opens it instead of marking it done. */
    val openSubtasks: Boolean,
)

/** A task coming back later today, and when. */
data class WidgetLater(val id: UUID, val at: Instant)

/** What the widget shows from [at] until the next frame: the Do now list (its first [WidgetSnapshot.MAX_ITEMS] of [nowCount]) and what comes back later today. */
data class WidgetFrame(val at: Instant, val now: List<UUID>, val nowCount: Int, val later: List<WidgetLater>, val laterCount: Int) {
    internal fun sameList(other: WidgetFrame) = nowCount == other.nowCount && laterCount == other.laterCount && now == other.now && later == other.later
}

/** What a widget shows at one moment. */
data class WidgetView(
    /** The one thing to do now (null: nothing). */
    val focus: WidgetItem?,
    /** The next few after it. */
    val next: List<WidgetItem>,
    /** How many more today after [focus] and [next]: the rest to do now and what comes back later today. */
    val more: Int,
    /** The Do now list (as far as the snapshot keeps it). */
    val today: List<WidgetItem>,
    /** What comes back later today, soonest first. */
    val later: List<Pair<WidgetItem, Instant>>,
    val nowCount: Int,
    val laterCount: Int,
) {
    /** How many more today after showing the first [shown] of the Do now list. */
    fun moreAfter(shown: Int): Int = maxOf(0, nowCount - shown) + laterCount

    companion object {
        val EMPTY = WidgetView(null, emptyList(), 0, emptyList(), emptyList(), 0, 0)
    }
}

/**
 * What the home-screen widget shows, worked out with the agenda's own rules ([Agenda.build]) at every moment the list can
 * change in the next day (a snooze ends, a reminder or a planned block starts, a deadline passes, midnight), so the
 * widget knows when to update with the app closed. Same rules as OneThing.Core.WidgetSnapshot and the Swift port
 * (tests/fixtures/widget.json checks all three).
 */
data class WidgetSnapshot(val at: Instant, val items: List<WidgetItem>, val frames: List<WidgetFrame>) {
    /** What to show at [at], leaving out [hidden] (just marked done from the widget). */
    fun viewAt(at: Instant, hidden: Set<UUID> = emptySet()): WidgetView {
        val frame = frameAt(at) ?: return WidgetView.EMPTY
        val byId = items.associateBy { it.id }
        val today = frame.now.filter { it !in hidden }.mapNotNull { byId[it] }
        val later = frame.later.filter { it.id !in hidden }.mapNotNull { l -> byId[l.id]?.let { it to l.at } }
        val nowCount = frame.nowCount - (frame.now.size - today.size)
        val laterCount = frame.laterCount - (frame.later.size - later.size)
        val next = today.drop(1).take(NEXT_COUNT)
        val shown = if (today.isEmpty()) 0 else 1 + next.size
        return WidgetView(today.firstOrNull(), next, maxOf(0, nowCount - shown) + laterCount, today, later, nowCount, laterCount)
    }

    /** The frame on at [at]: the last one started by then (the first one before it starts). */
    fun frameAt(at: Instant): WidgetFrame? = frames.lastOrNull { !it.at.isAfter(at) } ?: frames.firstOrNull()

    /** When the widget should next change (null: not in the next day). */
    fun nextChange(after: Instant): Instant? = frames.firstOrNull { it.at.isAfter(after) }?.at

    /** The snapshot in the shared format (tests/fixtures/widget.json): titles, groups, ids and times only. */
    fun toJson(): JsonObject = buildJsonObject {
        put("at", BoardCodec.formatDate(at))
        put("items", buildJsonArray {
            items.forEach { i ->
                add(buildJsonObject {
                    put("id", i.id.toString())
                    put("title", i.title)
                    put("group", i.group?.let { JsonPrimitive(it) } ?: JsonNull)
                    put("color", i.color?.let { JsonPrimitive(it) } ?: JsonNull)
                    put("openSubtasks", i.openSubtasks)
                })
            }
        })
        put("frames", buildJsonArray {
            frames.forEach { f ->
                add(buildJsonObject {
                    put("at", BoardCodec.formatDate(f.at))
                    put("now", JsonArray(f.now.map { JsonPrimitive(it.toString()) }))
                    put("nowCount", f.nowCount)
                    put("later", JsonArray(f.later.map { buildJsonObject { put("id", it.id.toString()); put("at", BoardCodec.formatDate(it.at)) } }))
                    put("laterCount", f.laterCount)
                })
            }
        })
    }

    companion object {
        /** How far ahead frames are worked out. */
        val HORIZON: Duration = Duration.ofHours(24)

        /** At most this many frames (the first one is now). */
        const val MAX_FRAMES = 24

        /** At most this many tasks of each list in a frame. */
        const val MAX_ITEMS = 8

        /** How many tasks after the focus a medium widget lists. */
        const val NEXT_COUNT = 3

        fun build(board: TaskBoard, now: Instant, zone: ZoneId): WidgetSnapshot {
            val until = now.plus(HORIZON)
            val points = sortedSetOf<Instant>()
            fun add(at: Instant?) {
                if (at != null && at.isAfter(now) && !at.isAfter(until)) points.add(at)
            }
            for (item in board.allItems.filter { !it.isDone && !it.isArchived }) {
                add(item.nextActionAt)
                add(item.deadline)
                add(item.plannedAt)
                add(item.plannedAt?.plusSeconds(60L * (item.plannedMinutes ?: 30)))
                item.reminders.filter { it.dismissedAt == null }.forEach { add(it.dueAt) }
            }
            var midnight = nextMidnight(now, zone)
            while (!midnight.isAfter(until)) {
                add(midnight)
                midnight = nextMidnight(midnight, zone)
            }

            val frames = mutableListOf<WidgetFrame>()
            for (at in listOf(now) + points.take(MAX_FRAMES - 1)) {
                val dashboard = Agenda.build(board, at, recentNoteCount = 0)
                val endOfDay = nextMidnight(at, zone)
                val later = dashboard.waiting.filter { e -> e.wakeAt?.isBefore(endOfDay) == true }
                val frame = WidgetFrame(
                    at,
                    dashboard.now.take(MAX_ITEMS).map { it.item.id },
                    dashboard.now.size,
                    later.take(MAX_ITEMS).map { WidgetLater(it.item.id, it.wakeAt!!) },
                    later.size,
                )
                if (frames.lastOrNull()?.sameList(frame) == true) continue
                frames.add(frame)
            }

            val items = frames.flatMap { f -> f.now + f.later.map { it.id } }.distinct().mapNotNull { id ->
                val item = board.find(id) ?: return@mapNotNull null
                val group = board.groups.firstOrNull { it.id == item.groupId }
                WidgetItem(id, item.title, group?.name, group?.color, item.hasOpenChildren)
            }
            return WidgetSnapshot(now, items, frames)
        }

        /**
         * Whether Done on the widget marks [id] done at [now]: only a task that's to do now and has no open subtasks
         * (those open in the app). A second tap on a repeating task (moved on to its next time), or a widget showing a
         * task done elsewhere, does nothing.
         */
        fun canMarkDone(board: TaskBoard, id: UUID, now: Instant): Boolean {
            val item = board.find(id) ?: return false
            if (item.isDone || item.hasOpenChildren) return false
            return Agenda.build(board, now, recentNoteCount = 0).now.any { it.item.id == id }
        }

        /** The start of the day after the one [at] is in. */
        fun nextMidnight(at: Instant, zone: ZoneId): Instant = at.atZone(zone).toLocalDate().plusDays(1).atStartOfDay(zone).toInstant()

        /** Reads a snapshot in the shared format (null when it isn't one). */
        fun fromJson(json: JsonObject): WidgetSnapshot? = runCatching {
            fun text(o: JsonObject, key: String): String? = (o[key] as? JsonPrimitive)?.takeIf { it.isString }?.content
            WidgetSnapshot(
                BoardCodec.parseDate(text(json, "at")!!)!!,
                json["items"]!!.jsonArray.map { it.jsonObject }.map { i ->
                    WidgetItem(UUID.fromString(text(i, "id")), text(i, "title")!!, text(i, "group"), text(i, "color"), i["openSubtasks"]?.jsonPrimitive?.booleanOrNull ?: false)
                },
                json["frames"]!!.jsonArray.map { it.jsonObject }.map { f ->
                    val now = f["now"]!!.jsonArray.map { UUID.fromString(it.jsonPrimitive.content) }
                    val later = f["later"]!!.jsonArray.map { it.jsonObject }.map { WidgetLater(UUID.fromString(text(it, "id")), BoardCodec.parseDate(text(it, "at")!!)!!) }
                    WidgetFrame(
                        BoardCodec.parseDate(text(f, "at")!!)!!, now, f["nowCount"]?.jsonPrimitive?.intOrNull ?: now.size,
                        later, f["laterCount"]?.jsonPrimitive?.intOrNull ?: later.size,
                    )
                },
            )
        }.getOrNull()
    }
}
