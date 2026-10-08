package dev.todotracker.app

import android.app.Application
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import dev.todotracker.core.Agenda
import dev.todotracker.core.BoardException
import dev.todotracker.core.BoardFileStore
import dev.todotracker.core.Dashboard
import dev.todotracker.core.NewTask
import dev.todotracker.core.QuickCaptureParser
import dev.todotracker.core.TaskBoard
import java.io.File
import java.time.Instant
import java.time.ZoneId
import java.util.UUID
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/** The board on this phone: every change is saved at once; the lists follow the clock (waiting tasks come back). */
class BoardViewModel(application: Application) : AndroidViewModel(application) {
    private val store = BoardFileStore(File(application.filesDir, "board.json"))
    private var board: TaskBoard

    var dashboard: Dashboard by mutableStateOf(Dashboard(null, emptyList(), emptyList(), emptyMap()))
        private set
    var groups: List<Pair<UUID, String>> by mutableStateOf(emptyList())
        private set
    var selectedGroup: UUID? by mutableStateOf(null)
        private set
    var status: String? by mutableStateOf(null)
    var readOnly: Boolean by mutableStateOf(false)
        private set

    init {
        board = try {
            store.load()
        } catch (e: BoardException) {
            readOnly = true
            status = "Couldn't open your saved board (${e.message}). Changes are off so nothing gets overwritten."
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

    /** Quick capture: "Call mum @tomorrow !high due:3d". True when it was added. */
    fun capture(text: String): Boolean = change("Added") { now ->
        val capture = QuickCaptureParser.parse(text, now, ZoneId.systemDefault())
        val item = board.addTask(NewTask(capture.title, groupId = selectedGroup, priority = capture.priority, deadline = capture.deadline), now = now)
        capture.nextActionAt?.let { board.scheduleNextAction(item.id, it, notify = true, now = now) }
    }

    fun complete(id: UUID) = change("Done") { board.complete(id, now = it) }

    fun snooze(id: UUID, minutes: Long) = change("Snoozed") { board.scheduleNextAction(id, it.plusSeconds(minutes * 60), notify = true, now = it) }

    fun tomorrow(id: UUID) = change("See you tomorrow") {
        board.scheduleNextAction(id, QuickCaptureParser.tomorrowMorning(it, ZoneId.systemDefault()), notify = true, now = it)
    }

    fun bringBack(id: UUID) = change(null) { board.clearNextAction(id, now = it) }

    fun dismissReminder(id: UUID, reminderId: UUID) = change(null) { board.dismissReminder(id, reminderId, now = it) }

    fun addNote(id: UUID, text: String) = change("Note saved") { board.addNote(id, text, now = it) }

    fun addGroup(name: String) = change(null) { selectedGroup = board.addGroup(name, now = it).id }

    private fun change(message: String?, action: (Instant) -> Unit): Boolean {
        if (readOnly) {
            status = "Your saved board couldn't be opened, so changes are off to protect it."
            return false
        }
        return try {
            action(Instant.now())
            store.save(board)
            status = message
            true
        } catch (e: BoardException) {
            status = e.message
            false
        } finally {
            refresh()
        }
    }
}
