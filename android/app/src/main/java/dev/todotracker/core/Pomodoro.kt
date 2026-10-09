package dev.todotracker.core

import java.time.Duration
import java.time.Instant
import java.util.UUID

enum class PomodoroPhase(val wire: String) {
    IDLE("idle"), FOCUS("focus"), SHORT_BREAK("shortBreak"), LONG_BREAK("longBreak");

    val isBreak: Boolean get() = this == SHORT_BREAK || this == LONG_BREAK

    companion object {
        fun of(text: String?): PomodoroPhase = entries.firstOrNull { it.wire == text } ?: IDLE
    }
}

enum class PomodoroEventKind { FOCUS_COMPLETED, BREAK_COMPLETED }

data class PomodoroEvent(val kind: PomodoroEventKind, val itemId: UUID?, val at: Instant)

data class PomodoroSettings(
    val focusMinutes: Int = 25,
    val shortBreakMinutes: Int = 5,
    val longBreakMinutes: Int = 15,
    val focusesBeforeLongBreak: Int = 4,
) {
    fun durationOf(phase: PomodoroPhase): Duration = Duration.ofMinutes(
        when (phase) {
            PomodoroPhase.SHORT_BREAK -> shortBreakMinutes
            PomodoroPhase.LONG_BREAK -> longBreakMinutes
            else -> focusMinutes
        }.toLong().coerceAtLeast(1),
    )
}

/**
 * The focus timer. Breaks start by themselves after a focus session; a new session always needs a start, so the person
 * stays in control. Mirrors TodoTracker.Core.PomodoroTimer (and the Swift one).
 */
class PomodoroTimer(var settings: PomodoroSettings = PomodoroSettings()) {
    var phase: PomodoroPhase = PomodoroPhase.IDLE
        private set
    var endsAt: Instant? = null
        private set
    var pausedRemaining: Duration? = null
        private set
    var itemId: UUID? = null
        private set
    var completedFocusCount: Int = 0
        private set

    /** When the last break ran out by itself (null after a skip, a reset, or a new session): "Break's over" asks only then. */
    var breakEndedAt: Instant? = null
        private set

    val isRunning: Boolean get() = endsAt != null

    fun remaining(now: Instant): Duration {
        endsAt?.let { val left = Duration.between(now, it); return if (left.isNegative) Duration.ZERO else left }
        return pausedRemaining ?: settings.durationOf(phase)
    }

    fun startFocus(now: Instant, itemId: UUID? = null) {
        phase = PomodoroPhase.FOCUS
        this.itemId = itemId
        pausedRemaining = null
        breakEndedAt = null
        endsAt = now.plus(settings.durationOf(PomodoroPhase.FOCUS))
    }

    fun pause(now: Instant) {
        if (endsAt == null || phase == PomodoroPhase.IDLE) return
        pausedRemaining = remaining(now)
        endsAt = null
    }

    fun resume(now: Instant) {
        val left = pausedRemaining ?: return
        if (phase == PomodoroPhase.IDLE) return
        endsAt = now.plus(left)
        pausedRemaining = null
    }

    fun reset() {
        goIdle()
        itemId = null
        completedFocusCount = 0
        breakEndedAt = null
    }

    /** Skips the current phase: focus goes to a (short) break without counting; a break ends. */
    fun skip(now: Instant) {
        if (phase == PomodoroPhase.FOCUS) {
            beginBreak(PomodoroPhase.SHORT_BREAK, now)
        } else if (phase != PomodoroPhase.IDLE) {
            goIdle()
            breakEndedAt = null
        }
    }

    /** Advances at most one phase; call again to catch up after sleep. */
    fun tick(now: Instant): PomodoroEvent? {
        val end = endsAt ?: return null
        if (now.isBefore(end)) return null
        if (phase == PomodoroPhase.FOCUS) {
            completedFocusCount++
            beginBreak(if (completedFocusCount % settings.focusesBeforeLongBreak.coerceAtLeast(1) == 0) PomodoroPhase.LONG_BREAK else PomodoroPhase.SHORT_BREAK, end)
            return PomodoroEvent(PomodoroEventKind.FOCUS_COMPLETED, itemId, end)
        }
        val item = itemId
        goIdle()
        breakEndedAt = end
        return PomodoroEvent(PomodoroEventKind.BREAK_COMPLETED, item, end)
    }

    internal fun detachItem() {
        itemId = null
    }

    internal fun restore(phase: PomodoroPhase, endsAt: Instant?, pausedRemaining: Duration?, itemId: UUID?, completedFocusCount: Int, breakEndedAt: Instant?) {
        this.phase = phase
        this.endsAt = if (phase == PomodoroPhase.IDLE) null else endsAt
        this.pausedRemaining = if (phase == PomodoroPhase.IDLE || endsAt != null) null else pausedRemaining
        this.itemId = itemId
        this.completedFocusCount = completedFocusCount.coerceAtLeast(0)
        this.breakEndedAt = if (phase == PomodoroPhase.IDLE) breakEndedAt else null
    }

    private fun beginBreak(phase: PomodoroPhase, from: Instant) {
        this.phase = phase
        pausedRemaining = null
        endsAt = from.plus(settings.durationOf(phase))
    }

    private fun goIdle() {
        phase = PomodoroPhase.IDLE
        endsAt = null
        pausedRemaining = null
    }
}
