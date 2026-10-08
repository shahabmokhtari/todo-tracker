package dev.todotracker.core

import java.time.Duration
import java.time.Instant
import java.util.UUID
import kotlinx.serialization.json.JsonElement

/**
 * Tasks, groups (tabs) and the activity timeline. Mirrors TodoTracker.Core.TaskBoard (C#) and TaskBoard (Swift):
 * validate first, then change, and log every change.
 */
class TaskBoard(seedDefaultGroups: Boolean = true) {
    val items: MutableList<WorkItem> = mutableListOf()
    val activity: MutableList<ActivityEntry> = mutableListOf()
    val groups: MutableList<TaskGroup> = mutableListOf()

    /** The Do now order the person arranged (tasks not listed slot in by the automatic rules). */
    val nowOrder: MutableList<UUID> = mutableListOf()

    /** The focus timer as the other apps wrote it (kept as it is: this app doesn't run it yet). */
    var pomodoro: JsonElement? = null

    private val index = HashMap<UUID, WorkItem>()

    init {
        if (seedDefaultGroups) seedGroups()
    }

    val defaultGroupId: UUID get() = groups[0].id

    val allItems: List<WorkItem> get() = items.flatMap { it.selfAndDescendants }

    fun find(id: UUID): WorkItem? = index[id]

    fun get(id: UUID): WorkItem = index[id] ?: throw BoardException("Task $id was not found.")

    fun group(id: UUID): TaskGroup = groups.firstOrNull { it.id == id } ?: throw BoardException("Group $id was not found.")

    fun addTask(spec: NewTask, actor: Actor = Actor.USER, now: Instant): WorkItem {
        val title = requireText(spec.title, "A task title is required.")
        validateStepDelay(spec.stepDelay)
        val parent = spec.parentId?.let { get(it) }
        val groupId = parent?.groupId ?: spec.groupId?.let { group(it).id } ?: defaultGroupId
        val item = WorkItem(title = title, priority = spec.priority, createdAt = now, ownGroupId = groupId)
        item.details = optionalText(spec.details)
        item.deadline = spec.deadline
        item.sequential = spec.sequential
        item.stepDelay = spec.stepDelay
        attach(item, parent)
        log(now, item.id, "created", if (parent != null) "Added \"$title\" to \"${parent.title}\"" else "Created \"$title\"", actor)
        return item
    }

    fun addSteps(parentId: UUID, titles: List<String>, stepDelay: Duration?, actor: Actor = Actor.USER, now: Instant): List<WorkItem> {
        val parent = get(parentId)
        validateStepDelay(stepDelay)
        val clean = titles.map { it.trim() }.filter { it.isNotEmpty() }
        if (clean.isEmpty()) throw BoardException("At least one step title is required.")
        parent.sequential = true
        parent.stepDelay = stepDelay ?: parent.stepDelay
        return clean.map { addTask(NewTask(it, parentId = parent.id), actor, now) }
    }

    fun delete(id: UUID, actor: Actor = Actor.USER, now: Instant) {
        val item = get(id)
        val parent = item.parent
        if (parent != null) parent.children.removeAll { it === item } else items.removeAll { it === item }
        for (removed in item.selfAndDescendants) index.remove(removed.id)
        nowOrder.removeAll { index[it] == null }
        log(now, id, "deleted", "Deleted \"${item.title}\"", actor)
    }

    fun addNote(id: UUID, text: String, actor: Actor = Actor.USER, now: Instant): Note {
        val item = get(id)
        val clean = requireText(text, "A note cannot be empty.", maxLength = 10_000)
        val note = Note(UUID.randomUUID(), now, clean, actor)
        item.notes.add(note)
        log(now, id, "noteAdded", clean, actor)
        return note
    }

    fun complete(id: UUID, actor: Actor = Actor.USER, now: Instant) {
        val item = get(id)
        if (item.isDone) return
        findBlockingStep(item)?.let { throw BoardException("Finish \"${it.title}\" before \"${item.title}\".") }
        for (d in item.selfAndDescendants) if (!d.isDone) d.completedAt = now
        log(now, id, "completed", "Completed \"${item.title}\"", actor)
        advanceSequence(item, actor, now)
    }

    fun reopen(id: UUID, actor: Actor = Actor.USER, now: Instant) {
        val item = get(id)
        val completedAt = item.completedAt ?: return
        item.completedAt = null
        // Undo the cascade from completing a parent: children finished by that same action reopen too.
        for (d in item.selfAndDescendants.drop(1)) if (d.completedAt == completedAt) d.completedAt = null
        for (a in item.ancestors) if (a.isDone) a.completedAt = null
        log(now, id, "reopened", "Reopened \"${item.title}\"", actor)
    }

    /** Defers the item until [at]; with [notify], a reminder fires at that time. */
    fun scheduleNextAction(id: UUID, at: Instant, notify: Boolean = false, message: String? = null, actor: Actor = Actor.USER, now: Instant) {
        val item = get(id)
        item.nextActionAt = at
        dismissPendingScheduleReminders(item, now)
        if (notify) {
            item.reminders.add(Reminder(dueAt = at, message = optionalText(message) ?: defaultReminderMessage(item), kind = ReminderKind.NEXT_ACTION))
        }
        log(now, id, "scheduled", "Next action $at${if (notify) " (with reminder)" else ""}", actor)
    }

    fun clearNextAction(id: UUID, actor: Actor = Actor.USER, now: Instant) {
        val item = get(id)
        item.nextActionAt = null
        dismissPendingScheduleReminders(item, now)
        log(now, id, "scheduled", "Cleared next action; back to Now", actor)
    }

