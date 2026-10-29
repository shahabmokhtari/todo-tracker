package dev.todotracker.app

import android.app.Application
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import dev.todotracker.core.Agenda
import dev.todotracker.core.BoardCodec
import dev.todotracker.core.BoardException
import dev.todotracker.core.BoardFileStore
import dev.todotracker.core.Dashboard
import dev.todotracker.core.Energy
import dev.todotracker.core.NewTask
import dev.todotracker.core.BreakPrompt
import dev.todotracker.core.Breaks
import dev.todotracker.core.PhaseEnd
import dev.todotracker.core.PomodoroEventKind
import dev.todotracker.core.PomodoroPhase
import dev.todotracker.core.QuickCaptureParser
import dev.todotracker.core.Priority
import dev.todotracker.core.PomodoroSettings
import dev.todotracker.core.Reminders
import dev.todotracker.core.RunningTimer
import dev.todotracker.core.Snooze
import dev.todotracker.core.SnoozeChoice
import dev.todotracker.core.TaskBoard
import dev.todotracker.core.TaskChanges
import dev.todotracker.core.TaskLinks
import dev.todotracker.core.TaskSearch
import dev.todotracker.core.WorkItem
import java.time.Duration
import java.io.File
import java.io.IOException
import java.time.Instant
import java.time.ZoneId
import java.util.UUID
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** Something just done that can be taken back (shown with an Undo button). */
data class Undoable(val message: String, val undo: () -> Unit)

/** The focus timer as the screen shows it. */
data class FocusState(
    val phase: PomodoroPhase = PomodoroPhase.IDLE,
    val running: Boolean = false,
    val endsAt: Instant? = null,
    val remaining: java.time.Duration = java.time.Duration.ZERO,
    val itemTitle: String? = null,
    val completed: Int = 0,
)

/**
 * The board on this phone: every change is saved right away (written off the screen's thread, one at a time); the
 * lists follow the clock (waiting tasks come back).
 */
class BoardViewModel(application: Application) : AndroidViewModel(application), BoardHub.LiveBoard {
    private val store = BoardFileStore(BoardHub.file(application))
    private val prefs = application.getSharedPreferences("settings", android.content.Context.MODE_PRIVATE)
    @OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
    private val saving = Dispatchers.IO.limitedParallelism(1)
    private var board: TaskBoard

    var dashboard: Dashboard by mutableStateOf(Dashboard(null, emptyList(), emptyList(), emptyMap()))
        private set

    /** Today's energy line ("⚡ 3 of 10 planned today."), when there's something to say. */
    var energyToday: String? by mutableStateOf(null)
        private set

    /** Today's plan takes more energy than there is in a day (the line is a warning then). */
    var energyOver: Boolean by mutableStateOf(false)
        private set

    /** How much energy there is in a day. */
    var dailyEnergy: Int by mutableStateOf(Energy.DEFAULT_PER_DAY)
        private set

    /** Goes up with every redraw of the lists, so what shows a task (its details) redraws too. */
    var version: Int by mutableIntStateOf(0)
        private set

    /** What's typed in the search box ("" shows the lists as usual). */
    var query: String by mutableStateOf("")
        private set

    /** The tasks the search finds: open ones first, then done ones (null: not searching). */
    var searchResults: List<WorkItem>? by mutableStateOf(null)
        private set

    /** Light, dark, or as the phone is ("system"); kept on this phone. */
    var theme: String by mutableStateOf(prefs.getString("theme", "system") ?: "system")
        private set

    /** How long focus sessions and breaks last. */
    val focusSettings: PomodoroSettings get() = board.pomodoro.settings

    /** Each group's color (#rrggbb), or null. */
    var groupColors: Map<UUID, String?> by mutableStateOf(emptyMap())
        private set

    // The reminder alarm set last (set again when it changes, and back in the app).
    private var scheduledReminder: Instant? = null
    private var reminderSet = false

