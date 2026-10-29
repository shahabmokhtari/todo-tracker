package dev.todotracker.core

import java.time.Instant
import java.util.UUID
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive

/**
 * What the phone asks a connected computer to do: the same REST API the web app and the Mac app use (ServerAPI in
 * Swift), and only what the computer lets phones do (its phone allow-list). Field names are the server's.
 */
sealed interface RemoteAction {
    data class Capture(val text: String, val groupId: UUID?) : RemoteAction
    data class AddSubtask(val parentId: UUID, val title: String) : RemoteAction
    data class Complete(val id: UUID) : RemoteAction
    data class Reopen(val id: UUID) : RemoteAction
    data class Archive(val id: UUID) : RemoteAction
    data class Unarchive(val id: UUID) : RemoteAction
    data class Schedule(val id: UUID, val at: Instant, val notify: Boolean = true) : RemoteAction
    data class BringBack(val id: UUID) : RemoteAction
    data class WaitFor(val id: UUID, val afterId: UUID) : RemoteAction
    data class DismissReminder(val id: UUID, val reminderId: UUID) : RemoteAction
    data class AddReminder(val id: UUID, val at: Instant, val message: String?) : RemoteAction
    data class ArrangeNow(val ids: List<UUID>) : RemoteAction
    data class AddNote(val id: UUID, val text: String) : RemoteAction
    data class Update(val id: UUID, val changes: TaskChanges) : RemoteAction
    data class AddSteps(val parentId: UUID, val titles: List<String>, val stepDelayMinutes: Long?) : RemoteAction
    data class Delete(val id: UUID) : RemoteAction
    data class MoveToGroup(val id: UUID, val groupId: UUID) : RemoteAction
    data class AddGroup(val name: String) : RemoteAction
    data class UpdateGroup(val id: UUID, val name: String, val color: String?) : RemoteAction
    data class DeleteGroup(val id: UUID, val moveTo: UUID) : RemoteAction
    data class SetDailyEnergy(val perDay: Int?) : RemoteAction
    /** start, pause, resume, skip, reset, next (with the task, or none). */
    data class Pomodoro(val command: String, val itemId: UUID? = null) : RemoteAction
    data class StartTimer(val id: UUID) : RemoteAction
    data object StopTimer : RemoteAction
}

/** An HTTP request to the computer; the body is JSON with sorted keys (null: none). */
data class PhoneRequest(val method: String, val path: String, val body: String? = null)

object PhoneApi {
    /** The whole board, in the JSON the apps share (BoardCodec), with an ETag. */
    val export = PhoneRequest("GET", "/api/export")
    val health = PhoneRequest("GET", "/health")

    fun claim(code: String, name: String) = post("/api/phones/claim", mapOf("code" to code, "name" to phoneName(name, "Android phone")))

    /** The longest name the computer keeps for a phone. */
    const val MAX_NAME = 60

    /**
     * The name the computer lists this phone by: what was typed (spaces tidied, at most [MAX_NAME] characters), or
     * [fallback] (the phone's own name) when nothing was.
     */
    fun phoneName(typed: String?, fallback: String): String {
        val tidy = typed?.trim()?.replace(Regex("\\s+"), " ").orEmpty()
        val cut = tidy.ifEmpty { fallback.trim() }.ifEmpty { "Android phone" }.take(MAX_NAME)
        // Not half an emoji at the end.
        return (if (cut.isNotEmpty() && cut.last().isHighSurrogate()) cut.dropLast(1) else cut).trim()
    }