    fun dismissReminder(id: UUID, reminderId: UUID, actor: Actor = Actor.USER, now: Instant) {
        val item = get(id)
        val reminder = item.reminders.firstOrNull { it.id == reminderId } ?: throw BoardException("Reminder $reminderId was not found.")
        if (reminder.dismissedAt != null) return
        reminder.dismissedAt = now
        log(now, id, "reminderDismissed", "Dismissed reminder: ${reminder.message}", actor)
    }

    /** Marks a due reminder as delivered (notification shown) so it isn't delivered again. */
    fun markNotified(itemId: UUID, reminderId: UUID, now: Instant) {
        val item = index[itemId] ?: return
        val reminder = item.reminders.firstOrNull { it.id == reminderId } ?: return
        if (reminder.notifiedAt != null) return
        reminder.notifiedAt = now
        log(now, itemId, "reminderFired", "Reminder: ${reminder.message}", Actor.SYSTEM)
    }

    fun update(id: UUID, title: String? = null, priority: Priority? = null, actor: Actor = Actor.USER, now: Instant) {
        val item = get(id)
        val newTitle = title?.let { requireText(it, "A task title is required.") } ?: item.title
        item.title = newTitle
        if (priority != null) item.priority = priority
        log(now, id, "updated", "Updated \"$newTitle\"", actor)
    }

    fun addGroup(name: String, color: String? = null, actor: Actor = Actor.USER, now: Instant): TaskGroup {
        val clean = requireText(name, "A group name is required.", maxLength = 60)
        if (color != null && !Regex("^#[0-9a-fA-F]{6}$").matches(color)) throw BoardException("Group colors must be #rrggbb.")
        if (groups.any { it.name.equals(clean, ignoreCase = true) }) throw BoardException("A group named \"$clean\" already exists.")
        val group = TaskGroup(name = clean, color = color)
        groups.add(group)
        log(now, BOARD_SCOPE_ID, "groupChanged", "Added group \"$clean\"", actor)
        return group
    }

    internal fun attach(item: WorkItem, parent: WorkItem?) {
        if (index.containsKey(item.id)) throw BoardException("Duplicate task id ${item.id}.")
        index[item.id] = item
        item.parent = parent
        if (parent != null) parent.children.add(item) else items.add(item)
    }

    internal fun seedGroups() {
        groups.add(TaskGroup(name = "Work", color = "#3b82f6"))
        groups.add(TaskGroup(name = "Personal", color = "#22c55e"))
    }

    private fun advanceSequence(completed: WorkItem, actor: Actor, now: Instant) {
        val parent = completed.parent ?: return
        if (!parent.sequential) return
        val next = parent.children.firstOrNull { !it.isDone }
        if (next == null) {
            parent.completedAt = now
            log(now, parent.id, "completed", "Completed \"${parent.title}\" (all steps done)", Actor.SYSTEM)
            advanceSequence(parent, actor, now)
            return
        }
        val delay = parent.stepDelay ?: return
        val gate = now.plus(delay)
        val current = next.nextActionAt
        if (current == null || current.isBefore(gate)) {
            next.nextActionAt = gate
            log(now, next.id, "scheduled", "Unlocks after the step delay", actor)
        }
    }

    private fun dismissPendingScheduleReminders(item: WorkItem, now: Instant) {
        // Replace earlier schedule reminders and silence ones already due (even if delivered), so "Later" really defers.
        for (r in item.reminders) {
            if (r.dismissedAt == null && ((r.kind == ReminderKind.NEXT_ACTION && r.notifiedAt == null) || !r.dueAt.isAfter(now))) {
                r.dismissedAt = now
            }
        }
    }

    private fun log(at: Instant, itemId: UUID, kind: String, summary: String, actor: Actor) {
        activity.add(ActivityEntry(at, itemId, kind, summary, actor))
    }

    companion object {
        const val CURRENT_SCHEMA_VERSION = 1
        const val MAX_TITLE_LENGTH = 300

        /** Activity not tied to a task (group changes) uses the empty id, like Guid.Empty in C#. */
        val BOARD_SCOPE_ID: UUID = UUID(0L, 0L)

        /** The first unfinished earlier step blocking this item or any of its ancestors. */
        fun findBlockingStep(item: WorkItem): WorkItem? {
            var current: WorkItem? = item
            while (current != null) {
                blockingStep(current)?.let { return it }
                current = current.parent
            }
            return null
        }

        /** The earlier open sibling that must be finished first when the parent is sequential. */
        fun blockingStep(item: WorkItem): WorkItem? {
            val parent = item.parent ?: return null
            if (!parent.sequential) return null
            val firstOpen = parent.children.firstOrNull { !it.isDone } ?: return null
            return if (firstOpen === item) null else firstOpen
        }

        /** Trimmed and at most [maxLength] UTF-16 units (like .NET and Swift), else refused. */
        internal fun requireText(value: String?, message: String, maxLength: Int = MAX_TITLE_LENGTH): String {
            val trimmed = (value ?: "").trim()
            if (trimmed.isEmpty()) throw BoardException(message)
            if (trimmed.length > maxLength) throw BoardException("Must be at most $maxLength characters.")
            return trimmed
        }

        internal fun optionalText(value: String?): String? = value?.trim()?.ifEmpty { null }

        private fun defaultReminderMessage(item: WorkItem) = "Time to act on: ${item.title}"

        private fun validateStepDelay(delay: Duration?) {
            if (delay != null && delay.seconds < 60) throw BoardException("Step delay must be at least one minute.")
        }
    }
}