    /** What got done today (newest first): something to look back on. */
    var doneToday: List<WorkItem> by mutableStateOf(emptyList())
        private set
    var groups: List<Pair<UUID, String>> by mutableStateOf(emptyList())
        private set
    var selectedGroup: UUID? by mutableStateOf(null)
        private set
    var status: String? by mutableStateOf(null)
    var undoable: Undoable? by mutableStateOf(null)
    var readOnly: Boolean by mutableStateOf(false)
        private set

    /** The clock the screen shows (moves every second: the focus timer, the task timer, the break). */
    var now: Instant by mutableStateOf(Instant.now())
        private set

    /** The focus timer as it is now (a copy, so the screen redraws when it changes). */
    var focus: FocusState by mutableStateOf(FocusState())
        private set

    /** The task being timed, and since when (null: no timer runs). */
    var timer: RunningTimer? by mutableStateOf(null)
        private set

    /** The break to show full screen now (or "Break's over"), or null. */
    var breakPrompt: BreakPrompt? by mutableStateOf(null)
        private set

    /** Whether the app is on screen (it tells the person itself; off screen, a notification does). */
    var appVisible: Boolean = true

    /** A focus session or a break ran out while the app was off screen: say so now (once per end; the alarm may too). */
    var onPhaseEnded: (PhaseEnd) -> Unit = {}

    /** Called when the focus timer's coming ends change (the app sets alarms for them); told at once when set. */
    var onPhaseEnds: (List<PhaseEnd>) -> Unit = {}
        set(value) {
            field = value
            scheduled?.let { value(it) }
        }

    // The break this app saw (shown, or hidden with "I'm taking it"), and the ones answered.
    private var seenBreak: Instant? = null
    private var dismissedBreak: Instant? = null
    private var dismissedOver: Instant? = null
    private var scheduled: List<PhaseEnd>? = null

    init {
        board = try {
            synchronized(BoardHub.lock) { store.load() }.also { status = store.problem }
        } catch (e: BoardException) {
            // Only a board from a newer version of the app: it's never overwritten.
            readOnly = true
            status = "${e.message} Changes are off so your board isn't overwritten."
            TaskBoard()
        }
        // While the app runs, alarms and notification buttons change this board (not the file behind its back).
        BoardHub.live = this
        refresh()
        viewModelScope.launch {
            var seconds = 0
            while (true) {
                delay(1_000)
                tick(++seconds % 30 == 0)
            }
        }
    }

    /** Back in the app: the alarms are set again (allowing exact alarms meanwhile makes them ring on time). */
    fun resume() {
        scheduled = null
        reminderSet = false
        refresh()
    }

    override fun onCleared() {
        if (BoardHub.live === this) BoardHub.live = null
        super.onCleared()
    }

    fun refresh() {
        now = Instant.now()
        // Reminders due now are told (once): with the app open, closed, or the phone locked.
        if (!readOnly && ReminderAlarm.deliver(getApplication(), board, now)) save(BoardCodec.encode(board))
        if (selectedGroup != null && board.groups.none { it.id == selectedGroup }) selectedGroup = null
        groups = board.groups.map { it.id to it.name }
        groupColors = board.groups.associate { it.id to it.color }
        dashboard = Agenda.build(board, now, selectedGroup)
        doneToday = Agenda.doneToday(board, now, ZoneId.systemDefault(), selectedGroup)
        energyToday = Energy.today(board, now, ZoneId.systemDefault())
        energyOver = Energy.overToday(board, now, ZoneId.systemDefault())
        dailyEnergy = board.dailyEnergy
        searchResults = TaskSearch.parse(query).takeIf { !it.isEmpty }?.let { search ->
            search.apply(board).filter { selectedGroup == null || it.groupId == selectedGroup }.sortedBy { it.isDone }
        }
        val nextReminder = if (readOnly) null else Reminders.nextAt(board, now)
        if (nextReminder != scheduledReminder || !reminderSet) {
            scheduledReminder = nextReminder
            reminderSet = true
            ReminderAlarm.schedule(getApplication(), nextReminder)
        }
        version++
        showTimers()
    }

