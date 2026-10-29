package dev.todotracker.core

import java.time.Instant

/** A reminder to deliver now, with its task. */
data class DueReminder(val item: WorkItem, val reminder: Reminder)

/**
 * Which reminders the phone tells about, and when (ReminderDispatcher in C#): a reminder is delivered once, when it's
 * due, for a task that's still open and not archived. A snooze with "remind me" is one too (it ends with one).
 */
object Reminders {
    /** Due now and not delivered yet, earliest first. */
    fun due(board: TaskBoard, now: Instant): List<DueReminder> =
        pending(board).filter { !it.reminder.dueAt.isAfter(now) }.sortedBy { it.reminder.dueAt }

    /** When the next one not due yet goes off (null: none). */
    fun nextAt(board: TaskBoard, now: Instant): Instant? =
        pending(board).map { it.reminder.dueAt }.filter { it.isAfter(now) }.minOrNull()

    private fun pending(board: TaskBoard): List<DueReminder> =
        board.allItems.filter { !it.isDone && !it.isArchived }.flatMap { item ->
            item.reminders.filter { it.dismissedAt == null && it.notifiedAt == null }.map { DueReminder(item, it) }
        }
}
