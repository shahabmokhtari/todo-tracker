package dev.todotracker.core

import java.time.Duration
import java.time.Instant

/** A break due now (or, with [isOver], one that just ran out): when it ends, whether it's the long one, its tip. */
data class BreakPrompt(val until: Instant, val isLong: Boolean, val isOver: Boolean = false) {
    val title: String get() = if (isOver) "Break’s over" else if (isLong) "Time for a longer break" else "Time for a break"
    val tip: String get() = if (isOver) "Ready for the next one? One small step is enough." else Breaks.tip(until)
}

/** When a phase of the focus timer ends: a focus session ([focus]) or a break. */
data class PhaseEnd(val at: Instant, val focus: Boolean)

/**
 * The full-screen break after a focus session, and "Break's over" when it runs out: the same rules as the web app,
 * Windows and the Apple apps (tests/fixtures/breaks.json checks all of them).
 */
object Breaks {
    /**
     * When the running phases end, for the phone to say so with the app asleep: a focus session's end and the break
     * after it (long every few sessions), or a break's end. Nothing while paused or idle.
     */
    fun phaseEnds(timer: PomodoroTimer): List<PhaseEnd> {
        val end = timer.endsAt?.takeIf { timer.isRunning } ?: return emptyList()
        return when (timer.phase) {
            PomodoroPhase.FOCUS -> {
                val long = (timer.completedFocusCount + 1) % timer.settings.focusesBeforeLongBreak.coerceAtLeast(1) == 0
                listOf(PhaseEnd(end, true), PhaseEnd(end.plus(timer.settings.durationOf(if (long) PomodoroPhase.LONG_BREAK else PomodoroPhase.SHORT_BREAK)), false))
            }
            PomodoroPhase.IDLE -> emptyList()
            else -> listOf(PhaseEnd(end, false))
        }
    }

    /** The phase end that just went by unannounced (the phone was off): within [overFor], the latest one. */
    fun missed(ends: List<PhaseEnd>, now: Instant): PhaseEnd? =
        ends.lastOrNull { !it.at.isAfter(now) }?.takeIf { Duration.between(it.at, now) < overFor }

    /** Same tips, in the same order, as the web app. */
    val tips = listOf(
        "Stand up and stretch for a minute.",
        "Look at something far away: rest your eyes.",
        "Drink a glass of water.",
        "Take five slow breaths.",
        "Walk around the room.",
        "Roll your shoulders and unclench your jaw.",
        "Step outside or open a window.",
    )

    /** Taps this soon after the screen appears are still meant for what was under it, not an answer. */
    val settleTime: Duration = Duration.ofMillis(800)

    /** How long "Break's over" keeps asking after a break ran out. */
    val overFor: Duration = Duration.ofMinutes(15)

    /** The break due at [now]: during a running break, and the moment a running focus session ends. */
    fun due(phase: PomodoroPhase, running: Boolean, endsAt: Instant?, completedFocusCount: Int, settings: PomodoroSettings, now: Instant): BreakPrompt? {
        val end = endsAt ?: return null
        if (!running) return null
        return when {
            phase.isBreak -> if (end.isAfter(now)) BreakPrompt(end, phase == PomodoroPhase.LONG_BREAK) else null
            phase == PomodoroPhase.FOCUS && !end.isAfter(now) -> {
                val isLong = (completedFocusCount + 1) % settings.focusesBeforeLongBreak.coerceAtLeast(1) == 0
                val until = end.plus(settings.durationOf(if (isLong) PomodoroPhase.LONG_BREAK else PomodoroPhase.SHORT_BREAK))
                if (until.isAfter(now)) BreakPrompt(until, isLong) else null
            }
            else -> null
        }
    }

    fun due(timer: PomodoroTimer, now: Instant): BreakPrompt? =
        due(timer.phase, timer.isRunning, timer.endsAt, timer.completedFocusCount, timer.settings, now)

    /**
     * Whether to ask "Break's over": the break this app saw ([seen], its end) ran out by itself, not long ago, and it
     * wasn't answered with Not now. The timer says when a break ran out (never for a skipped or paused one); until it has
     * moved on, a running break at its end counts too.
     */
    fun over(phase: PomodoroPhase, running: Boolean, endsAt: Instant?, breakEndedAt: Instant?, now: Instant, seen: Instant?, dismissedOver: Instant?): Boolean {
        if (seen == null || seen == dismissedOver || now.isBefore(seen) || Duration.between(seen, now) >= overFor) return false
        if (phase == PomodoroPhase.IDLE) return breakEndedAt == seen
        val end = endsAt ?: return false
        if (!running) return false
        // The break itself, run out; or the session before it (both ran out before the timer moved on). Not a new session.
        return if (phase == PomodoroPhase.FOCUS) end.isBefore(seen) else end == seen
    }

    /** The web app's tipFor: a hash of the break's end in milliseconds. */
    fun tip(until: Instant): String {
        var hash = 7
        for (c in until.toEpochMilli().toString()) hash = hash * 31 + c.code
        return tips[(Math.abs(hash.toLong()) % tips.size).toInt()]
    }

    /** "4:59": minutes and seconds left. */
    fun clock(until: Instant, now: Instant): String {
        val millis = Duration.between(now, until).toMillis().coerceAtLeast(0)
        val seconds = (millis + 999) / 1000
        return "${seconds / 60}:${(seconds % 60).toString().padStart(2, '0')}"
    }
}