    /** Searches as you type: words in the title or details, is:done, is:archived, group:work, #tag… */
    fun search(text: String) {
        query = text
        refresh()
    }

    /** An alarm or a notification button changed the board while the app runs. */
    override fun applyOutside(action: (TaskBoard, Instant) -> Unit): Result<Unit> {
        if (readOnly) return Result.failure(BoardException("Your board is from a newer version of the app, so changes are off to protect it."))
        return try {
            action(board, Instant.now())
            save(BoardCodec.encode(board))
            Result.success(Unit)
        } catch (e: BoardException) {
            Result.failure(e)
        } finally {
            refresh()
        }
    }

    /** How much energy a task takes per hour (1 to 3; null: not said). */
    fun setEnergy(id: UUID, level: Int?) = change(if (level == null) "Energy: not said" else "Energy: ${"⚡".repeat(level)} an hour") { board.setEnergy(id, level, now = it) }

    /** How much energy there is in a day (every app uses it). */
    fun setDailyEnergy(perDay: Int?) = change("Energy in a day: ${perDay ?: Energy.DEFAULT_PER_DAY}") { board.setEnergyPerDay(perDay) }

    /** Not done after all (from Done today): back in Do now. */
    fun reopen(id: UUID) { change(null) { board.reopen(id, now = it) } }

    /** Every second: the clocks move; the focus timer moves on (saved); every 30 s the lists follow the clock too. */
    private fun tick(lists: Boolean) {
        now = Instant.now()
        val events = if (readOnly) emptyList() else board.tickPomodoro(now)
        // A timer left running for half a day was forgotten: it ends (at 12 hours), so the task can be timed again.
        val closed = if (readOnly) 0 else board.closeForgottenTimers(now)
        if (events.isNotEmpty() || closed > 0) {
            save(BoardCodec.encode(board))
            // Running in the background (a locked phone): this moved the timer on before the alarm could ring.
            events.lastOrNull()?.takeIf { !appVisible }?.let { onPhaseEnded(PhaseEnd(it.at, it.kind == PomodoroEventKind.FOCUS_COMPLETED)) }
            refresh()
        } else if (lists) {
            refresh()
        } else {
            showTimers()
        }
    }

    private fun showTimers() {
        val p = board.pomodoro
        focus = FocusState(p.phase, p.isRunning, p.endsAt, p.remaining(now), p.itemId?.let { board.find(it)?.title }, p.completedFocusCount)
        timer = board.runningTimer(now)
        val due = Breaks.due(p, now)
        if (due != null && due.until != seenBreak) seenBreak = due.until
        // A break that ran out while the app was asleep (the phone said so): back in the app, it asks about the next one.
        if (due == null && seenBreak == null && p.phase == PomodoroPhase.IDLE) seenBreak = p.breakEndedAt
        breakPrompt = when {
            due != null && due.until != dismissedBreak -> due
            due == null && Breaks.over(p.phase, p.isRunning, p.endsAt, p.breakEndedAt, now, seenBreak, dismissedOver) -> BreakPrompt(seenBreak!!, isLong = false, isOver = true)
            else -> null
        }
        // Tell the phone when the phases end, so it can say so with the app closed: a focus session, and the break that
        // follows it (the app may be asleep when the break starts), or a break.
        val next = Breaks.phaseEnds(p)
        if (next != scheduled) {
            scheduled = next
            onPhaseEnds(next)
        }
    }

    /** How long the focus timer's current phase lasts in all. */
    fun focusTotal(): java.time.Duration = board.pomodoro.settings.durationOf(board.pomodoro.phase)

    /** What "Start next focus" picks up (null: no task). */
    val nextFocusTitle: String? get() = (board.nextFocusItem() ?: dashboard.focus?.item)?.title

    // ---- Focus timer and task timer ----

    fun startFocus(id: UUID?) = change("Focus started") { board.startFocus(id ?: dashboard.focus?.item?.id, now = it) }

