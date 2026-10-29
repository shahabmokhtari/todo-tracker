package dev.todotracker.core

import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.time.ZonedDateTime
import java.time.format.DateTimeFormatter
import java.util.Locale
import java.util.UUID

/**
 * Tasks, groups (tabs) and the activity timeline. Mirrors TodoTracker.Core.TaskBoard (C#) and TaskBoard (Swift):
 * validate first, then change, and log every change.
 */
class TaskBoard(seedDefaultGroups: Boolean = true) {
    val items: MutableList<WorkItem> = mutableListOf()
    val activity: MutableList<ActivityEntry> = mutableListOf()
    val groups: MutableList<TaskGroup> = mutableListOf()

    /** The labels defined on the board (name and color), in the order they were made. */
    val labels: MutableList<LabelDefinition> = mutableListOf()

    /** The label called [name] (case doesn't matter), or null. */
    fun findLabel(name: String?): LabelDefinition? = labels.firstOrNull { it.name.equals(name?.trim(), ignoreCase = true) }

    /** The label called [name], made with the next unused color if there's none yet (as TodoTracker.Core does). */
    internal fun ensureLabel(name: String): LabelDefinition {
        findLabel(name)?.let { return it }
        val used = labels.map { it.color.lowercase() }.toSet()
        val color = LABEL_PALETTE.firstOrNull { it !in used } ?: LABEL_PALETTE[labels.size % LABEL_PALETTE.size]
        return LabelDefinition(name, color).also { labels.add(it) }
    }

    /** The Do now order the person arranged (tasks not listed slot in by the automatic rules). */
    val nowOrder: MutableList<UUID> = mutableListOf()

    /** When Do now was last arranged: a reminder going off after it brings its task back to the top. */
    var nowArrangedAt: Instant? = null

    /** How much energy there is in a day (null: [Energy.DEFAULT_PER_DAY]); kept with the board, so every app uses it. */
    var energyPerDay: Int? = null
        internal set

    val dailyEnergy: Int get() = energyPerDay ?: Energy.DEFAULT_PER_DAY

    /** How much energy a task takes per hour (1 to 3; null: not said). */
    fun setEnergy(id: UUID, level: Int?, actor: Actor = Actor.USER, now: Instant) {
        val item = get(id)
        if (level != null && level !in Energy.LEVELS) throw BoardException("Energy is 1 to 3 (⚡ to ⚡⚡⚡ an hour).")
        item.energy = level
        log(now, id, "updated", "Updated \"${item.title}\"", actor)
    }

    /** How much energy there is in a day (null: back to the default). */
    fun setEnergyPerDay(perDay: Int?) {
        if (perDay != null && perDay !in 1..Energy.MAX_PER_DAY) throw BoardException("A day's energy is 1 to ${Energy.MAX_PER_DAY} ⚡.")
        energyPerDay = perDay
    }

    /** The focus timer (Pomodoro): its phase, when it ends, and the task it times. */
    val pomodoro = PomodoroTimer()

    /** What the other apps keep in the focus timer that this one doesn't use: written back as it was. */
    var pomodoroExtra: kotlinx.serialization.json.JsonObject? = null

