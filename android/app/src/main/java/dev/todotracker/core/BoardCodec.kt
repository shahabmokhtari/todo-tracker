package dev.todotracker.core

import java.time.Duration
import java.time.Instant
import java.time.OffsetDateTime
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import java.util.UUID
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
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

/** JSON schema v1, the same as TodoTracker.Core.BoardSerializer (C#) and BoardCodec (Swift). */
object BoardCodec {
    private val json = Json { prettyPrint = true }
    private val millis: DateTimeFormatter = DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'").withZone(ZoneOffset.UTC)

    fun formatDate(at: Instant): String = millis.format(at)

    /** UTC with any number of fraction digits (.NET writes up to 7), or with an offset. */
    fun parseDate(text: String): Instant? =
        runCatching { Instant.parse(text) }.getOrNull() ?: runCatching { OffsetDateTime.parse(text).toInstant() }.getOrNull()

    private val boardKeys = setOf("schemaVersion", "groups", "items", "activity", "pomodoro", "nowOrder")
    private val itemKeys = setOf(
        "id", "title", "details", "priority", "createdAt", "completedAt", "deadline", "nextActionAt", "sequential",
        "stepDelayMinutes", "groupId", "reminders", "notes", "children",
    )

    /** The board in [text]; anything unexpected (not JSON, the wrong shapes) is a [BoardException]. */
    fun decode(text: String): TaskBoard =
        try {
            decodeOrThrow(text)
        } catch (e: BoardException) {
            throw e
        } catch (e: Exception) {
            throw BoardException("Board file is not valid: ${e.message}")
        }

    private fun decodeOrThrow(text: String): TaskBoard {
        val doc = json.parseToJsonElement(text).jsonObject
        val version = doc["schemaVersion"]?.jsonPrimitive?.intOrNull ?: 1
        if (version > TaskBoard.CURRENT_SCHEMA_VERSION) {
            throw BoardException("Board schema v$version is newer than this app supports (v${TaskBoard.CURRENT_SCHEMA_VERSION}). Please update the app.", isNewerSchema = true)
        }

        val board = TaskBoard(seedDefaultGroups = false)
        for (g in doc.array("groups")) {
            val o = g.jsonObject
            val id = o.uuid("id") ?: continue
            val name = o.string("name")?.trim().orEmpty()
            if (name.isNotEmpty() && board.groups.none { it.id == id }) board.groups.add(TaskGroup(id, name, o.string("color")))
        }
        if (board.groups.isEmpty()) board.seedGroups()
        for (item in doc.array("items")) load(item.jsonObject, board, null)
        for (a in doc.array("activity")) {
            val o = a.jsonObject
            val at = o.date("at") ?: continue
            val itemId = o.uuid("itemId") ?: continue
            board.activity.add(ActivityEntry(at, itemId, o.string("kind").orEmpty(), o.string("summary").orEmpty(), actor(o["actor"])))
        }
        val seen = HashSet<UUID>()
        for (id in doc.array("nowOrder")) {
            val uuid = runCatching { UUID.fromString(id.jsonPrimitive.content) }.getOrNull() ?: continue
            if (board.find(uuid) != null && seen.add(uuid)) board.nowOrder.add(uuid)
        }
        board.pomodoro = doc["pomodoro"]?.takeIf { it !is JsonNull }
        board.extra = unknown(doc, boardKeys)
        return board
    }

    private fun unknown(o: JsonObject, known: Set<String>): JsonObject? =
        o.filterKeys { it !in known }.takeIf { it.isNotEmpty() }?.let(::JsonObject)

    fun encode(board: TaskBoard): String = json.encodeToString(JsonObject.serializer(), buildJsonObject {
        board.extra?.forEach { (key, value) -> put(key, value) }
        put("schemaVersion", TaskBoard.CURRENT_SCHEMA_VERSION)
        put("groups", buildJsonArray {
            for (g in board.groups) add(buildJsonObject {
                put("id", g.id.toString())
                put("name", g.name)
                g.color?.let { put("color", it) }
            })
        })
        put("items", JsonArray(board.items.map(::item)))
        put("activity", JsonArray(board.activity.map { a ->
            buildJsonObject {
                put("at", formatDate(a.at))
                put("itemId", a.itemId.toString())
                put("kind", a.kind)
                put("summary", a.summary)
                put("actor", actorJson(a.actor))
            }
        }))
        board.pomodoro?.let { put("pomodoro", it) }
        if (board.nowOrder.isNotEmpty()) put("nowOrder", JsonArray(board.nowOrder.map { JsonPrimitive(it.toString()) }))
    })