    fun pauseFocus() = change(null) { board.pauseFocus(it) }

    fun resumeFocus() = change(null) { board.resumeFocus(it) }

    fun skipFocus() = change(null) { board.skipFocus(it) }

    fun resetFocus() = change(null) { board.resetFocus(it) }

    /** "I'm taking it" (the break goes on), or "Not now" when the break is over. */
    fun takeBreak() {
        if (breakPrompt?.isOver == true) dismissedOver = seenBreak else dismissedBreak = Breaks.due(board.pomodoro, Instant.now())?.until ?: dismissedBreak
        showTimers()
    }

    /** Back to work now: the break ends. */
    fun skipBreak() {
        dismissedBreak = Breaks.due(board.pomodoro, Instant.now())?.until ?: dismissedBreak
        skipFocus()
    }

    /** The next focus session now (during the break, or when it's over): on the last task, else today's top one. */
    fun startNextFocus() {
        dismissedBreak = Breaks.due(board.pomodoro, Instant.now())?.until ?: dismissedBreak
        dismissedOver = seenBreak
        val fallback = dashboard.focus?.item?.id
        change("Focus started") { board.startNextFocus(fallback, now = it) }
    }

    /** Times the task (any other timer stops), or stops it when it's the one timing. */
    fun toggleTimer(id: UUID) {
        if (board.runningTimer(Instant.now())?.item?.id == id) {
            change("Timer stopped") { board.stopTimer(it) }
        } else {
            change("Timing “${titleOf(id).orEmpty()}”") { board.startTimer(id, now = it) }
        }
    }

    fun stopTimer() = change("Timer stopped") { board.stopTimer(it) }

    fun select(group: UUID?) {
        selectedGroup = group
        refresh()
    }

    fun count(group: UUID?): Int =
        if (group == null) dashboard.groupCounts.values.sumOf { it.now } else dashboard.groupCounts[group]?.now ?: 0

    fun titleOf(id: UUID): String? = board.find(id)?.title

    /** The task a todotracker:// link asked to open (shown over Today until closed). */
    var openTaskId: UUID? by mutableStateOf(null)

    /** A clicked todotracker://task/<id> link: that task opens (anything else is ignored). */
    fun openLink(link: String?) {
        val target = TaskLinks.target(link)
        if (target is TaskLinks.Target.Task) {
            if (board.find(target.id) == null) status = "That task isn't on this phone" else openTaskId = target.id
        }
    }

    fun itemOf(id: UUID): WorkItem? = board.find(id)

    /** When it was done (a repeating task that moved on: when its last time was). */
    fun doneAt(item: WorkItem): Instant? = Agenda.doneAt(board, item)

    /** Quick capture: "Call mum @tomorrow !high due:3d". True when it was added. */
    fun capture(text: String): Boolean = change("Added") { now ->
        val capture = QuickCaptureParser.parse(text, now, ZoneId.systemDefault())
        val item = board.addTask(NewTask(capture.title, groupId = selectedGroup, priority = capture.priority, deadline = capture.deadline), now = now)
        capture.nextActionAt?.let { board.scheduleNextAction(item.id, it, notify = true, now = now) }
    }

    /** Done, with a way back (a mis-tap shouldn't lose a task). */
    fun complete(id: UUID) {
        val title = titleOf(id) ?: return
        if (change(null) { board.complete(id, now = it) }) {
            undoable = Undoable("Done: $title") { change(null) { board.reopen(id, now = it) } }
        }
    }

    /** The quick snooze choices right now (the same in every app). */
    fun snoozeChoices(): List<SnoozeChoice> = Snooze.choices(Instant.now(), ZoneId.systemDefault())

    /** "today 17:00", "Fri 14:30", "Mon 12 Jan, 9:00". */
    fun describe(at: Instant): String = Snooze.describe(at, Instant.now(), ZoneId.systemDefault())