    /** Board-level data other apps keep that this one doesn't use (labels…): written back as it was. */
    var extra: kotlinx.serialization.json.JsonObject? = null

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
        // A timer on what's deleted ends now. A subtask's time stays with its task (the work happened).
        stopTimers(item.selfAndDescendants, now)
        if (parent != null) {
            parent.children.removeAll { it === item }
            parent.timeEntries.addAll(item.selfAndDescendants.flatMap { it.timeEntries })
            parent.timeEntries.sortBy { it.start }
        } else {
            items.removeAll { it === item }
        }
        for (removed in item.selfAndDescendants) index.remove(removed.id)
        if (pomodoro.itemId?.let { index[it] } == null) pomodoro.detachItem()
        nowOrder.removeAll { index[it] == null }
        // Tasks waiting for what's gone come back, like when it's done.
        releaseWaiters(now, item.selfAndDescendants.associate { it.id to it.title })
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
        if (tryRollForward(item, actor, now)) {
            // What waited for this time of it comes back.
            releaseWaiters(now, finished = item.id)
            return
        }
        for (d in item.selfAndDescendants) if (!d.isDone) d.completedAt = now
        stopTimers(item.selfAndDescendants, now)
        log(now, id, "completed", "Completed \"${item.title}\"", actor)
        advanceSequence(item, actor, now)
        releaseWaiters(now)
    }

    /**
     * Snoozes a task until another one is done ("after task X"); then it comes back with a reminder. Replaces a snooze
     * until a time. Same rules as TodoTracker.Core.TaskBoard.WaitFor.
     */
    fun waitFor(id: UUID, afterId: UUID, actor: Actor = Actor.USER, now: Instant) {
        val item = get(id)
        val other = get(afterId)
        if (other === item) throw BoardException("A task can't wait for itself.")
        waitProblem(item, other)?.let { throw BoardException(it) }
        item.afterId = other.id
        item.nextActionAt = null
        dismissPendingScheduleReminders(item, now)
        log(now, id, "scheduled", "Waits until \"${other.title}\" is done", actor)
    }

    /** Whether [id] may wait for [afterId] (what the picker offers). */
    fun canWaitFor(id: UUID, afterId: UUID): Boolean {
        val item = find(id) ?: return false
        val other = find(afterId) ?: return false
        return other !== item && waitProblem(item, other) == null
    }

    /** Tasks whose wait ended (what they waited for is done, or was just deleted) come back, with a reminder saying why. */
    private fun releaseWaiters(now: Instant, deleted: Map<UUID, String> = emptyMap(), finished: UUID? = null) {
        for (waiter in allItems) {
            val afterId = waiter.afterId ?: continue
            val other = index[afterId]
            val why = when {
                other != null && afterId == finished -> "\"${other.title}\" is done"
                // Only one deleted just now: a task merely missing (not synced yet) doesn't end a wait.
                other == null -> deleted[afterId]?.let { "\"$it\" was deleted" } ?: continue
                other.isDone -> "\"${other.title}\" is done"
                else -> continue
            }
            waiter.afterId = null
            if (!waiter.isDone) {
                waiter.reminders.add(Reminder(dueAt = now, message = "$why: back to \"${waiter.title}\"", kind = ReminderKind.NEXT_ACTION))
                log(now, waiter.id, "scheduled", "Back: $why", Actor.SYSTEM)
            }
        }
    }

    fun reopen(id: UUID, actor: Actor = Actor.USER, now: Instant) {
        val item = get(id)
        val completedAt = item.completedAt
        if (completedAt == null) {
            if (wasRolledForward(item)) {
                undoRollForward(item, actor, now)
            } else {
                // The last step of a repeating task moved the whole task on (and opened the step again): "not done
                // after all" on that step puts the task back as it was, with this step open.
                rolledBy(item)?.let { undoRollForward(it, actor, now, keepOpen = item) }
            }
            return
        }
        item.completedAt = null
        item.root.archivedAt = null
        val reopened = mutableListOf(item)
        // Undo the cascade from completing a parent: children finished by that same action reopen too.
        for (d in item.selfAndDescendants.drop(1)) if (d.completedAt == completedAt) { d.completedAt = null; reopened.add(d) }
        for (a in item.ancestors) if (a.isDone) { a.completedAt = null; reopened.add(a) }
        for (back in reopened) bringBack(back, now)
        log(now, id, "reopened", "Reopened \"${item.title}\"", actor)
    }

    /**
     * Reopening means "I want to do this again": back in Do now, not hidden behind a snooze or a wait set before it was
     * done (it looked as if Reopen did nothing). Only a snooze or wait still in effect is cleared, with its pending
     * snooze reminder; reminders set by hand stay.
     */
    private fun bringBack(item: WorkItem, now: Instant) {
        val snoozed = item.nextActionAt?.isAfter(now) == true
        if (!snoozed && item.afterId == null) return
        val hidden = snoozed || item.waitingFor != null
        item.nextActionAt = null
        item.afterId = null
        if (!hidden) return
        for (r in item.reminders) if (r.kind == ReminderKind.NEXT_ACTION && r.dismissedAt == null) r.dismissedAt = now
    }

    /** Defers the item until [at]; with [notify], a reminder fires at that time. */
    fun scheduleNextAction(id: UUID, at: Instant, notify: Boolean = false, message: String? = null, actor: Actor = Actor.USER, now: Instant) {
        val item = get(id)
        item.nextActionAt = at
        item.afterId = null
        dismissPendingScheduleReminders(item, now)
        if (notify) {
            item.reminders.add(Reminder(dueAt = at, message = optionalText(message) ?: defaultReminderMessage(item), kind = ReminderKind.NEXT_ACTION))
        }
        log(now, id, "scheduled", "Next action $at${if (notify) " (with reminder)" else ""}", actor)
    }

    fun clearNextAction(id: UUID, actor: Actor = Actor.USER, now: Instant) {
        val item = get(id)
        item.nextActionAt = null
        item.afterId = null
        dismissPendingScheduleReminders(item, now)
        log(now, id, "scheduled", "Cleared next action; back to Now", actor)
    }

    /**
     * Records the order of the tasks someone sees in Do now (TaskBoard.ArrangeNow in C#, same rules): only the places
     * those tasks already had are reused, so arranging one group's tab doesn't move the other groups' tasks.
     */
    fun arrangeNow(ids: List<UUID>, now: Instant) {
        if (ids.toSet().size != ids.size) throw BoardException("Each task can appear only once.")
        ids.forEach { get(it) }
        val arranged = ids.toSet()
        val slots = nowOrder.indices.filter { nowOrder[it] in arranged }
        val result: MutableList<UUID>
        if (slots.size == ids.size) {
            // Same set of places: keep them, fill them in the new order.
            result = nowOrder.toMutableList()
            slots.forEachIndexed { k, slot -> result[slot] = ids[k] }
        } else {
            val start = nowOrder.indexOfFirst { it in arranged }
            val at = if (start < 0) 0 else nowOrder.take(start).count { it !in arranged }
            result = nowOrder.filter { it !in arranged }.toMutableList()
            result.addAll(at, ids)
        }
        nowOrder.clear()
        nowOrder.addAll(result.filter { find(it)?.isDone == false })
        nowArrangedAt = now
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

    fun update(id: UUID, title: String? = null, priority: Priority? = null, actor: Actor = Actor.USER, now: Instant) =
        update(id, TaskChanges(title = title, priority = priority), actor, now)

    /**
     * Changes what [changes] says (fields left null stay as they are). Everything is checked first, so a bad value
     * changes nothing. Same rules as TodoTracker.Core.TaskBoard.Update.
     */
    fun update(id: UUID, changes: TaskChanges, actor: Actor = Actor.USER, now: Instant) {
        val item = get(id)
        val title = changes.title?.let { requireText(it, "A task title is required.") } ?: item.title
        val details = changes.details?.let {
            if (it.trim().length > MAX_TEXT_LENGTH) throw BoardException("Details must be at most $MAX_TEXT_LENGTH characters.")
            optionalText(it)
        }
        validateStepDelay(changes.stepDelay)
        val repeat = changes.repeat?.takeIf { it.isNotBlank() }?.let {
            try {
                Recurrence.parse(it)
            } catch (e: Recurrence.RuleException) {
                throw BoardException(e.message ?: "That repeat rule isn't understood.")
            }
        }
        if (changes.energy != null && changes.energy !in Energy.LEVELS) throw BoardException("Energy is 1 to 3 (⚡ to ⚡⚡⚡ an hour).")

        item.title = title
        if (changes.details != null) item.details = details
        item.priority = changes.priority ?: item.priority
        item.deadline = if (changes.clearDeadline) null else changes.deadline ?: item.deadline
        item.sequential = changes.sequential ?: item.sequential
        item.stepDelay = if (changes.clearStepDelay) null else changes.stepDelay ?: item.stepDelay
        // An empty rule stops it repeating (like the repeat box in the other apps).
        item.repeat = if (changes.clearRepeat || changes.repeat?.isBlank() == true) null else repeat ?: item.repeat
        item.energy = if (changes.clearEnergy) null else changes.energy ?: item.energy
        log(now, id, "updated", "Updated \"$title\"", actor)
    }

    /** A reminder at [dueAt] (the task stays where it is; when it's due, the task comes to the top of Do now). */
    fun addReminder(id: UUID, dueAt: Instant, message: String?, actor: Actor = Actor.USER, now: Instant): Reminder {
        val item = get(id)
        val clean = optionalText(message)
        if (clean != null && clean.length > MAX_TITLE_LENGTH) throw BoardException("Must be at most $MAX_TITLE_LENGTH characters.")
        val reminder = Reminder(dueAt = dueAt, message = clean ?: defaultReminderMessage(item), kind = ReminderKind.MANUAL)
        item.reminders.add(reminder)
        log(now, id, "reminderAdded", "Reminder at $dueAt: ${reminder.message}", actor)
        return reminder
    }

    // ---- Archive (TaskBoard.Time.cs in C#) ----------------------------------------------------------------------

    /** Puts a finished top-level task away: out of every list, kept (search finds it with is:archived). */
    fun archive(id: UUID, actor: Actor = Actor.USER, now: Instant) {
        val item = get(id)
        if (item.parent != null) throw BoardException("Subtasks are archived with their task.")
        if (!item.isDone) throw BoardException("Finish \"${item.title}\" before archiving it.")
        if (item.archivedAt != null) return
        item.archivedAt = now
        log(now, id, "archived", "Archived \"${item.title}\"", actor)
    }

    fun unarchive(id: UUID, actor: Actor = Actor.USER, now: Instant) {
        val item = get(id)
        if (item.archivedAt == null) return
        item.archivedAt = null
        log(now, id, "archived", "Brought \"${item.title}\" back from the archive", actor)
    }

    // ---- Groups ---------------------------------------------------------------------------------------------------

    fun addGroup(name: String, color: String? = null, actor: Actor = Actor.USER, now: Instant): TaskGroup {
        val clean = validateGroup(name, color, except = null)
        val group = TaskGroup(name = clean, color = color)
        groups.add(group)
        log(now, BOARD_SCOPE_ID, "groupChanged", "Added group \"$clean\"", actor)
        return group
    }

    /** A new name and color for a group (null color: none). */
    fun updateGroup(groupId: UUID, name: String, color: String?, actor: Actor = Actor.USER, now: Instant) {
        val group = group(groupId)
        val clean = validateGroup(name, color, except = group)
        group.name = clean
        group.color = color
        log(now, BOARD_SCOPE_ID, "groupChanged", "Updated group \"$clean\"", actor)
    }

    /** Deletes a group; its tasks move to [moveTasksTo] (nothing is lost). */
    fun deleteGroup(groupId: UUID, moveTasksTo: UUID, actor: Actor = Actor.USER, now: Instant) {
        val group = group(groupId)
        if (groups.size == 1) throw BoardException("At least one group is required.")
        val target = group(moveTasksTo)
        if (target === group) throw BoardException("Choose a different group to move the tasks into.")
        for (root in items) if (root.ownGroupId == group.id) root.ownGroupId = target.id
        groups.remove(group)
        log(now, BOARD_SCOPE_ID, "groupChanged", "Deleted group \"${group.name}\"", actor)
    }

    /** Moves a top-level task (with its subtasks) to another group. */
    fun moveToGroup(id: UUID, groupId: UUID, actor: Actor = Actor.USER, now: Instant) {
        val item = get(id)
        val group = group(groupId)
        if (item.parent != null) throw BoardException("Only top-level tasks can change group; subtasks follow their parent.")
        item.ownGroupId = group.id
        log(now, id, "moved", "Moved \"${item.title}\" to ${group.name}", actor)
    }

    private fun validateGroup(name: String, color: String?, except: TaskGroup?): String {
        val clean = requireText(name, "A group name is required.", maxLength = 60)
        if (color != null && !Regex("^#[0-9a-fA-F]{6}$").matches(color)) throw BoardException("Group colors must be #rrggbb.")
        if (groups.any { it !== except && it.name.equals(clean, ignoreCase = true) }) throw BoardException("A group named \"$clean\" already exists.")
        return clean
    }

    /** How long a focus session and its breaks last, in minutes (1 to [MAX_FOCUS_MINUTES]). */
    fun setFocusLengths(focusMinutes: Int, shortBreakMinutes: Int, longBreakMinutes: Int) {
        for (m in listOf(focusMinutes, shortBreakMinutes, longBreakMinutes)) {
            if (m !in 1..MAX_FOCUS_MINUTES) throw BoardException("Focus and break lengths are 1 to $MAX_FOCUS_MINUTES minutes.")
        }
        pomodoro.settings = pomodoro.settings.copy(focusMinutes = focusMinutes, shortBreakMinutes = shortBreakMinutes, longBreakMinutes = longBreakMinutes)
    }

    // ---- Time: one timer runs at a time; a focus session on a task times it (TaskBoard.Time.cs) ----------------

    /** The timer that runs (the newest, if sync brought two together), or null. A forgotten one doesn't count. */
    fun runningTimer(now: Instant? = null): RunningTimer? =
        allItems.flatMap { item -> item.timeEntries.filter { it.isRunning }.map { RunningTimer(item, it) } }
            .filter { now == null || Duration.between(it.entry.start, now) <= FORGOTTEN_AFTER }
            .maxByOrNull { it.entry.start }

    /** Starts timing a task; any other timer stops. */
    fun startTimer(id: UUID, actor: Actor = Actor.USER, now: Instant): TimeEntry {
        val item = get(id)
        if (item.isDone) throw BoardException("\"${item.title}\" is already done.")
        runningTimer(now)?.takeIf { it.item === item }?.let { return it.entry }
        // A focus session on another task no longer counts for it (the session itself goes on).
        if (pomodoro.phase == PomodoroPhase.FOCUS && pomodoro.itemId != null && pomodoro.itemId != item.id) pomodoro.detachItem()
        val entry = startTimerCore(item, TimeSource.MANUAL, now)
        log(now, item.id, "timeLogged", "Started the timer on \"${item.title}\"", actor)
        return entry
    }

    /** Ends every timer (any device) that has run longer than [FORGOTTEN_AFTER], at that length. Returns how many ended. */
    fun closeForgottenTimers(now: Instant): Int {
        var closed = 0
        for (entry in allItems.flatMap { it.timeEntries }) {
            if (entry.isRunning && Duration.between(entry.start, now) > FORGOTTEN_AFTER) {
                entry.end = entry.start.plus(FORGOTTEN_AFTER)
                closed++
            }
        }
        return closed
    }

    /** Stops the timer (every running one); the newest is returned. */
    fun stopTimer(now: Instant): RunningTimer? {
        val newest = runningTimer()
        stopTimers(allItems, now)
        return newest
    }

    /** Ends the running timers among [items]: each at the start of a newer one, the newest now. */
    private fun stopTimers(items: List<WorkItem>, now: Instant) {
        val running = items.flatMap { it.timeEntries }.filter { it.isRunning }.sortedBy { it.start }
        running.forEachIndexed { i, entry ->
            var end = if (i < running.size - 1) running[i + 1].start else now
            if (end.isBefore(entry.start)) end = entry.start
            // A forgotten timer ends at the most it counts, not hours (or days) later.
            entry.end = if (Duration.between(entry.start, end) > FORGOTTEN_AFTER) entry.start.plus(FORGOTTEN_AFTER) else end
        }
    }

    private fun stopFocusTimer(at: Instant) = stopTimers(allItems.filter { i -> i.timeEntries.any { it.isRunning && it.source == TimeSource.FOCUS } }, at)

    private fun startTimerCore(item: WorkItem, source: TimeSource, now: Instant): TimeEntry {
        stopTimers(allItems, now)
        val entry = TimeEntry(start = now, source = source, device = THIS_DEVICE)
        item.timeEntries.add(entry)
        // Starting work on a card moves it to Doing (the board column).
        val root = item.ancestors.lastOrNull() ?: item
        if (!root.isDone) root.stage = Stage.DOING
        return entry
    }

    // ---- Focus timer -----------------------------------------------------------------------------------------

    /** Starts a focus session (on a task, timed as focus time on it). */
    fun startFocus(itemId: UUID?, actor: Actor = Actor.USER, now: Instant) {
        val item = itemId?.let { get(it) }
        if (item != null && item.isDone) throw BoardException("\"${item.title}\" is already done.")
        stopFocusTimer(now)
        pomodoro.startFocus(now, item?.id)
        if (item != null) {
            startTimerCore(item, TimeSource.FOCUS, now)
            log(now, item.id, "focusStarted", "Focus started on \"${item.title}\"", actor)
        }
    }

    fun pauseFocus(now: Instant) {
        if (pomodoro.phase == PomodoroPhase.FOCUS) stopFocusTimer(now)
        pomodoro.pause(now)
    }

    fun resumeFocus(now: Instant) {
        val resuming = pomodoro.phase == PomodoroPhase.FOCUS && !pomodoro.isRunning
        pomodoro.resume(now)
        // A timer started by hand meanwhile keeps running: the session goes on without timing its task.
        val item = pomodoro.itemId?.let { index[it] }
        if (resuming && item != null && !item.isDone && runningTimer(now) == null) startTimerCore(item, TimeSource.FOCUS, now)
    }

    /** A session that has already run out ends on time first, so Skip skips the break that followed it. */
    fun skipFocus(now: Instant) {
        tickPomodoro(now)
        if (pomodoro.phase == PomodoroPhase.FOCUS) stopFocusTimer(now)
        pomodoro.skip(now)
    }

    fun resetFocus(now: Instant) {
        stopFocusTimer(now)
        pomodoro.reset()
    }

    fun tickPomodoro(now: Instant): List<PomodoroEvent> {
        val events = mutableListOf<PomodoroEvent>()
        while (true) {
            val evt = pomodoro.tick(now) ?: break
            events.add(evt)
            if (evt.kind == PomodoroEventKind.FOCUS_COMPLETED) {
                // The session ended at its end time, whenever this runs (after sleep, say).
                stopFocusTimer(evt.at)
                evt.itemId?.let { index[it] }?.let { log(evt.at, it.id, "focusCompleted", "Focus session completed on \"${it.title}\"", Actor.SYSTEM) }
            }
        }
        return events
    }

    /** The task "Start next focus" picks up: the last session's, if it's still there and open. */
    fun nextFocusItem(): WorkItem? = pomodoro.itemId?.let { index[it] }?.takeIf { !it.isDone }

    /**
     * "Start next focus": ends the break and starts a session on the last session's task, else [fallbackItemId]. A
     * session that just ran out counts first; one still running is never restarted.
     */
    fun startNextFocus(fallbackItemId: UUID?, actor: Actor = Actor.USER, now: Instant): WorkItem? {
        tickPomodoro(now)
        if (pomodoro.phase == PomodoroPhase.FOCUS) throw BoardException("A focus session is already going.")
        val item = nextFocusItem() ?: fallbackItemId?.let { index[it] }?.takeIf { !it.isDone }
        startFocus(item?.id, actor, now)
        return item
    }

    internal fun attach(item: WorkItem, parent: WorkItem?) {
        if (index.containsKey(item.id)) throw BoardException("Duplicate task id ${item.id}.")
        index[item.id] = item
        item.parent = parent
        item.board = this
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
            // Every step done: a repeating task moves on to its next time (its steps open again).
            if (tryRollForward(parent, actor, now)) {
                releaseWaiters(now, finished = parent.id)
                return
            }
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

    // ---- Repeating tasks (same rules as TodoTracker.Core.TaskBoard.Repeat.cs) ------------------------------------

    /** Where the user lives: a repeating task's next time is a calendar day there. */
    var zone: ZoneId = ZoneId.systemDefault()

    /**
     * Done with this time of a repeating task: it moves on to its next time instead of finishing (deadline to that day
     * at the same time; it waits until then, the same days before the deadline and at the same time as it waited
     * before, else from the start of that day), its subtasks open again and this time's reminders and timer end.
     * False when it doesn't repeat (any more): then it's simply done.
     */
    private fun tryRollForward(item: WorkItem, actor: Actor, now: Instant): Boolean {
        val repeat = item.repeat ?: return false
        // A step of an in-order task doesn't repeat on its own (it would hold up the steps after it for good).
        if (repeat.count == 1 || item.parent?.sequential == true) return false
        val today = now.atZone(zone).toLocalDate()
        val scheduled = item.deadline ?: item.nextActionAt
        val anchor = if (repeat.whenDone || scheduled == null) today else scheduled.atZone(zone).toLocalDate()
        // Its day is fixed from its first time on (month ends, leap days and snoozes don't move it).
        val rule = repeat.pinnedTo(anchor)
        val next = rule.next(anchor, if (anchor.isAfter(today)) anchor else today) ?: return false

        item.repeatUndo = RepeatUndo(item.deadline, item.nextActionAt, item.plannedAt)
        val waitedUntil = item.nextActionAt
        val reminded = waitedUntil != null && item.reminders.any { it.kind == ReminderKind.NEXT_ACTION && it.dueAt == waitedUntil }
        val due = item.deadline
        val lead = if (due != null && waitedUntil != null) {
            maxOf(0L, due.atZone(zone).toLocalDate().toEpochDay() - waitedUntil.atZone(zone).toLocalDate().toEpochDay())
        } else {
            0L
        }
        // A planned block moves along too (as many days from the deadline, at the same time).
        item.plannedAt?.let { planned ->
            val day = planned.atZone(zone)
            item.plannedAt = ZonedDateTime.of(next.plusDays(day.toLocalDate().toEpochDay() - anchor.toEpochDay()), day.toLocalTime(), zone).toInstant()
        }
        if (due != null) item.deadline = ZonedDateTime.of(next, due.atZone(zone).toLocalTime(), zone).toInstant()
        val back = if (waitedUntil != null) {
            ZonedDateTime.of(next.minusDays(lead), waitedUntil.atZone(zone).toLocalTime(), zone).toInstant()
        } else {
            next.atStartOfDay(zone).toInstant()
        }
        item.nextActionAt = back
        item.afterId = null
        dismissPendingScheduleReminders(item, now)
        for (r in item.reminders) if (r.dismissedAt == null && r.dueAt.isBefore(back)) r.dismissedAt = now
        if (reminded && back.isAfter(now)) item.reminders.add(Reminder(dueAt = back, message = defaultReminderMessage(item), kind = ReminderKind.NEXT_ACTION))
        for (d in item.selfAndDescendants.drop(1)) d.completedAt = null
        stopTimers(item.selfAndDescendants, now)
        // Doing ends with this time of it (the board column).
        if (item.parent == null && item.stage == Stage.DOING) item.stage = Stage.NEXT
        item.repeat = rule.countedDown()
        log(now, item.id, "completed", "Completed \"${item.title}\" $ROLLED_MARKER${next.format(DateTimeFormatter.ofPattern("EEE d MMM", Locale.ENGLISH))})", actor)
        return true
    }

    /** Whether this repeating task's last change was moving on to its next time (so "not done after all" can undo it). */
    fun wasRolledForward(item: WorkItem): Boolean =
        item.repeat != null && !item.isDone &&
            activity.lastOrNull { it.itemId == item.id && (it.kind == "completed" || it.kind == "reopened") }?.kind == "completed"

    /** The repeating task that completing [step] (its last step) moved on to its next time, or null. */
    private fun rolledBy(step: WorkItem): WorkItem? {
        val doneAt = activity.lastOrNull { it.itemId == step.id && (it.kind == "completed" || it.kind == "reopened") }
            ?.takeIf { it.kind == "completed" }?.at ?: return null
        return step.ancestors.firstOrNull { a -> wasRolledForward(a) && isNewest(a, doneAt) }
    }

    /**
     * Whether moving [item] on (at [at]) is the last thing that happened to it or its subtasks: once its next time has
     * begun (a step done, a reminder set…), reopening a step no longer undoes it.
     */
    private fun isNewest(item: WorkItem, at: Instant): Boolean {
        val family = item.selfAndDescendants.map { it.id }.toSet()
        val roll = activity.indexOfLast { it.itemId == item.id && it.kind == "completed" }
        return roll >= 0 && activity[roll].at == at && activity.indexOfLast { it.itemId in family } == roll
    }

    /** When the task last moved on before its latest time, not counting times that were undone (null: never). */
    private fun previousRoll(item: WorkItem): Instant? {
        val entries = activity.filter { it.itemId == item.id && (it.kind == "completed" || it.kind == "reopened") }
        val rolls = ArrayDeque<Instant>()
        for (entry in entries.dropLast(1)) {
            if (entry.kind == "completed") rolls.addLast(entry.at) else rolls.removeLastOrNull()
        }
        return rolls.lastOrNull()
    }

    /** A "completed" entry of a repeating task that moved on (it stayed open): every app writes this marker. */
    private fun isRolled(entry: ActivityEntry) = entry.kind == "completed" && entry.summary.contains(ROLLED_MARKER)

    /**
     * "Not done after all" on a repeating task that moved on: due a time earlier, back in Do now, with what moving on
     * undid as it was: the subtasks done before (all but [keepOpen]) done again, and the reminders it put away pending
     * again.
     */
    private fun undoRollForward(item: WorkItem, actor: Actor, now: Instant, keepOpen: WorkItem? = null) {
        val rule = item.repeat ?: return
        val rolledAt = activity.lastOrNull { it.itemId == item.id && it.kind == "completed" }?.at
        if (rolledAt != null) {
            val since = previousRoll(item)
            val keep = keepOpen?.selfAndDescendants?.map { it.id }?.toSet() ?: emptySet()
            for (d in item.selfAndDescendants.drop(1)) {
                if (d.id in keep || d.isDone) continue
                // Done by itself (or with a parent below this task) since the time before it moved on, and not reopened.
                // A repeating subtask that moved on by itself wasn't finished: that doesn't count.
                val lineage = (listOf(d) + d.ancestors.takeWhile { it !== item }).map { it.id }.toSet()
                val last = activity.lastOrNull {
                    it.itemId in lineage && (it.kind == "completed" || it.kind == "reopened") && !isRolled(it) &&
                        !it.at.isAfter(rolledAt) && (since == null || it.at.isAfter(since))
                }
                if (last?.kind == "completed") d.completedAt = last.at
            }
            // Put away when it moved on (not the reminder for its next time, which ends below).
            for (r in item.reminders) if (r.dismissedAt == rolledAt && r.dueAt.isBefore(item.nextActionAt ?: Instant.MAX)) r.dismissedAt = null
        }
        for (r in item.reminders) if (r.kind == ReminderKind.NEXT_ACTION && r.dismissedAt == null && r.dueAt.isAfter(now)) r.dismissedAt = now
        val undo = item.repeatUndo
        if (undo != null) {
            item.deadline = undo.deadline
            item.nextActionAt = undo.nextActionAt
            item.plannedAt = undo.plannedAt ?: item.plannedAt
        } else {
            val due = item.deadline
            if (due != null) {
                val day = due.atZone(zone).toLocalDate()
                rule.previous(day, day)?.let { item.deadline = ZonedDateTime.of(it, due.atZone(zone).toLocalTime(), zone).toInstant() }
            }
            item.nextActionAt = null
        }
        item.repeatUndo = null
        item.repeat = rule.countedUp()
        log(now, item.id, "reopened", "Reopened \"${item.title}\" (not done after all)", actor)
    }

    companion object {
        const val CURRENT_SCHEMA_VERSION = 1
        const val MAX_TITLE_LENGTH = 300
        const val MAX_TEXT_LENGTH = 10_000
        const val MAX_FOCUS_MINUTES = 180

        /** What every app writes in the "completed" entry of a repeating task that moved on (it stays open). */
        const val ROLLED_MARKER = "(repeats: next "

        /** A timer running longer than this was forgotten (or its device is gone): it counts this long at most. */
        val FORGOTTEN_AFTER: Duration = Duration.ofHours(12)

        /** This device, as time entries name it. */
        const val THIS_DEVICE = "Android"

        /** Label colors, accessible on light and dark backgrounds; new labels take the next unused one. */
        val LABEL_PALETTE = listOf("#7c3aed", "#0ea5e9", "#10b981", "#f59e0b", "#ef4444", "#ec4899", "#14b8a6", "#64748b")

        /** Activity not tied to a task (group changes) uses the empty id, like Guid.Empty in C#. */
        val BOARD_SCOPE_ID: UUID = UUID(0L, 0L)

        /** Why a task can't wait for another, or null when it can. */
        internal fun waitProblem(item: WorkItem, other: WorkItem): String? {
            if (item.isDone) return "\"${item.title}\" is already done."
            if (other.isDone) return "\"${other.title}\" is already done."
            // Its own subtasks or parents would never get done while it waits.
            val family = (item.selfAndDescendants + item.ancestors).map { it.id }.toSet()
            if (other.id in family) return "\"${item.title}\" can't wait for \"${other.title}\", which is part of it."
            // Nor would a circle: follow what holds "other" up (what it, its parents and its open subtasks wait for, and
            // its open subtasks themselves); reaching this task's family means they'd wait for each other.
            val seen = HashSet<UUID>()
            val queue = ArrayDeque(listOf(other))
            while (queue.isNotEmpty()) {
                val next = queue.removeFirst()
                if (!seen.add(next.id)) continue
                if (next.id in family) return "\"${other.title}\" is already waiting for \"${item.title}\": they'd wait for each other."
                queue.addAll((listOf(next) + next.ancestors).mapNotNull { it.waitingFor } + next.children.filter { !it.isDone })
            }
            return null
        }

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
