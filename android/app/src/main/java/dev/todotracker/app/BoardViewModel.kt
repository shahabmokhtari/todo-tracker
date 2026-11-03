package dev.todotracker.app

import android.app.Application
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import dev.todotracker.core.Agenda
import dev.todotracker.core.BoardException
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
import dev.todotracker.core.LocalBoard
import dev.todotracker.core.sync.PhoneSync
import dev.todotracker.core.sync.PhoneSyncOutcome
import dev.todotracker.core.sync.SyncBackend
import dev.todotracker.core.sync.SyncSchedule
import dev.todotracker.core.sync.SyncSettings
import dev.todotracker.core.sync.SyncStatus
import dev.todotracker.core.vault.VaultStore
import java.time.Duration
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
 * The board on this phone: its tasks folder (the one store, [BoardHub.store]). A change shows at once and is saved
 * there right away, off the screen's thread. While the app is on screen ([whileOnScreen]) the lists follow the clock
 * (waiting tasks come back) and, syncing, the phone syncs on launch and resume, 5 s after a change and every minute;
 * with the app closed, WorkManager syncs (SyncHub).
 */
class BoardViewModel(application: Application) : AndroidViewModel(application), BoardHub.LiveBoard {
    // The tasks folder, opened off the screen's thread (the first time, board.json moves in): null until then.
    private var opened: LocalBoard.Opened? = null
    private val store: VaultStore? get() = opened?.store

    /** While the tasks move from the old board file into the tasks folder (the first start): what to say. */
    var moving: String? by mutableStateOf(
        if (LocalBoard.needsMove(application.filesDir)) "Moving your tasks into a tasks folder on this phone…" else null,
    )
        private set
    private val prefs = application.getSharedPreferences("settings", android.content.Context.MODE_PRIVATE)
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

    /** Shaking the phone opens Send feedback (on unless switched off in Settings); kept on this phone. */
    var shakeToFeedback: Boolean by mutableStateOf(prefs.getBoolean("shake-to-feedback", true))
        private set

    fun chooseShakeToFeedback(on: Boolean) {
        shakeToFeedback = on
        prefs.edit().putBoolean("shake-to-feedback", on).apply()
    }

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

    // Changes on their way to the tasks folder: until the last is saved, the board on screen shows them already.
    private var writing = 0

    // Reminders being told and marked in the tasks folder right now.
    private var delivering = false

    // ---- Sync with your other devices: its state (the actions are at the end) ----

    private var phoneSync: PhoneSync? = null
    private val syncSchedule = SyncSchedule()

    var syncOn: Boolean by mutableStateOf(false)
        private set
    var syncBusy: Boolean by mutableStateOf(false)
        private set
    var syncStatus: SyncStatus by mutableStateOf(SyncStatus.EMPTY)
        private set

    /** Something to say about the last sync that isn't an error ("hasn't arrived yet"). */
    var syncNotice: String? by mutableStateOf(null)
        private set

    /** Why turning sync on (or Sync now) didn't work. */
    var syncMessage: String? by mutableStateOf(null)
        private set
    var syncLibrary: String by mutableStateOf(SyncSettings.DEFAULT_LIBRARY)
        private set

    /** The ways this phone can sync (Settings › Sync offers them), the first one first. */
    var syncBackends: List<SyncBackend> by mutableStateOf(emptyList())
        private set

    /** The one it syncs through (null: sync is off). */
    var syncBackend: SyncBackend? by mutableStateOf(null)
        private set

    /** This phone has synced with the others once (everything came in, its own went out). */
    var syncJoined: Boolean by mutableStateOf(false)
        private set

    /** The backend's token can't be read any more (restored to another phone): sign in again. */
    var syncNeedsSignIn: Boolean by mutableStateOf(false)
        private set

    init {
        // Shown once the tasks folder is open (just below); changes wait until then.
        board = TaskBoard()
        readOnly = true
        viewModelScope.launch {
            val local = withContext(Dispatchers.IO) { BoardHub.local(application) }
            val sync = withContext(Dispatchers.IO) { SyncHub.get(application) }
            val loaded = withContext(Dispatchers.IO) { runCatching { local.store?.snapshot() }.getOrNull() }
            opened = local
            phoneSync = sync
            moving = null
            showDeviceBoard(loaded)
            // From now on alarms and notification buttons change the tasks through this screen (the same store).
            BoardHub.live = this@BoardViewModel
            readSyncStatus()
            if (syncOn) SyncHub.startBackground(application)
            reminderSet = false
            refresh()
        }
        refresh()
    }