    /** Snoozes until a time: a quick choice or one picked on the calendar (refused when it's already past). */
    fun snoozeUntil(id: UUID, at: Instant) {
        val undo = snoozeUndo(id)
        if (change(null) {
                if (!at.isAfter(it)) throw BoardException("That time has passed. Pick a later one.")
                board.scheduleNextAction(id, at, notify = true, now = it)
            }
        ) {
            undoable = Undoable("Snoozed until ${describe(at)}", undo)
        }
    }

    /** Snoozes until another task is done; it comes back with a reminder then. */
    fun waitFor(id: UUID, afterId: UUID) {
        val undo = snoozeUndo(id)
        if (change(null) { board.waitFor(id, afterId, now = it) }) undoable = Undoable("Waiting for “${titleOf(afterId)}”", undo)
    }

    /** Puts the task's snooze back as it was before (Undo on "Snoozed until…"): its wait, or back in Do now. */
    private fun snoozeUndo(id: UUID): () -> Unit {
        val item = board.find(id)
        val before = item?.nextActionAt
        val after = item?.afterId
        val reminded = before != null && item.reminders.any { it.kind == dev.todotracker.core.ReminderKind.NEXT_ACTION && it.dueAt == before && it.dismissedAt == null }
        return {
            change(null) { now ->
                when {
                    after != null && board.canWaitFor(id, after) -> board.waitFor(id, after, now = now)
                    before != null && before.isAfter(now) -> board.scheduleNextAction(id, before, notify = reminded, now = now)
                    else -> board.clearNextAction(id, now = now)
                }
            }
        }
    }

    // ---- A task's details ----

    /** Saves what the details changed (a bad value is refused with why, and nothing changes). */
    fun edit(id: UUID, changes: TaskChanges, message: String? = "Saved"): Boolean = change(message) { board.update(id, changes, now = it) }

    fun setPriority(id: UUID, priority: Priority) = edit(id, TaskChanges(priority = priority), "Priority: ${priority.wire}")

    fun setDeadline(id: UUID, at: Instant?) = edit(id, if (at == null) TaskChanges(clearDeadline = true) else TaskChanges(deadline = at), if (at == null) "No deadline" else "Due ${describe(at)}")

    /** A repeat rule in words ("every weekday"); empty stops it repeating. */
    fun setRepeat(id: UUID, rule: String) = edit(id, TaskChanges(repeat = rule.trim()), if (rule.isBlank()) "Doesn't repeat any more" else "Repeats ${rule.trim()}")

    fun setSequential(id: UUID, inOrder: Boolean) = edit(id, TaskChanges(sequential = inOrder), if (inOrder) "Steps in order" else "Steps in any order")

    /** Hours between steps (null or 0: none). */
    fun setStepDelay(id: UUID, hours: Double?) =
        edit(id, if (hours == null || hours <= 0) TaskChanges(clearStepDelay = true) else TaskChanges(stepDelay = Duration.ofMinutes(Math.round(hours * 60))), "Saved")

    fun addSubtask(parentId: UUID, title: String) = change("Added") { board.addTask(NewTask(title, parentId = parentId), now = it) }

    /** Steps done one after another, [hours] apart (one per line). */
    fun addSteps(parentId: UUID, lines: String, hours: Double?): Boolean {
        val titles = lines.lines().map { it.trim() }.filter { it.isNotEmpty() }
        val delay = hours?.takeIf { it > 0 }?.let { Duration.ofMinutes(Math.round(it * 60)) }
        return change("Added ${titles.size} step${if (titles.size == 1) "" else "s"}") { board.addSteps(parentId, titles, delay, now = it) }
    }

    /** A reminder at a time (the task stays where it is). */
    fun addReminder(id: UUID, at: Instant, message: String?) = change("Reminder set for ${describe(at)}") {
        if (!at.isAfter(it)) throw BoardException("That time has passed. Pick a later one.")
        board.addReminder(id, at, message, now = it)
    }

