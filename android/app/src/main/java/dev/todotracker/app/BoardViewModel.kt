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
import dev.todotracker.core.PomodoroEventKind
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
    @OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
    private val saving = Dispatchers.IO.limitedParallelism(1)
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
        val end = p.endsAt.takeIf { p.isRunning }
        val next = when {
            end == null -> emptyList()
            p.phase == PomodoroPhase.FOCUS -> {
                val long = (p.completedFocusCount + 1) % p.settings.focusesBeforeLongBreak.coerceAtLeast(1) == 0
                listOf(PhaseEnd(end, true), PhaseEnd(end.plus(p.settings.durationOf(if (long) PomodoroPhase.LONG_BREAK else PomodoroPhase.SHORT_BREAK)), false))
            }
            else -> listOf(PhaseEnd(end, false))
        }
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

    /** Saves off the screen's thread, one at a time and in order (a newer board is never overwritten by an older one). */
    private fun save(text: String) {
        viewModelScope.launch(saving) {
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