    /**
     * While the app is on screen (MainActivity runs it while started, and it stops when the app leaves the screen):
     * every second the clocks move and the focus timer moves on, every 30 s the lists follow the clock, and sync runs
     * when its schedule says so (5 s after a change, every minute). With the app closed, WorkManager syncs instead.
     */
    suspend fun whileOnScreen() {
        var seconds = 0
        while (true) {
            delay(1_000)
            tick(lists = ++seconds % 30 == 0)
            if (syncOn && !syncBusy && syncSchedule.due(Instant.now())) syncNow(asked = false)
        }
    }

    /** This phone's own board, as its tasks folder holds it (a board.json from a newer app: changes are off, nothing is overwritten). */
    private fun showDeviceBoard(snapshot: TaskBoard?) {
        val o = opened ?: return
        val s = o.store
        if (s == null || snapshot == null) {
            readOnly = true
            status = o.problem
            board = TaskBoard()
            return
        }
        readOnly = false
        o.problem?.let { status = it }
        s.problems.firstOrNull()?.let { status = "“${it.path}” couldn't be read (${it.message}). It's kept as it is." }
        board = snapshot
    }

    /** Back in the app: the alarms are set again (allowing exact alarms meanwhile makes them ring on time). */
    fun resume() {
        scheduled = null
        reminderSet = false
        refresh()
        reloadLocal()
        syncSchedule.soon()
    }

    override fun onCleared() {
        if (BoardHub.live === this) BoardHub.live = null
        super.onCleared()
    }

    fun refresh(deliver: Boolean = true) {
        now = Instant.now()
        // Reminders due now are told (once): with the app open, closed, or the phone locked. Told and marked as told
        // in the tasks folder, off the screen's thread (the redraw after it doesn't try again).
        if (deliver && !readOnly && !delivering && opened != null && Reminders.due(board, now).isNotEmpty()) {
            delivering = true
            val app = getApplication<Application>()
            save(changed = false, at = now, mutate = { b, at -> ReminderAlarm.deliver(app, b, at) }) { result ->
                delivering = false
                if (result.getOrNull() == true) syncSchedule.changed(Instant.now())
                refresh(deliver = false)
            }
        }
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
        // While the tasks folder opens, the alarm set before stays as it is (the board shown is only a placeholder).
        if ((nextReminder != scheduledReminder || !reminderSet) && opened != null) {
            scheduledReminder = nextReminder
            reminderSet = true
            ReminderAlarm.schedule(getApplication(), nextReminder)
        }
        version++
        showTimers()
        updateWidgets()
    }

    // What the widgets were last told about (the board, its changes and order), and when.
    private var widgetKey = ""
    private var widgetsAt = Instant.EPOCH

    /** The board changed (or a while went by): the home-screen widgets show it again. */
    private fun updateWidgets() {
        val key = "${System.identityHashCode(board)}|${board.activity.size}|${board.activity.lastOrNull()?.at}|${board.nowOrder}|${board.nowArrangedAt}"
        if (key == widgetKey && Duration.between(widgetsAt, now) < Duration.ofMinutes(15)) return
        widgetKey = key
        widgetsAt = now
        Widgets.update(getApplication())
    }

    /** Searches as you type: words in the title or details, is:done, is:archived, group:work, #tag… */
    fun search(text: String) {
        query = text
        refresh()
    }

    /** An alarm, a notification button or a widget changes the board while the app runs: shown at once, then saved. */
    override fun applyOutside(action: (TaskBoard, Instant) -> Unit, done: (Result<Unit>) -> Unit) {
        if (opened == null || readOnly) {
            return done(Result.failure(BoardException(opened?.problem ?: "Your board is from a newer version of the app, so changes are off to protect it.")))
        }
        val at = Instant.now()
        try {
            action(board, at)
        } catch (e: BoardException) {
            refresh()
            return done(Result.failure(e))
        }
        refresh()
        save(at = at, mutate = action) { result ->
            refresh()
            done(result)
        }
    }

    /** How much energy a task takes per hour (1 to 3; null: not said). */
    fun setEnergy(id: UUID, level: Int?) =
        change(if (level == null) "Energy: not said" else "Energy: ${"⚡".repeat(level)} an hour") { b, now -> b.setEnergy(id, level, now = now) }