    fun archive(id: UUID) {
        val title = titleOf(id) ?: return
        if (change(null) { board.archive(id, now = it) }) undoable = Undoable("Archived: $title") { change(null) { board.unarchive(id, now = it) } }
    }

    fun unarchive(id: UUID) = change("Back in Done") { board.unarchive(id, now = it) }

    /** Deletes the task and its subtasks for good (asked first, on screen). */
    fun delete(id: UUID) {
        val title = titleOf(id) ?: return
        if (openTaskId == id) openTaskId = board.find(id)?.parent?.id
        change("Deleted “$title”") { board.delete(id, now = it) }
    }

    fun moveToGroup(id: UUID, groupId: UUID) = change("Moved to ${board.groups.firstOrNull { it.id == groupId }?.name}") { board.moveToGroup(id, groupId, now = it) }

    fun groupOf(id: UUID): UUID? = board.find(id)?.groupId

    fun groupName(id: UUID): String? = board.groups.firstOrNull { it.id == id }?.name

    // ---- Groups ----

    fun updateGroup(id: UUID, name: String, color: String?) = change("Saved") { board.updateGroup(id, name, color, now = it) }

    /** Deletes a group; its tasks move to [moveTo] (none are lost). */
    fun deleteGroup(id: UUID, moveTo: UUID): Boolean {
        val name = groupName(id)
        return change("Deleted “$name”; its tasks are in ${groupName(moveTo)}") { board.deleteGroup(id, moveTo, now = it) }
    }

    // ---- Settings ----

    fun chooseTheme(value: String) {
        theme = if (value == "light" || value == "dark") value else "system"
        prefs.edit().putString("theme", theme).apply()
    }

    fun setFocusLengths(focus: Int, shortBreak: Int, longBreak: Int) = change("Saved") { board.setFocusLengths(focus, shortBreak, longBreak) }

    /** Tasks it can wait for: the open ones shown (to do now, then waiting), not itself, its subtasks or parents, or what waits for it. */
    fun waitCandidates(id: UUID): List<Pair<UUID, String>> =
        (dashboard.now + dashboard.waiting).map { it.item }.distinctBy { it.id }.filter { board.canWaitFor(id, it.id) }.map { it.id to it.title }

    fun bringBack(id: UUID) = change(null) { board.clearNextAction(id, now = it) }

    fun dismissReminder(id: UUID, reminderId: UUID) = change(null) { board.dismissReminder(id, reminderId, now = it) }

    /** Do something else first: the task moves one down Do now (the next one becomes the focus). */
    fun skip(id: UUID) {
        val ids = dashboard.now.map { it.item.id }.toMutableList()
        val i = ids.indexOf(id)
        if (i < 0 || i + 1 >= ids.size) return
        java.util.Collections.swap(ids, i, i + 1)
        change(null) { board.arrangeNow(ids, now = it) }
    }

    fun addNote(id: UUID, text: String) = change("Note saved") { board.addNote(id, text, now = it) }

    fun addGroup(name: String) = change(null) { selectedGroup = board.addGroup(name, now = it).id }

    /** Applies a change and saves it; false (with the reason shown) when the board refused it. */
    private fun change(message: String?, action: (Instant) -> Unit): Boolean {
        if (readOnly) {
            status = "Your board is from a newer version of the app, so changes are off to protect it."
            return false
        }
        return try {
            action(Instant.now())
            save(BoardCodec.encode(board))
            status = message
            true
        } catch (e: BoardException) {
            status = e.message
            false
        } finally {
            refresh()
        }
    }

    /** Saves off the screen's thread, one at a time and in order (a newer board is never overwritten by an older one). */
    private fun save(text: String) {
        viewModelScope.launch(saving) {
            try {
                synchronized(BoardHub.lock) { store.saveText(text) }
            } catch (e: IOException) {
                withContext(Dispatchers.Main) { status = "Couldn't save (${e.message}). Your change is kept on screen; it's saved with the next one." }
            } catch (e: BoardException) {
                withContext(Dispatchers.Main) { status = e.message }
            }
        }
    }
}
