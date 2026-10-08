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
import dev.todotracker.core.QuickCaptureParser
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
            while (true) {
                delay(30_000)
                refresh()
            }
        }
    }

    fun refresh() {
        if (selectedGroup != null && board.groups.none { it.id == selectedGroup }) selectedGroup = null
        groups = board.groups.map { it.id to it.name }
        dashboard = Agenda.build(board, Instant.now(), selectedGroup)
    }

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

    fun snooze(id: UUID, minutes: Long) = change("Snoozed") { board.scheduleNextAction(id, it.plusSeconds(minutes * 60), notify = true, now = it) }

    fun tomorrow(id: UUID) = change("See you tomorrow") {
        board.scheduleNextAction(id, QuickCaptureParser.tomorrowMorning(it, ZoneId.systemDefault()), notify = true, now = it)
    }

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