    private fun item(i: WorkItem): JsonObject = buildJsonObject {
        i.extra?.forEach { (key, value) -> put(key, value) }
        put("id", i.id.toString())
        put("title", i.title)
        i.details?.let { put("details", it) }
        put("priority", i.priority.wire)
        put("createdAt", formatDate(i.createdAt))
        i.completedAt?.let { put("completedAt", formatDate(it)) }
        i.deadline?.let { put("deadline", formatDate(it)) }
        i.nextActionAt?.let { put("nextActionAt", formatDate(it)) }
        if (i.sequential) put("sequential", true)
        i.stepDelay?.let { put("stepDelayMinutes", it.toMinutes()) }
        if (i.parent == null) put("groupId", i.ownGroupId.toString())
        if (i.reminders.isNotEmpty()) put("reminders", JsonArray(i.reminders.map { r ->
            buildJsonObject {
                put("id", r.id.toString())
                put("dueAt", formatDate(r.dueAt))
                put("message", r.message)
                put("kind", r.kind.wire)
                r.notifiedAt?.let { put("notifiedAt", formatDate(it)) }
                r.dismissedAt?.let { put("dismissedAt", formatDate(it)) }
            }
        }))
        if (i.notes.isNotEmpty()) put("notes", JsonArray(i.notes.map { n ->
            buildJsonObject {
                put("id", n.id.toString())
                put("at", formatDate(n.at))
                put("text", n.text)
                put("author", actorJson(n.author))
                n.sourceUrl?.let { put("sourceUrl", it) }
                n.sourceTitle?.let { put("sourceTitle", it) }
            }
        }))
        if (i.children.isNotEmpty()) put("children", JsonArray(i.children.map(::item)))
    }

    private fun load(o: JsonObject, board: TaskBoard, parent: WorkItem?) {
        val id = o.uuid("id") ?: throw BoardException("A task has no id.")
        val title = TaskBoard.requireText(o.string("title"), "Task $id has no title.")
        val group = o.uuid("groupId")?.takeIf { g -> board.groups.any { it.id == g } } ?: board.defaultGroupId
        val item = WorkItem(id, title, Priority.of(o.string("priority")), o.date("createdAt") ?: Instant.EPOCH, group)
        item.details = o.string("details")
        item.completedAt = o.date("completedAt")
        item.deadline = o.date("deadline")
        item.nextActionAt = o.date("nextActionAt")
        item.sequential = o["sequential"]?.jsonPrimitive?.booleanOrNull ?: false
        item.stepDelay = o["stepDelayMinutes"]?.jsonPrimitive?.intOrNull?.takeIf { it > 0 }?.let { Duration.ofMinutes(it.toLong()) }
        item.extra = unknown(o, itemKeys)
        for (r in o.array("reminders")) {
            val ro = r.jsonObject
            val rid = ro.uuid("id") ?: continue
            val dueAt = ro.date("dueAt") ?: continue
            item.reminders.add(Reminder(rid, dueAt, ro.string("message").orEmpty(), ReminderKind.of(ro.string("kind")), ro.date("notifiedAt"), ro.date("dismissedAt")))
        }
        for (n in o.array("notes")) {
            val no = n.jsonObject
            val nid = no.uuid("id") ?: continue
            val at = no.date("at") ?: continue
            item.notes.add(Note(nid, at, no.string("text").orEmpty(), actor(no["author"]), no.string("sourceUrl"), no.string("sourceTitle")))
        }
        board.attach(item, parent)
        for (child in o.array("children")) load(child.jsonObject, board, item)
    }

    private fun actor(element: JsonElement?): Actor {
        val o = element as? JsonObject ?: return Actor.USER
        return Actor(ActorKind.of(o.string("kind")), o.string("name"))
    }

    private fun actorJson(actor: Actor) = buildJsonObject {
        put("kind", actor.kind.wire)
        actor.name?.let { put("name", it) }
    }

    private fun JsonObject.array(name: String): List<JsonElement> = (this[name] as? JsonArray) ?: emptyList()

    private fun JsonObject.string(name: String): String? = (this[name] as? JsonPrimitive)?.takeIf { it.isString }?.content

    private fun JsonObject.uuid(name: String): UUID? = string(name)?.let { runCatching { UUID.fromString(it) }.getOrNull() }

    private fun JsonObject.date(name: String): Instant? = string(name)?.let(::parseDate)
}
