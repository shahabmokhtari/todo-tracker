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
import dev.todotracker.core.Dashboard
import dev.todotracker.core.Energy
import dev.todotracker.core.NewTask
import dev.todotracker.core.BreakPrompt
import dev.todotracker.core.Breaks
import dev.todotracker.core.PairingLink
import dev.todotracker.core.PhaseEnd
import dev.todotracker.core.PhoneApi
import dev.todotracker.core.PhoneClient
import dev.todotracker.core.PhoneException
import dev.todotracker.core.PhoneLink
import dev.todotracker.core.RemoteAction
import dev.todotracker.core.RemoteState
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
import dev.todotracker.core.sync.PhoneMode
import dev.todotracker.core.sync.PhoneSync
import dev.todotracker.core.sync.PhoneSyncOutcome
import dev.todotracker.core.sync.SyncSchedule
import dev.todotracker.core.sync.SyncSettings
import dev.todotracker.core.sync.SyncStatus
import dev.todotracker.core.vault.VaultStore
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
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

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
 * The board on this phone: its tasks folder (the one store, [BoardHub.store]), and every change saved there right
 * away; or, connected, a computer's. The lists follow the clock (waiting tasks come back). Syncing on its own, the
 * phone syncs on launch and resume, 5 s after a change and every minute while open (off the screen's thread).
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

    // ---- Connected to a computer (Settings › Connect to a computer) ----

    private val phone = PhoneConnection(application)
    private var pairing: PhoneConnection.Pairing? = phone.load()
    private var client: PhoneClient? = pairing?.let { phone.client(it) }
    @OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
    private val network = Dispatchers.IO.limitedParallelism(1)
    // The computer's board as last read (shown again when a change doesn't go through), its tag, and when.
    private var remoteText: String? = null
    private var remoteEtag: String? = null
    private var remoteReadAt: Instant? = null
    private var sending = 0

    /** How it stands with the computer (null: this phone's own board is shown). */
    var remote: RemoteState? by mutableStateOf(null)
        private set

    /** The connected computer's name. */
    var computer: String? by mutableStateOf(pairing?.computer)
        private set

    /** The calm line over the tasks when the computer can't be reached (null: nothing to say). */
    var remoteBanner: String? by mutableStateOf(null)
        private set

    /** The computer that disconnected this phone (said with Reconnect, until dismissed). */
    var revokedFrom: String? by mutableStateOf(if (pairing == null) phone.revokedFrom else null)
        private set

    /** A pairing link waiting for "Connect" (and why the last try didn't work). */
    var pairingLink: PairingLink? by mutableStateOf(null)
        private set
    var pairingProblem: String? by mutableStateOf(null)
        private set
    var pairingBusy: Boolean by mutableStateOf(false)
        private set

    /** The name the computer lists this phone by (prefilled with the phone's own; kept through Disconnect). */
    var phoneName: String by mutableStateOf(PhoneConnection.phoneName(application))
        private set

    fun changePhoneName(text: String) {
        phoneName = text.take(PhoneApi.MAX_NAME)
        PhoneConnection.rememberPhoneName(getApplication(), phoneName)
    }

    /** The tasks are a computer's: what the computer doesn't let phones do (the focus timer's lengths) is hidden. */
    val isRemote: Boolean get() = pairing != null

    // ---- Sync with your other devices: its state (the actions are at the end) ----

    private var phoneSync: PhoneSync? = null
    private val syncSchedule = SyncSchedule()

    /** Only on this phone, syncing on its own, or a computer's tasks: one at a time. */
    val mode: PhoneMode get() = when {
        // [computer] is screen state (the screen follows it); it's set exactly while connected.
        computer != null -> PhoneMode.CONNECTED
        syncOn -> PhoneMode.SYNCING
        else -> PhoneMode.ON_THIS_PHONE
    }

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
    var syncPlace: String? by mutableStateOf(null)
        private set

    /** This phone has synced with the others once (everything came in, its own went out). */
    var syncJoined: Boolean by mutableStateOf(false)
        private set

    /** The token can't be read any more (restored to another phone): sign in again. */
    var syncNeedsToken: Boolean by mutableStateOf(false)
        private set

    init {
        if (pairing != null) {
            // Connected to a computer: its tasks (the last read until it answers); this phone's own board isn't read.
            board = remoteFromCache()
            // With the app closed, a check now and then (reminders added on the computer meanwhile still ring).
            RemoteCheck.start(application)
            BoardHub.live = this
        } else {
            // Shown once the tasks folder is open (just below); changes wait until then.
            board = TaskBoard()
            readOnly = true
        }
        viewModelScope.launch {
            val local = withContext(Dispatchers.IO) { BoardHub.local(application) }
            val sync = withContext(Dispatchers.IO) { SyncHub.get(application) }
            opened = local
            phoneSync = sync
            moving = null
            // From now on alarms and notification buttons change the tasks through this screen (the same store).
            if (!isRemote) board = deviceBoard(register = true)
            readSyncStatus()
            if (syncOn) SyncHub.startBackground(application)
            reminderSet = false
            refresh()
        }
        refresh()
        viewModelScope.launch {
            var seconds = 0
            while (true) {
                delay(1_000)
                val lists = ++seconds % 30 == 0
                tick(lists)
                // Connected: the computer's board is read again every 30 s (just "unchanged" when it is).
                if (lists) poll()
                // Syncing on its own: when the schedule says so (5 s after a change, every minute).
                if (syncOn && !isRemote && !syncBusy && syncSchedule.due(Instant.now())) syncNow(asked = false)
            }
        }
        poll()
    }

    /** This phone's own board, as its tasks folder holds it (a board.json from a newer app: changes are off, nothing is overwritten). */
    private fun deviceBoard(register: Boolean = false): TaskBoard {
        val o = opened ?: run {
            // Still opening: shown when it's open.
            readOnly = true
            return TaskBoard()
        }
        val s = o.store
        val loaded = if (s == null) {
            readOnly = true
            status = o.problem
            TaskBoard()
        } else {
            readOnly = false
            o.problem?.let { status = it }
            s.problems.firstOrNull()?.let { status = "“${it.path}” couldn't be read (${it.message}). It's kept as it is." }
            s.snapshot()
        }
        if (register) BoardHub.live = this
        return loaded
    }

    /** The computer's board as last read (until it answers). */
    private fun remoteFromCache(): TaskBoard {
        readOnly = false
        remote = RemoteState.Connecting
        val cached = phone.cached()
        remoteText = cached?.text
        remoteEtag = cached?.etag
        remoteReadAt = cached?.at
        return remoteBoard()
    }

    private fun remoteBoard(): TaskBoard = remoteText?.let { runCatching { BoardCodec.decode(it) }.getOrNull() } ?: TaskBoard()

    /** Back in the app: the alarms are set again (allowing exact alarms meanwhile makes them ring on time). */
    fun resume() {
        scheduled = null
        reminderSet = false
        if (!isRemote && store != null) board = store!!.snapshot()
        refresh()
        poll()
        syncSchedule.soon()
    }

    /** Reads the computer's board again if it changed (nothing to do while changes are on their way). */
    fun poll() {
        val c = client ?: return
        if (sending > 0) return
        val etag = remoteEtag
        viewModelScope.launch {
            val result = withContext(network) {
                runCatching {
                    val export = c.export(etag)
                    phone.answered(c)
                    val now = Instant.now()
                    if (export == null) phone.stillCurrent(now) else phone.cache(export.text, export.etag, now)
                    export
                }
            }
            if (client !== c || sending > 0) return@launch
            result.fold(
                onSuccess = { export ->
                    if (export != null) {
                        val read = runCatching { BoardCodec.decode(export.text) }
                        read.onSuccess {
                            board = it
                            remoteText = export.text
                            remoteEtag = export.etag
                        }
                        read.onFailure { e -> status = "Couldn't read the tasks from ${computer}: ${e.message}" }
                    }
                    // Back in touch: what was said about not reaching it is old news.
                    if (remote is RemoteState.Unreachable) status = null
                    remoteReadAt = Instant.now()
                    remote = RemoteState.read(remoteReadAt!!)
                },
                onFailure = { e -> failed(e) },
            )
            refresh()
        }
    }

    /** A read or a change didn't reach the computer (or it said this phone isn't connected any more). */
    private fun failed(e: Throwable) {
        val error = e as? PhoneException ?: PhoneException.Unreachable(e.message ?: "no answer")
        val next = RemoteState.failed(remote ?: RemoteState.Connecting, error, remoteReadAt)
        if (next is RemoteState.Revoked) revoked() else remote = next
    }

    /** The computer disconnected this phone: its own board comes back (as it was), with a way to reconnect. */
    private fun revoked() {
        val name = computer
        phone.forget(revoked = true)
        leaveRemote()
        revokedFrom = name
    }

    private fun leaveRemote() {
        RemoteCheck.stop(getApplication())
        pairing = null
        client = null
        remote = null
        remoteText = null
        remoteEtag = null
        remoteReadAt = null
        computer = null
        undoable = null
        openTaskId = null
        selectedGroup = null
        board = deviceBoard()
        reminderSet = false
        scheduled = null
        refresh()
    }

    /** Stops using the computer's tasks: this phone's own board comes back (the computer keeps its tasks). */
    fun disconnect() {
        val name = computer ?: return
        phone.forget(revoked = false)
        leaveRemote()
        status = "Back to the tasks on this phone. To remove this phone on $name too, open Settings › Phones there."
    }

    fun dismissRevoked() {
        revokedFrom = null
        phone.revokedFrom = null
    }

    /** A pairing link (opened, scanned or pasted): asks first, then connects. */
    fun offerPairing(text: String?) {
        val link = PhoneLink.parse(text)
        if (link == null) {
            status = "That isn't a whole pairing link. On your computer, open Settings › Phones › Connect a phone, then scan the new code."
            return
        }
        pairingProblem = null
        pairingLink = link
    }

    fun cancelPairing() {
        if (pairingBusy) return
        pairingLink = null
        pairingProblem = null
    }

    /** Trades the link's code for this phone's key, then shows the computer's tasks. */
    fun confirmPairing() {
        val link = pairingLink ?: return
        if (pairingBusy) return
        pairingBusy = true
        pairingProblem = null
        val name = PhoneApi.phoneName(phoneName, PhoneConnection.deviceName(getApplication()))
        viewModelScope.launch {
            val result = withContext(network) { runCatching { PhoneConnection.claim(link, name) } }
            pairingBusy = false
            result.fold(
                onSuccess = { claimed ->
                    phone.save(link, claimed, Instant.now())
                    val paired = phone.load()
                    if (paired == null) {
                        pairingProblem = "Connected, but this phone couldn't keep its key safely. Try again."
                        return@fold
                    }
                    pairingLink = null
                    // The name the computer has for it (what was typed, tidied, or the phone's own).
                    changePhoneName(name)
                    pairing = paired
                    client = phone.client(paired)
                    computer = paired.computer
                    revokedFrom = null
                    undoable = null
                    openTaskId = null
                    selectedGroup = null
                    board = remoteFromCache()
                    RemoteCheck.start(getApplication())
                    reminderSet = false
                    scheduled = null
                    val wasSyncing = syncOn
                    if (wasSyncing) turnOffSync()
                    status = "Connected to ${paired.computer}. Your tasks on this phone stay here for when you disconnect." +
                        if (wasSyncing) " Sync on this phone is off now (the computer syncs them)." else ""
                    refresh()
                    poll()
                },
                onFailure = { e ->
                    pairingProblem = when (e) {
                        is PhoneException.Unreachable -> "Couldn't reach ${link.computer}. Is this phone on the same Wi-Fi (or on Tailscale), and is Todo Tracker running there?"
                        else -> e.message ?: "That didn't work. Try again."
                    }
                },
            )
        }
    }

    /** The computer's board changed (a notification button), or a reminder is due: read it again. */
    override fun remoteChanged() {
        refresh()
        poll()
    }

    override fun onCleared() {
        if (BoardHub.live === this) BoardHub.live = null
        super.onCleared()
    }

    fun refresh() {
        now = Instant.now()
        // Reminders due now are told (once): with the app open, closed, or the phone locked.
        // Connected, the computer's reminders are told here too (once each), and nothing is written back.
        val remoteNext = if (isRemote) ReminderAlarm.deliverRemote(getApplication(), phone, board, now) else null
        if (!isRemote && !readOnly && Reminders.due(board, now).isNotEmpty()) {
            runCatching { if (local(changed = false) { ReminderAlarm.deliver(getApplication(), it, now) }) syncSchedule.changed(now) }
        }
        remoteBanner = remote?.let { RemoteState.banner(it, computer ?: "your computer", now, ZoneId.systemDefault()) }
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
        val nextReminder = if (isRemote) remoteNext else if (readOnly) null else Reminders.nextAt(board, now)
        // While the tasks folder opens, the alarm set before stays as it is (the board shown is only a placeholder).
        if ((nextReminder != scheduledReminder || !reminderSet) && (isRemote || opened != null)) {
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
        // Connected, the phone's own board isn't changed (the computer's changes go through the REST API).
        if (isRemote) return Result.failure(BoardException("Open the app to change tasks on ${computer}."))
        return try {
            local { action(it, Instant.now()) }
            Result.success(Unit)
        } catch (e: BoardException) {
            Result.failure(e)
        } catch (e: IOException) {
            Result.failure(e)
        } finally {
            refresh()
        }
    }

    /** How much energy a task takes per hour (1 to 3; null: not said). */
    fun setEnergy(id: UUID, level: Int?) = change(
        if (level == null) "Energy: not said" else "Energy: ${"⚡".repeat(level)} an hour",
        RemoteAction.Update(id, TaskChanges(energy = level, clearEnergy = level == null)),
    ) { board.setEnergy(id, level, now = it) }

    /** How much energy there is in a day (every app uses it). */
    fun setDailyEnergy(perDay: Int?) = change("Energy in a day: ${perDay ?: Energy.DEFAULT_PER_DAY}", RemoteAction.SetDailyEnergy(perDay)) { board.setEnergyPerDay(perDay) }

    /** Not done after all (from Done today): back in Do now. */
    fun reopen(id: UUID) { change(null, RemoteAction.Reopen(id)) { board.reopen(id, now = it) } }

    /** Every second: the clocks move; the focus timer moves on (saved); every 30 s the lists follow the clock too. */
    private fun tick(lists: Boolean) {
        now = Instant.now()
        val events = if (readOnly) emptyList() else board.tickPomodoro(now)
        // A timer left running for half a day was forgotten: it ends (at 12 hours), so the task can be timed again.
        val closed = if (readOnly) 0 else board.closeForgottenTimers(now)
        if (events.isNotEmpty() || closed > 0) {
            // Connected, the computer moves its own timer on: this only shows it. Here, the same is done to the tasks folder.
            if (!isRemote) {
                try {
                    local { b ->
                        b.tickPomodoro(now)
                        b.closeForgottenTimers(now)
                    }
                } catch (e: IOException) {
                    status = "Couldn't save (${e.message}). It's tried again."
                }
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
        return change("Focus started", RemoteAction.Pomodoro("start", on)) { board.startFocus(on, now = it) }
    }

    fun pauseFocus() = change(null, RemoteAction.Pomodoro("pause")) { board.pauseFocus(it) }

    fun resumeFocus() = change(null, RemoteAction.Pomodoro("resume")) { board.resumeFocus(it) }

    fun skipFocus() = change(null, RemoteAction.Pomodoro("skip")) { board.skipFocus(it) }

    fun resetFocus() = change(null, RemoteAction.Pomodoro("reset")) { board.resetFocus(it) }

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
        change("Focus started", RemoteAction.Pomodoro("next", fallback)) { board.startNextFocus(fallback, now = it) }
    }

    /** Times the task (any other timer stops), or stops it when it's the one timing. */
    fun toggleTimer(id: UUID) {
        if (board.runningTimer(Instant.now())?.item?.id == id) {
            change("Timer stopped", RemoteAction.StopTimer) { board.stopTimer(it) }
        } else {
            change("Timing “${titleOf(id).orEmpty()}”", RemoteAction.StartTimer(id)) { board.startTimer(id, now = it) }
        }
    }

    fun stopTimer() = change("Timer stopped", RemoteAction.StopTimer) { board.stopTimer(it) }

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
        // todotracker://pair?… from a computer's Settings › Phones: connect to it (asked first).
        if (PhoneLink.isPairing(link)) return offerPairing(link)
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
    fun capture(text: String): Boolean = change("Added", RemoteAction.Capture(text, selectedGroup)) { now ->
        val capture = QuickCaptureParser.parse(text, now, ZoneId.systemDefault())
        val item = board.addTask(NewTask(capture.title, groupId = selectedGroup, priority = capture.priority, deadline = capture.deadline), now = now)
        capture.nextActionAt?.let { board.scheduleNextAction(item.id, it, notify = true, now = now) }
    }

    /** Done, with a way back (a mis-tap shouldn't lose a task). */
    fun complete(id: UUID) {
        val title = titleOf(id) ?: return
        if (change(null, RemoteAction.Complete(id)) { board.complete(id, now = it) }) {
            undoable = Undoable("Done: $title") { change(null, RemoteAction.Reopen(id)) { board.reopen(id, now = it) } }
        }
    }

    /** The quick snooze choices right now (the same in every app). */
    fun snoozeChoices(): List<SnoozeChoice> = Snooze.choices(Instant.now(), ZoneId.systemDefault())

    /** "today 17:00", "Fri 14:30", "Mon 12 Jan, 9:00". */
    fun describe(at: Instant): String = Snooze.describe(at, Instant.now(), ZoneId.systemDefault())

    /** Snoozes until a time: a quick choice or one picked on the calendar (refused when it's already past). */
    fun snoozeUntil(id: UUID, at: Instant) {
        val undo = snoozeUndo(id)
        if (change(null, RemoteAction.Schedule(id, at)) {
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
        if (change(null, RemoteAction.WaitFor(id, afterId)) { board.waitFor(id, afterId, now = it) }) undoable = Undoable("Waiting for “${titleOf(afterId)}”", undo)
    }

    /** Puts the task's snooze back as it was before (Undo on "Snoozed until…"): its wait, or back in Do now. */
    private fun snoozeUndo(id: UUID): () -> Unit {
        val item = board.find(id)
        val before = item?.nextActionAt
        val after = item?.afterId
        val reminded = before != null && item.reminders.any { it.kind == dev.todotracker.core.ReminderKind.NEXT_ACTION && it.dueAt == before && it.dismissedAt == null }
        return {
            val now0 = Instant.now()
            val request = when {
                after != null && board.canWaitFor(id, after) -> RemoteAction.WaitFor(id, after)
                before != null && before.isAfter(now0) -> RemoteAction.Schedule(id, before, notify = reminded)
                else -> RemoteAction.BringBack(id)
            }
            change(null, request) { now ->
                when (request) {
                    is RemoteAction.WaitFor -> board.waitFor(id, request.afterId, now = now)
                    is RemoteAction.Schedule -> board.scheduleNextAction(id, request.at, notify = request.notify, now = now)
                    else -> board.clearNextAction(id, now = now)
                }
            }
        }
    }

    // ---- A task's details ----

    /** Saves what the details changed (a bad value is refused with why, and nothing changes). */
    fun edit(id: UUID, changes: TaskChanges, message: String? = "Saved"): Boolean = change(message, RemoteAction.Update(id, changes)) { board.update(id, changes, now = it) }

    fun setPriority(id: UUID, priority: Priority) = edit(id, TaskChanges(priority = priority), "Priority: ${priority.wire}")

    fun setDeadline(id: UUID, at: Instant?) = edit(id, if (at == null) TaskChanges(clearDeadline = true) else TaskChanges(deadline = at), if (at == null) "No deadline" else "Due ${describe(at)}")

    /** A repeat rule in words ("every weekday"); empty stops it repeating. */
    fun setRepeat(id: UUID, rule: String) = edit(id, TaskChanges(repeat = rule.trim()), if (rule.isBlank()) "Doesn't repeat any more" else "Repeats ${rule.trim()}")

    fun setSequential(id: UUID, inOrder: Boolean) = edit(id, TaskChanges(sequential = inOrder), if (inOrder) "Steps in order" else "Steps in any order")

    /** Hours between steps (null or 0: none). */
    fun setStepDelay(id: UUID, hours: Double?) =
        edit(id, if (hours == null || hours <= 0) TaskChanges(clearStepDelay = true) else TaskChanges(stepDelay = Duration.ofMinutes(Math.round(hours * 60))), "Saved")

    fun addSubtask(parentId: UUID, title: String) = change("Added", RemoteAction.AddSubtask(parentId, title)) { board.addTask(NewTask(title, parentId = parentId), now = it) }

    /** Steps done one after another, [hours] apart (one per line). */
    fun addSteps(parentId: UUID, lines: String, hours: Double?): Boolean {
        val titles = lines.lines().map { it.trim() }.filter { it.isNotEmpty() }
        val delay = hours?.takeIf { it > 0 }?.let { Duration.ofMinutes(Math.round(it * 60)) }
        return change("Added ${titles.size} step${if (titles.size == 1) "" else "s"}", RemoteAction.AddSteps(parentId, titles, delay?.toMinutes())) {
            board.addSteps(parentId, titles, delay, now = it)
        }
    }

    /** A reminder at a time (the task stays where it is). */
    fun addReminder(id: UUID, at: Instant, message: String?) = change("Reminder set for ${describe(at)}", RemoteAction.AddReminder(id, at, message)) {
        if (!at.isAfter(it)) throw BoardException("That time has passed. Pick a later one.")
        board.addReminder(id, at, message, now = it)
    }

    fun archive(id: UUID) {
        val title = titleOf(id) ?: return
        if (change(null, RemoteAction.Archive(id)) { board.archive(id, now = it) }) {
            undoable = Undoable("Archived: $title") { change(null, RemoteAction.Unarchive(id)) { board.unarchive(id, now = it) } }
        }
    }

    fun unarchive(id: UUID) = change("Back in Done", RemoteAction.Unarchive(id)) { board.unarchive(id, now = it) }

    /** Deletes the task and its subtasks for good (asked first, on screen). */
    fun delete(id: UUID) {
        val title = titleOf(id) ?: return
        if (openTaskId == id) openTaskId = board.find(id)?.parent?.id
        change("Deleted “$title”", RemoteAction.Delete(id)) { board.delete(id, now = it) }
    }

    fun moveToGroup(id: UUID, groupId: UUID) =
        change("Moved to ${board.groups.firstOrNull { it.id == groupId }?.name}", RemoteAction.MoveToGroup(id, groupId)) { board.moveToGroup(id, groupId, now = it) }

    fun groupOf(id: UUID): UUID? = board.find(id)?.groupId

    fun groupName(id: UUID): String? = board.groups.firstOrNull { it.id == id }?.name

    // ---- Groups ----

    fun updateGroup(id: UUID, name: String, color: String?) = change("Saved", RemoteAction.UpdateGroup(id, name, color)) { board.updateGroup(id, name, color, now = it) }

    /** Deletes a group; its tasks move to [moveTo] (none are lost). */
    fun deleteGroup(id: UUID, moveTo: UUID): Boolean {
        val name = groupName(id)
        return change("Deleted “$name”; its tasks are in ${groupName(moveTo)}", RemoteAction.DeleteGroup(id, moveTo)) { board.deleteGroup(id, moveTo, now = it) }
    }

    // ---- Settings ----

    fun chooseTheme(value: String) {
        theme = if (value == "light" || value == "dark") value else "system"
        prefs.edit().putString("theme", theme).apply()
    }

    /** The focus timer's lengths are set on the computer when connected (phones can't): hidden then. */
    val canSetFocusLengths: Boolean get() = !isRemote

    fun setFocusLengths(focus: Int, shortBreak: Int, longBreak: Int) = change("Saved", null) { board.setFocusLengths(focus, shortBreak, longBreak) }

    /** Tasks it can wait for: the open ones shown (to do now, then waiting), not itself, its subtasks or parents, or what waits for it. */
    fun waitCandidates(id: UUID): List<Pair<UUID, String>> =
        (dashboard.now + dashboard.waiting).map { it.item }.distinctBy { it.id }.filter { board.canWaitFor(id, it.id) }.map { it.id to it.title }

    fun bringBack(id: UUID) = change(null, RemoteAction.BringBack(id)) { board.clearNextAction(id, now = it) }

    fun dismissReminder(id: UUID, reminderId: UUID) = change(null, RemoteAction.DismissReminder(id, reminderId)) { board.dismissReminder(id, reminderId, now = it) }

    /** Do something else first: the task moves one down Do now (the next one becomes the focus). */
    fun skip(id: UUID) {
        val ids = dashboard.now.map { it.item.id }.toMutableList()
        val i = ids.indexOf(id)
        if (i < 0 || i + 1 >= ids.size) return
        java.util.Collections.swap(ids, i, i + 1)
        change(null, RemoteAction.ArrangeNow(ids.toList())) { board.arrangeNow(ids, now = it) }
    }

    fun addNote(id: UUID, text: String) = change("Note saved", RemoteAction.AddNote(id, text)) { board.addNote(id, text, now = it) }

    fun addGroup(name: String) = change(null, RemoteAction.AddGroup(name), after = { body ->
        // The computer made the group (with its own id): that's the one to show.
        runCatching { Json.parseToJsonElement(body).jsonObject["id"]?.jsonPrimitive?.content?.let(UUID::fromString) }.getOrNull()?.let { selectedGroup = it }
    }) { selectedGroup = board.addGroup(name, now = it).id }

    /**
     * Applies a change and saves it; false (with the reason shown) when the board refused it. Connected to a computer,
     * it's [request] that changes the tasks there (the change shows here at once, then the computer's board is read
     * back); [after] gets the computer's answer. A change the computer doesn't let phones make (null) isn't made.
     */
    private fun change(message: String?, request: RemoteAction?, after: ((String) -> Unit)? = null, action: (Instant) -> Unit): Boolean {
        if (isRemote) {
            val name = computer ?: "your computer"
            when {
                request == null -> status = "Do that in Todo Tracker on $name."
                remote?.canChange != true -> status = "Can't change tasks right now: $name isn't answering. Try again when you're back on its Wi-Fi or Tailscale."
                else -> return try {
                    action(Instant.now())
                    send(request, after)
                    status = message
                    true
                } catch (e: BoardException) {
                    status = e.message
                    false
                } finally {
                    refresh()
                }
            }
            return false
        }
        if (opened == null) {
            status = "Just a moment: your tasks are still opening."
            return false
        }
        if (readOnly) {
            status = opened?.problem ?: "Your board is from a newer version of the app, so changes are off to protect it."
            return false
        }
        return try {
            local { action(Instant.now()) }
            status = message
            true
        } catch (e: BoardException) {
            status = e.message
            false
        } catch (e: IOException) {
            status = "Couldn't save your change (${e.message}). Nothing was changed; try again."
            false
        } finally {
            refresh()
        }
    }

    /**
     * Sends a change to the computer (one at a time, in order); once none are left on their way, its board is read back.
     * One that doesn't go through is taken back on screen (the computer's board as last read), with why.
     */
    private fun send(request: RemoteAction, after: ((String) -> Unit)?) {
        val c = client ?: return
        sending++
        viewModelScope.launch {
            val result = withContext(network) { runCatching { c.perform(request).also { phone.answered(c) } } }
            sending--
            if (client !== c) return@launch
            result.fold(
                onSuccess = { body -> after?.invoke(body) },
                onFailure = { e ->
                    undoable = null
                    board = remoteBoard()
                    val name = computer ?: "your computer"
                    status = when (e) {
                        is PhoneException.Refused -> "Not changed: ${e.message}"
                        is PhoneException.Revoked -> null
                        else -> "Not changed: couldn't reach $name."
                    }
                    // A change the computer refused (that route, that task) says nothing about the connection.
                    if (e !is PhoneException.Refused) failed(e)
                    refresh()
                },
            )
            if (sending == 0) poll()
        }
    }

    /**
     * Applies a change to the tasks folder (the one store): [mutate] runs on the store's board ([board] points at it
     * meanwhile, so the actions that use it change the right one), only the files it touched are written (all or
     * nothing; a file changed underneath is read again and the change made again), then the screen shows a copy of
     * what's saved. [changed]: sync goes out a few seconds later.
     */
    private fun <T> local(changed: Boolean = true, mutate: (TaskBoard) -> T): T {
        val s = store ?: throw BoardException("Your board is from a newer version of the app, so changes are off to protect it.")
        try {
            return s.update { b ->
                board = b
                mutate(b)
            }.also { if (changed) syncSchedule.changed(Instant.now()) }
        } finally {
            board = s.snapshot()
        }
    }

    /** Sync (or another part of the app) changed the tasks folder: show it as it is now. */
    override fun reloadLocal() {
        if (isRemote) return
        store?.let { board = it.snapshot() }
        reminderSet = false
        refresh()
    }

    // ---- Sync with your other devices (Settings) ----

    val canSync: Boolean get() = phoneSync != null

    private fun readSyncStatus() {
        val p = phoneSync ?: return
        syncOn = p.on
        syncPlace = p.settings.place
        syncLibrary = p.settings.library
        syncJoined = p.settings.joined
        syncNotice = p.settings.notice
        syncNeedsToken = p.settings.place == SyncSettings.GIST && p.settings.token == null
        syncStatus = p.status()
    }

    /** Syncs now (off the screen's thread); what came in shows at once, reminders included. */
    fun syncNow(asked: Boolean = true) {
        val p = phoneSync ?: return
        if (!p.on || isRemote || syncBusy) return
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
     * Turns on sync through a gist (or, in a debug build, a folder on the phone) with [token]; the first sync joins
     * the library: everything from the other devices comes in first, then this phone's tasks go out.
     */
    fun turnOnSync(place: String, token: String?, library: String) {
        val p = phoneSync ?: return
        if (place == SyncSettings.GIST && token.isNullOrBlank() && p.settings.token == null) {
            syncMessage = "Paste a GitHub token with the gist scope first."
            return
        }
        syncMessage = null
        SyncHub.run({ p.turnOn(place, token?.takeIf { it.isNotBlank() }, library, connected = isRemote) }) { result ->
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
    fun turnOffSync(then: (() -> Unit)? = null) {
        val p = phoneSync ?: return then?.invoke() ?: Unit
        SyncHub.stopBackground(getApplication())
        SyncHub.run({ p.turnOff() }) {
            readSyncStatus()
            syncMessage = null
            then?.invoke()
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
