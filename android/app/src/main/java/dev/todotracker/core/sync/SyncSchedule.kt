package dev.todotracker.core.sync

import java.time.Duration
import java.time.Instant

/**
 * When a phone syncs while the app is open (docs/design/phone-sync.md, "When phones sync"): at once on launch, when it
 * comes back to the screen and on Sync now; [settle] after the last change (changes made in a row wait for the last
 * one); and every [every] to pick up the other devices' changes. The app asks [due] often (every second) and calls
 * [started] when it syncs.
 */
class SyncSchedule(val settle: Duration = Duration.ofSeconds(5), val every: Duration = Duration.ofMinutes(1)) {
    private var asked = true
    private var changedAt: Instant? = null
    private var lastAt: Instant? = null

    /** Sync as soon as possible (launch, resume, Sync now). */
    fun soon() {
        asked = true
    }

    /** Something changed on this phone: sync once it has been quiet for [settle]. */
    fun changed(at: Instant) {
        changedAt = at
    }

    /** Whether to sync now. */
    fun due(at: Instant): Boolean {
        if (asked) return true
        val changed = changedAt
        if (changed != null) return !at.isBefore(changed.plus(settle))
        val last = lastAt ?: return true
        return !at.isBefore(last.plus(every))
    }

    /** A sync starts now (what it reads includes every change made before this). */
    fun started(at: Instant) {
        asked = false
        changedAt = null
        lastAt = at
    }
}
