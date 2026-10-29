package dev.todotracker.core

import java.time.Instant
import java.time.ZoneId

/**
 * How the phone stands with the computer it's connected to: reading its tasks now, unable to reach it (the last tasks
 * read are shown, and nothing can change until it answers again), or disconnected there (this phone's own board comes
 * back).
 */
sealed interface RemoteState {
    /** Not read yet since the app started (the last read, if any, is shown meanwhile). */
    data object Connecting : RemoteState

    /** The tasks are the computer's, read at [at]. */
    data class Connected(val at: Instant) : RemoteState

    /** The computer didn't answer (or said no, with [reason]); [lastRead] is when the tasks shown were read. */
    data class Unreachable(val lastRead: Instant?, val reason: String? = null) : RemoteState

    /** The computer doesn't know this phone any more. */
    data object Revoked : RemoteState

    /** Changes go to the computer only while it answers. */
    val canChange: Boolean get() = this is Connected

    companion object {
        /** A read came back (changed or not). */
        fun read(now: Instant): RemoteState = Connected(now)

        /** What a failed read or change makes of [state]: a 401 disconnects; anything else is "can't reach it". */
        fun failed(state: RemoteState, error: PhoneException, lastRead: Instant?): RemoteState = when {
            state is Revoked -> Revoked
            error is PhoneException.Revoked -> Revoked
            error is PhoneException.Refused && error.status == 403 -> Unreachable(lastRead, error.message)
            error is PhoneException.Refused -> state // That one change was refused; the computer is still there.
            else -> Unreachable(lastRead)
        }

        /** The calm line shown over the tasks (null: nothing to say). */
        fun banner(state: RemoteState, computer: String, now: Instant, zone: ZoneId): String? = when (state) {
            is Connected, Connecting -> null
            is Revoked -> "This phone was disconnected on $computer. Your tasks on this phone are back, as you left them; reconnect to use the tasks on $computer again."
            is Unreachable -> {
                val from = state.lastRead?.let { "You're seeing your tasks from ${Snooze.describe(it, now, zone)}" } ?: "There are no tasks from it on this phone yet"
                val why = state.reason?.let { " $it" }.orEmpty()
                "Can't reach $computer. $from; changes wait until you're back on its Wi-Fi or Tailscale.$why"
            }
        }
    }
}

/**
 * Reminders from a connected computer's board. The computer tells them itself (and marks them told there), so the
 * phone doesn't go by that: it tells each reminder that comes due after it last looked (each once), for a task still
 * open, unless it was dismissed. Nothing is written back.
 */
object RemoteReminders {
    /** Due after [since] and by [now], earliest first. */
    fun due(board: TaskBoard, since: Instant, now: Instant): List<DueReminder> =
        pending(board).filter { it.reminder.dueAt.isAfter(since) && !it.reminder.dueAt.isAfter(now) }.sortedBy { it.reminder.dueAt }

    /** When the next one goes off (null: none). */
    fun nextAt(board: TaskBoard, now: Instant): Instant? =
        pending(board).map { it.reminder.dueAt }.filter { it.isAfter(now) }.minOrNull()

    private fun pending(board: TaskBoard): List<DueReminder> =
        board.allItems.filter { !it.isDone && !it.isArchived }.flatMap { item ->
            item.reminders.filter { it.dismissedAt == null }.map { DueReminder(item, it) }
        }
}
