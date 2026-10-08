package dev.todotracker.core

import java.time.Duration
import java.time.Instant
import java.util.UUID

/** Same values (and JSON names) as the other apps: TodoTracker.Core (C#) and TodoTrackerKit (Swift). */
enum class Priority(val wire: String, val rank: Int) {
    LOW("low", 0), NORMAL("normal", 1), HIGH("high", 2), CRITICAL("critical", 3);

    companion object {
        fun of(text: String?): Priority = entries.firstOrNull { it.wire.equals(text, ignoreCase = true) } ?: NORMAL
    }
}

enum class ActorKind(val wire: String) {
    USER("user"), AGENT("agent"), BROWSER("browser"), TEAMS("teams"), SYSTEM("system"), VAULT("vault"), CONNECTOR("connector");

    companion object {
        /** A kind from a newer version reads as system rather than failing the whole board. */
        fun of(text: String?): ActorKind = entries.firstOrNull { it.wire.equals(text, ignoreCase = true) } ?: SYSTEM
    }
}

data class Actor(val kind: ActorKind, val name: String? = null) {
    companion object {
        val USER = Actor(ActorKind.USER)
        val SYSTEM = Actor(ActorKind.SYSTEM)
    }
}

enum class ItemState {
    ACTIONABLE, WAITING, LOCKED, CONTAINER, DONE;

    val wire: String get() = name.lowercase()
}

enum class ReminderKind(val wire: String) {
    MANUAL("manual"), NEXT_ACTION("nextAction");

    companion object {
        fun of(text: String?): ReminderKind = entries.firstOrNull { it.wire == text } ?: MANUAL
    }
}

/** A change the board refuses (not found, invalid, a step that must wait): the message says why. */
class BoardException(message: String, val isNewerSchema: Boolean = false) : Exception(message)

class Reminder(
    val id: UUID = UUID.randomUUID(),
    val dueAt: Instant,
    val message: String,
    val kind: ReminderKind,
    var notifiedAt: Instant? = null,
    var dismissedAt: Instant? = null,
) {
    val isPending: Boolean get() = dismissedAt == null

    fun isDue(now: Instant): Boolean = dismissedAt == null && !dueAt.isAfter(now)
}

data class Note(
    val id: UUID,
    val at: Instant,
    val text: String,
    val author: Actor,
    val sourceUrl: String? = null,
    val sourceTitle: String? = null,
)

/** One change on the timeline; the kind stays a string so newer kinds from other apps round-trip. */
data class ActivityEntry(val at: Instant, val itemId: UUID, val kind: String, val summary: String, val actor: Actor)

class TaskGroup(val id: UUID = UUID.randomUUID(), var name: String, var color: String?)

class WorkItem(
    val id: UUID = UUID.randomUUID(),
    var title: String,
    var priority: Priority,
    val createdAt: Instant,
    internal var ownGroupId: UUID,
) {
    var details: String? = null
    var completedAt: Instant? = null
    var deadline: Instant? = null
    var nextActionAt: Instant? = null

    /** Snoozed until this task is done ("after task X"); see [waitingFor]. */
    var afterId: UUID? = null

    /** The board it's on (set when it's added), to look up what it waits for. */
    internal var board: TaskBoard? = null
    var sequential: Boolean = false
    var stepDelay: Duration? = null
    var parent: WorkItem? = null
        internal set
    val children: MutableList<WorkItem> = mutableListOf()
    val reminders: MutableList<Reminder> = mutableListOf()
    val notes: MutableList<Note> = mutableListOf()

    /** What other apps keep on a task that this one doesn't use (tags, labels, time…): written back as it was. */
    var extra: kotlinx.serialization.json.JsonObject? = null

    val groupId: UUID get() = parent?.groupId ?: ownGroupId
    val isDone: Boolean get() = completedAt != null
    val hasOpenChildren: Boolean get() = children.any { !it.isDone }

    /** The open task this one waits for (null: none, it's done, or it's gone). */
    val waitingFor: WorkItem? get() = afterId?.let { board?.find(it) }?.takeIf { !it.isDone }

    val ancestors: List<WorkItem>
        get() {
            val result = mutableListOf<WorkItem>()
            var current = parent
            while (current != null) {
                result.add(current)
                current = current.parent
            }
            return result
        }

    val selfAndDescendants: List<WorkItem> get() = listOf(this) + children.flatMap { it.selfAndDescendants }
}

data class NewTask(
    val title: String,
    val parentId: UUID? = null,
    val groupId: UUID? = null,
    val priority: Priority = Priority.NORMAL,
    val details: String? = null,
    val deadline: Instant? = null,
    val sequential: Boolean = false,
    val stepDelay: Duration? = null,
)