    /** How much energy there is in a day (every app uses it). */
    fun setDailyEnergy(perDay: Int?) = change("Energy in a day: ${perDay ?: Energy.DEFAULT_PER_DAY}") { b, _ -> b.setEnergyPerDay(perDay) }

    /** Not done after all (from Done today): back in Do now. */
    fun reopen(id: UUID) { change(null) { b, now -> b.reopen(id, now = now) } }

    /** Every second: the clocks move; the focus timer moves on (saved); every 30 s the lists follow the clock too. */
    private fun tick(lists: Boolean) {
        now = Instant.now()
        val events = if (readOnly) emptyList() else board.tickPomodoro(now)
        // A timer left running for half a day was forgotten: it ends (at 12 hours), so the task can be timed again.
        val closed = if (readOnly) 0 else board.closeForgottenTimers(now)
        if (events.isNotEmpty() || closed > 0) {
            // Shown at once; the same is done to the tasks folder (off the screen's thread).
            save(at = now, mutate = { b, at ->
                b.tickPomodoro(at)
                b.closeForgottenTimers(at)
            }) { result ->
                result.onFailure { e -> status = "Couldn't save (${e.message}). It's tried again." }
                refresh()
            }
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

    fun startFocus(id: UUID?): Boolean {
        val on = id ?: dashboard.focus?.item?.id
        return change("Focus started") { b, now -> b.startFocus(on, now = now) }
    }

    fun pauseFocus() = change(null) { b, now -> b.pauseFocus(now) }

    fun resumeFocus() = change(null) { b, now -> b.resumeFocus(now) }

    fun skipFocus() = change(null) { b, now -> b.skipFocus(now) }

    fun resetFocus() = change(null) { b, now -> b.resetFocus(now) }

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
        change("Focus started") { b, now -> b.startNextFocus(fallback, now = now) }
    }

    /** Times the task (any other timer stops), or stops it when it's the one timing. */
    fun toggleTimer(id: UUID) {
        if (board.runningTimer(Instant.now())?.item?.id == id) {
            change("Timer stopped") { b, now -> b.stopTimer(now) }
        } else {
            change("Timing “${titleOf(id).orEmpty()}”") { b, now -> b.startTimer(id, now = now) }
        }
    }

    fun stopTimer() = change("Timer stopped") { b, now -> b.stopTimer(now) }

    fun select(group: UUID?) {
        selectedGroup = group
        refresh()
    }

    fun count(group: UUID?): Int =
        if (group == null) dashboard.groupCounts.values.sumOf { it.now } else dashboard.groupCounts[group]?.now ?: 0

    fun titleOf(id: UUID): String? = board.find(id)?.title

    /** The task whose details are open (shown over Today until closed). */
    var openTaskId: UUID? by mutableStateOf(null)

    /**
     * Another task a link or a notification asks to open while one is open: the open one saves what's typed first (or
     * asks), then switches.
     */
    var requestedTaskId: UUID? by mutableStateOf(null)

    /** A clicked todotracker://task/<id> link: that task opens (anything else is ignored). */
    fun openLink(link: String?) {
        val target = TaskLinks.target(link)
        if (target is TaskLinks.Target.Task) {
            when {
                board.find(target.id) == null -> status = "That task isn't on this phone"
                openTaskId == null || openTaskId == target.id -> openTaskId = target.id
                else -> requestedTaskId = target.id
            }
        }
    }

    fun itemOf(id: UUID): WorkItem? = board.find(id)

    /** When it was done (a repeating task that moved on: when its last time was). */
    fun doneAt(item: WorkItem): Instant? = Agenda.doneAt(board, item)

    /** Quick capture: "Call mum @tomorrow !high due:3d". True when it was added. */
    fun capture(text: String): Boolean {
        val group = selectedGroup
        return change("Added") { b, now ->
            val capture = QuickCaptureParser.parse(text, now, ZoneId.systemDefault())
            val item = b.addTask(NewTask(capture.title, groupId = group, priority = capture.priority, deadline = capture.deadline), now = now)
            capture.nextActionAt?.let { b.scheduleNextAction(item.id, it, notify = true, now = now) }
        }
    }

    /** Done, with a way back (a mis-tap shouldn't lose a task). */
    fun complete(id: UUID) {
        val title = titleOf(id) ?: return
        if (change(null) { b, now -> b.complete(id, now = now) }) {
            undoable = Undoable("Done: $title") { change(null) { b, now -> b.reopen(id, now = now) } }
        }
    }

    /** The quick snooze choices right now (the same in every app). */
    fun snoozeChoices(): List<SnoozeChoice> = Snooze.choices(Instant.now(), ZoneId.systemDefault())

    /** "today 17:00", "Fri 14:30", "Mon 12 Jan, 9:00". */
    fun describe(at: Instant): String = Snooze.describe(at, Instant.now(), ZoneId.systemDefault())

    /** Snoozes until a time: a quick choice or one picked on the calendar (refused when it's already past). */
    fun snoozeUntil(id: UUID, at: Instant) {
        val undo = snoozeUndo(id)
        if (change(null) { b, now ->
                if (!at.isAfter(now)) throw BoardException("That time has passed. Pick a later one.")
                b.scheduleNextAction(id, at, notify = true, now = now)
            }
        ) {
            undoable = Undoable("Snoozed until ${describe(at)}", undo)
        }
    }

    /** Snoozes until another task is done; it comes back with a reminder then. */
    fun waitFor(id: UUID, afterId: UUID) {
        val undo = snoozeUndo(id)
        if (change(null) { b, now -> b.waitFor(id, afterId, now = now) }) undoable = Undoable("Waiting for “${titleOf(afterId)}”", undo)
    }

    /** Puts the task's snooze back as it was before (Undo on "Snoozed until…"): its wait, or back in Do now. */
    private fun snoozeUndo(id: UUID): () -> Unit {
        val item = board.find(id)
        val before = item?.nextActionAt
        val after = item?.afterId
        val reminded = before != null && item.reminders.any { it.kind == dev.todotracker.core.ReminderKind.NEXT_ACTION && it.dueAt == before && it.dismissedAt == null }
        return {
            val waitFor = after?.takeIf { board.canWaitFor(id, it) }
            val until = before?.takeIf { it.isAfter(Instant.now()) }
            change(null) { b, now ->
                when {
                    waitFor != null -> b.waitFor(id, waitFor, now = now)
                    until != null -> b.scheduleNextAction(id, until, notify = reminded, now = now)
                    else -> b.clearNextAction(id, now = now)
                }
            }
        }
    }

    // ---- A task's details ----

    /** Saves what the details changed (a bad value is refused with why, and nothing changes). */
    fun edit(id: UUID, changes: TaskChanges, message: String? = "Saved"): Boolean = change(message) { b, now -> b.update(id, changes, now = now) }

    fun setPriority(id: UUID, priority: Priority) = edit(id, TaskChanges(priority = priority), "Priority: ${priority.wire}")

    fun setDeadline(id: UUID, at: Instant?) = edit(id, if (at == null) TaskChanges(clearDeadline = true) else TaskChanges(deadline = at), if (at == null) "No deadline" else "Due ${describe(at)}")

    /** A repeat rule in words ("every weekday"); empty stops it repeating. */
    fun setRepeat(id: UUID, rule: String) = edit(id, TaskChanges(repeat = rule.trim()), if (rule.isBlank()) "Doesn't repeat any more" else "Repeats ${rule.trim()}")

    fun setSequential(id: UUID, inOrder: Boolean) = edit(id, TaskChanges(sequential = inOrder), if (inOrder) "Steps in order" else "Steps in any order")

    /** Hours between steps (null or 0: none). */
    fun setStepDelay(id: UUID, hours: Double?) =
        edit(id, if (hours == null || hours <= 0) TaskChanges(clearStepDelay = true) else TaskChanges(stepDelay = Duration.ofMinutes(Math.round(hours * 60))), "Saved")

    fun addSubtask(parentId: UUID, title: String) = change("Added") { b, now -> b.addTask(NewTask(title, parentId = parentId), now = now) }

    /** Steps done one after another, [hours] apart (one per line). */
    fun addSteps(parentId: UUID, lines: String, hours: Double?): Boolean {
        val titles = lines.lines().map { it.trim() }.filter { it.isNotEmpty() }
        val delay = hours?.takeIf { it > 0 }?.let { Duration.ofMinutes(Math.round(it * 60)) }
        return change("Added ${titles.size} step${if (titles.size == 1) "" else "s"}") { b, now -> b.addSteps(parentId, titles, delay, now = now) }
    }

    /** A reminder at a time (the task stays where it is). */
    fun addReminder(id: UUID, at: Instant, message: String?) = change("Reminder set for ${describe(at)}") { b, now ->
        if (!at.isAfter(now)) throw BoardException("That time has passed. Pick a later one.")
        b.addReminder(id, at, message, now = now)
    }

    fun archive(id: UUID) {
        val title = titleOf(id) ?: return
        if (change(null) { b, now -> b.archive(id, now = now) }) {
            undoable = Undoable("Archived: $title") { change(null) { b, now -> b.unarchive(id, now = now) } }
        }
    }

    fun unarchive(id: UUID) = change("Back in Done") { b, now -> b.unarchive(id, now = now) }

    /** Deletes the task and its subtasks for good (asked first, on screen). */
    fun delete(id: UUID) {
        val title = titleOf(id) ?: return
        if (openTaskId == id) openTaskId = board.find(id)?.parent?.id
        change("Deleted “$title”") { b, now -> b.delete(id, now = now) }
    }

    fun moveToGroup(id: UUID, groupId: UUID) =
        change("Moved to ${board.groups.firstOrNull { it.id == groupId }?.name}") { b, now -> b.moveToGroup(id, groupId, now = now) }

    fun groupOf(id: UUID): UUID? = board.find(id)?.groupId

    fun groupName(id: UUID): String? = board.groups.firstOrNull { it.id == id }?.name

    // ---- Groups ----

    fun updateGroup(id: UUID, name: String, color: String?) = change("Saved") { b, now -> b.updateGroup(id, name, color, now = now) }

    /** Deletes a group; its tasks move to [moveTo] (none are lost). */
    fun deleteGroup(id: UUID, moveTo: UUID): Boolean {
        val name = groupName(id)
        return change("Deleted “$name”; its tasks are in ${groupName(moveTo)}") { b, now -> b.deleteGroup(id, moveTo, now = now) }
    }

    // ---- Settings ----

    fun chooseTheme(value: String) {
        theme = if (value == "light" || value == "dark") value else "system"
        prefs.edit().putString("theme", theme).apply()
    }

    fun setFocusLengths(focus: Int, shortBreak: Int, longBreak: Int) = change("Saved") { b, _ -> b.setFocusLengths(focus, shortBreak, longBreak) }

    /** Tasks it can wait for: the open ones shown (to do now, then waiting), not itself, its subtasks or parents, or what waits for it. */
    fun waitCandidates(id: UUID): List<Pair<UUID, String>> =
        (dashboard.now + dashboard.waiting).map { it.item }.distinctBy { it.id }.filter { board.canWaitFor(id, it.id) }.map { it.id to it.title }

    fun bringBack(id: UUID) = change(null) { b, now -> b.clearNextAction(id, now = now) }

    fun dismissReminder(id: UUID, reminderId: UUID) = change(null) { b, now -> b.dismissReminder(id, reminderId, now = now) }

    /** Do something else first: the task moves one down Do now (the next one becomes the focus). */
    fun skip(id: UUID) {
        val ids = dashboard.now.map { it.item.id }.toMutableList()
        val i = ids.indexOf(id)
        if (i < 0 || i + 1 >= ids.size) return
        java.util.Collections.swap(ids, i, i + 1)
        val order = ids.toList()
        change(null) { b, now -> b.arrangeNow(order, now = now) }
    }

    fun addNote(id: UUID, text: String) = change("Note saved") { b, now -> b.addNote(id, text, now = now) }

    /** A new group, shown once it's saved. */
    fun addGroup(name: String) = change(null, saved = { id: UUID -> selectedGroup = id }) { b, now -> b.addGroup(name, now = now).id }

    /**
     * Applies a change: it's checked and shown at once on the board on screen, then made again on the tasks folder (the
     * one store) off the screen's thread, where only the files it touched are written (all or nothing; a file changed
     * underneath is read again and the change made again). False (with the reason shown) when the board refused it.
     * [action] gets the board to change and the time; what it returns on the saved board goes to [saved].
     */
    private fun <T> change(message: String?, saved: (T) -> Unit = {}, action: (TaskBoard, Instant) -> T): Boolean {
        if (opened == null) {
            status = "Just a moment: your tasks are still opening."
            return false
        }
        if (readOnly) {
            status = opened?.problem ?: "Your board is from a newer version of the app, so changes are off to protect it."
            return false
        }
        val at = Instant.now()
        try {
            action(board, at)
        } catch (e: BoardException) {
            status = e.message
            refresh()
            return false
        }
        status = message
        refresh()
        save(at = at, mutate = action) { result ->
            result.onSuccess(saved)
            result.onFailure { e ->
                undoable = null
                status = if (e is BoardException) e.message else "Couldn't save your change (${e.message}). Nothing was changed; try again."
            }
            refresh()
        }
        return true
    }

    /**
     * Makes [mutate] on the tasks folder at [at], off the screen's thread (BoardHub.disk: one at a time and in order,
     * with the alarms' and widgets' changes, never waiting on the screen), then shows what's saved and tells [after]
     * (on the main thread). [changed]: sync goes out a few seconds later.
     */
    private fun <T> save(changed: Boolean = true, at: Instant, mutate: (TaskBoard, Instant) -> T, after: (Result<T>) -> Unit) {
        val s = store ?: return after(Result.failure(BoardException("Your board is from a newer version of the app, so changes are off to protect it.")))
        writing++
        BoardHub.disk.launch {
            val result = runCatching { s.update { mutate(it, at) } }
            val saved = runCatching { s.snapshot() }.getOrNull()
            withContext(Dispatchers.Main) {
                writing--
                if (saved != null && writing == 0) board = saved
                if (changed && result.isSuccess) syncSchedule.changed(Instant.now())
                after(result)
            }
        }
    }

    /** Sync (or another part of the app) changed the tasks folder: show it as it is now (read off the screen's thread). */
    override fun reloadLocal() {
        val s = store ?: return
        BoardHub.disk.launch {
            val saved = runCatching { s.snapshot() }.getOrNull()
            withContext(Dispatchers.Main) {
                if (saved != null && writing == 0) board = saved
                reminderSet = false
                refresh()
            }
        }
    }

    // ---- Sync with your other devices (Settings) ----

    val canSync: Boolean get() = phoneSync != null

    private fun readSyncStatus() {
        val p = phoneSync ?: return
        syncOn = p.on
        syncBackends = p.backends.all
        syncBackend = p.backend
        syncLibrary = p.settings.library
        syncJoined = p.settings.joined
        syncNotice = p.settings.notice
        syncNeedsSignIn = p.needsSignIn
        syncStatus = p.status()
    }

    /** Syncs now (off the screen's thread); what came in shows at once, reminders included. */
    fun syncNow(asked: Boolean = true) {
        val p = phoneSync ?: return
        if (!p.on || syncBusy) return
        syncBusy = true
        syncSchedule.started(Instant.now())
        if (asked) syncMessage = null
        SyncHub.run({ p.sync() }) { result -> synced(result, asked) }
    }

    private fun synced(result: Result<PhoneSyncOutcome>, asked: Boolean) {
        syncBusy = false
        result.onSuccess { if (it.changed) reloadLocal() }
        result.onFailure { e ->
            // A sync that failed part way may have brought some changes in already: show the tasks as they are.
            reloadLocal()
            if (asked) syncMessage = e.message ?: "Sync didn't work this time."
        }
        readSyncStatus()
    }

    /**
     * Turns on sync through the backend [place] names (a gist; in a debug build, a folder on the phone), signed in with
     * [token] when it asks for one; the first sync joins the library: everything from the other devices comes in
     * first, then this phone's tasks go out.
     */
    fun turnOnSync(place: String, token: String?, library: String) {
        val p = phoneSync ?: return
        val signIn = p.backends[place]?.token
        if (signIn != null && token.isNullOrBlank() && p.settings.token == null) {
            syncMessage = signIn.missing
            return
        }
        syncMessage = null
        SyncHub.run({ p.turnOn(place, token?.takeIf { it.isNotBlank() }, library) }) { result ->
            result.onFailure { e -> syncMessage = e.message }
            result.onSuccess {
                SyncHub.startBackground(getApplication())
                readSyncStatus()
                syncNow()
            }
        }
    }

    fun changeSyncLibrary(library: String) {
        val p = phoneSync ?: return
        SyncHub.run({ p.changeLibrary(library) }) {
            readSyncStatus()
            syncNow()
        }
    }

    /** Stops syncing; the tasks stay on this phone. */
    fun turnOffSync() {
        val p = phoneSync ?: return
        SyncHub.stopBackground(getApplication())
        SyncHub.run({ p.turnOff() }) {
            readSyncStatus()
            syncMessage = null
        }
    }

    fun forgetDevice(device: String) {
        val p = phoneSync ?: return
        SyncHub.run({ p.forget(device) }) { result ->
            result.onFailure { e -> syncMessage = "Couldn't forget it yet: ${e.message}" }
            readSyncStatus()
        }
    }

    fun dismissConflict(key: String) {
        val p = phoneSync ?: return
        SyncHub.run({ p.dismiss(key) }) { readSyncStatus() }
    }
}
