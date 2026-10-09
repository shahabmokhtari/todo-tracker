package dev.todotracker.core

import java.time.Duration
import java.time.Instant
import java.util.UUID

/** Where a stretch of time came from: a timer started by hand (or typed in), or a focus session. */
enum class TimeSource(val wire: String) {
    MANUAL("manual"), FOCUS("focus");

    companion object {
        /** A kind from a newer version reads as manual time. */
        fun of(text: String?): TimeSource = entries.firstOrNull { it.wire == text } ?: MANUAL
    }
}

/** A stretch of time spent on a task; a running one has no end. Same as TodoTracker.Core.TimeEntry. */
class TimeEntry(
    val id: UUID = UUID.randomUUID(),
    val start: Instant,
    var end: Instant? = null,
    val source: TimeSource,
    /** The device that timed it. */
    val device: String? = null,
) {
    val isRunning: Boolean get() = end == null

    /** How long it lasted (a running one: until [now], and at most [TaskBoard.FORGOTTEN_AFTER]). */
    fun duration(now: Instant): Duration {
        val until = end ?: minOf(now, start.plus(TaskBoard.FORGOTTEN_AFTER))
        val d = Duration.between(start, until)
        return if (d.isNegative) Duration.ZERO else d
    }
}

/** A timer running now: on which task, and since when. */
data class RunningTimer(val item: WorkItem, val entry: TimeEntry)
