package dev.todotracker.app

import android.app.Application
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import dev.todotracker.core.Agenda
import dev.todotracker.core.BoardCodec
import dev.todotracker.core.BoardException
import dev.todotracker.core.BoardFileStore
import dev.todotracker.core.Dashboard
import dev.todotracker.core.NewTask
import dev.todotracker.core.BreakPrompt
import dev.todotracker.core.Breaks
import dev.todotracker.core.PomodoroPhase
import dev.todotracker.core.QuickCaptureParser
import dev.todotracker.core.RunningTimer
import dev.todotracker.core.Snooze
import dev.todotracker.core.SnoozeChoice
import dev.todotracker.core.TaskBoard
import java.io.File
import java.io.IOException
import java.time.Instant
import java.time.ZoneId
import java.util.UUID
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
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
class BoardViewModel(application: Application) : AndroidViewModel(application) {
    private val store = BoardFileStore(File(application.filesDir, "board.json"))
    private val saving = Mutex()
    private var board: TaskBoard

    var dashboard: Dashboard by mutableStateOf(Dashboard(null, emptyList(), emptyList(), emptyMap()))
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

    /** Called when the focus timer's next end changes (the app schedules a notification for it); told at once when set. */
    var onFocusEnd: (Instant?, Boolean) -> Unit = { _, _ -> }
        set(value) {
            field = value
            scheduled?.let { value(it.first, it.second) }
        }

    // The break this app saw (shown, or hidden with "I'm taking it"), and the ones answered.
    private var seenBreak: Instant? = null
    private var dismissedBreak: Instant? = null
    private var dismissedOver: Instant? = null
    private var scheduled: Pair<Instant?, Boolean>? = null

    init {
        board = try {
            store.load().also { status = store.problem }
        } catch (e: BoardException) {
            // Only a board from a newer version of the app: it's never overwritten.
            readOnly = true
            status = "${e.message} Changes are off so your board isn't overwritten."
            TaskBoard()
        }
        refresh()
        viewModelScope.launch {
            var seconds = 0
            while (true) {
                delay(1_000)
                tick(++seconds % 30 == 0)
            }
        }
    }

    fun refresh() {
        if (selectedGroup != null && board.groups.none { it.id == selectedGroup }) selectedGroup = null
        groups = board.groups.map { it.id to it.name }
        now = Instant.now()
        dashboard = Agenda.build(board, now, selectedGroup)
        showTimers()
    }

    /** Every second: the clocks move; the focus timer moves on (saved); every 30 s the lists follow the clock too. */
    private fun tick(lists: Boolean) {
        now = Instant.now()
        if (!readOnly && board.tickPomodoro(now).isNotEmpty()) {
            save(BoardCodec.encode(board))
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
        breakPrompt = when {
            due != null && due.until != dismissedBreak -> due
            due == null && Breaks.over(p.phase, p.isRunning, p.endsAt, p.breakEndedAt, now, seenBreak, dismissedOver) -> BreakPrompt(seenBreak!!, isLong = false, isOver = true)
            else -> null
        }
        // Tell the phone when this phase ends, so it can say so with the app closed.
        val next = (if (p.isRunning) p.endsAt else null) to (p.phase == PomodoroPhase.FOCUS)
        if (next != scheduled) {
            scheduled = next
            onFocusEnd(next.first, next.second)
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
    fun snoozeUntil(id: UUID, at: Instant) = change("Snoozed until ${describe(at)}") {
        if (!at.isAfter(it)) throw BoardException("That time has passed. Pick a later one.")
        board.scheduleNextAction(id, at, notify = true, now = it)
    }

    /** Snoozes until another task is done; it comes back with a reminder then. */
    fun waitFor(id: UUID, afterId: UUID) = change("Waiting for “${titleOf(afterId)}”") { board.waitFor(id, afterId, now = it) }

    /** Tasks it can wait for: the open ones shown (to do now, then waiting), not itself, its subtasks or parents, or what waits for it. */
    fun waitCandidates(id: UUID): List<Pair<UUID, String>> =
        (dashboard.now + dashboard.waiting).map { it.item }.distinctBy { it.id }.filter { board.canWaitFor(id, it.id) }.map { it.id to it.title }

    fun bringBack(id: UUID) = change(null) { board.clearNextAction(id, now = it) }

    fun dismissReminder(id: UUID, reminderId: UUID) = change(null) { board.dismissReminder(id, reminderId, now = it) }

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

    private fun save(text: String) {
        viewModelScope.launch(Dispatchers.IO) {
            saving.withLock {
                try {
                    store.saveText(text)
                } catch (e: IOException) {
                    withContext(Dispatchers.Main) { status = "Couldn't save (${e.message}). Your change is kept on screen; it's saved with the next one." }
                } catch (e: BoardException) {
                    withContext(Dispatchers.Main) { status = e.message }
                }
            }
        }
    }
}