    fun request(action: RemoteAction): PhoneRequest = when (action) {
        is RemoteAction.Capture -> post("/api/capture", mapOf("text" to action.text, "groupId" to action.groupId?.toString()))
        is RemoteAction.AddSubtask -> post("/api/items", mapOf("title" to action.title, "parentId" to action.parentId.toString()))
        is RemoteAction.Complete -> post("/api/items/${action.id}/complete")
        is RemoteAction.Reopen -> post("/api/items/${action.id}/reopen")
        is RemoteAction.Archive -> post("/api/items/${action.id}/archive")
        is RemoteAction.Unarchive -> post("/api/items/${action.id}/unarchive")
        is RemoteAction.Schedule -> post("/api/items/${action.id}/schedule", mapOf("at" to BoardCodec.formatDate(action.at), "notify" to action.notify))
        is RemoteAction.BringBack -> post("/api/items/${action.id}/schedule", mapOf("clear" to true))
        is RemoteAction.WaitFor -> post("/api/items/${action.id}/after", mapOf("afterId" to action.afterId.toString()))
        is RemoteAction.DismissReminder -> post("/api/items/${action.id}/reminders/${action.reminderId}/dismiss")
        is RemoteAction.AddReminder -> post("/api/items/${action.id}/reminders", mapOf("at" to BoardCodec.formatDate(action.at), "message" to action.message))
        is RemoteAction.ArrangeNow -> post("/api/now/order", mapOf("ids" to action.ids.map { it.toString() }))
        is RemoteAction.AddNote -> post("/api/items/${action.id}/notes", mapOf("text" to action.text))
        is RemoteAction.Update -> PhoneRequest("PATCH", "/api/items/${action.id}", json(patch(action.changes)))
        is RemoteAction.AddSteps -> post("/api/items/${action.parentId}/steps", mapOf("titles" to action.titles, "stepDelayMinutes" to action.stepDelayMinutes))
        is RemoteAction.Delete -> PhoneRequest("DELETE", "/api/items/${action.id}")
        is RemoteAction.MoveToGroup -> post("/api/items/${action.id}/move", mapOf("groupId" to action.groupId.toString()))
        is RemoteAction.AddGroup -> post("/api/groups", mapOf("name" to action.name))
        // No color: the group has none (the server clears it).
        is RemoteAction.UpdateGroup -> PhoneRequest("PATCH", "/api/groups/${action.id}", json(mapOf("name" to action.name, "color" to action.color)))
        is RemoteAction.DeleteGroup -> PhoneRequest("DELETE", "/api/groups/${action.id}?moveTo=${action.moveTo}")
        is RemoteAction.SetDailyEnergy -> PhoneRequest("PUT", "/api/energy", json(mapOf("perDay" to action.perDay)))
        is RemoteAction.Pomodoro -> post("/api/pomodoro/${action.command}", action.itemId?.let { mapOf("itemId" to it.toString()) })
        is RemoteAction.StartTimer -> post("/api/timer/start", mapOf("itemId" to action.id.toString()))
        RemoteAction.StopTimer -> post("/api/timer/stop")
    }

    /** A task's changes as PatchItemRequest: "" stops a repeat, energy 0 removes it. */
    private fun patch(c: TaskChanges): Map<String, Any?> = mapOf(
        "title" to c.title,
        "details" to c.details,
        "priority" to c.priority?.wire,
        "deadline" to c.deadline?.let(BoardCodec::formatDate),
        "clearDeadline" to c.clearDeadline.takeIf { it },
        "sequential" to c.sequential,
        "stepDelayMinutes" to c.stepDelay?.toMinutes(),
        "clearStepDelay" to c.clearStepDelay.takeIf { it },
        "repeat" to (if (c.clearRepeat) "" else c.repeat),
        "energy" to (if (c.clearEnergy) 0 else c.energy),
    )

    private fun post(path: String, body: Map<String, Any?>? = null) = PhoneRequest("POST", path, body?.let(::json))

    /** Sorted keys, nulls left out: only strings, numbers, booleans and lists of strings go in. */
    fun json(values: Map<String, Any?>): String =
        JsonObject(values.filterValues { it != null }.toSortedMap().mapValues { (_, v) -> element(v) }).toString()

    private fun element(value: Any?): JsonElement = when (value) {
        null -> JsonNull
        is String -> JsonPrimitive(value)
        is Number -> JsonPrimitive(value)
        is Boolean -> JsonPrimitive(value)
        is List<*> -> JsonArray(value.map(::element))
        else -> JsonPrimitive(value.toString())
    }
}
