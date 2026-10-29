package dev.todotracker.core.sync

import dev.todotracker.core.vault.VaultStore
import java.io.File
import java.time.Instant

/** Small settings kept on the phone (SharedPreferences in the app, a map in tests); null removes a key. */
interface KeyValues {
    operator fun get(key: String): String?

    operator fun set(key: String, value: String?)
}

/** Seals a secret so what's kept on the phone can only be read here (the Android Keystore in the app). */
interface SecretBox {
    fun seal(plain: String): String

    /** Null when it can't be opened any more (the phone's key is gone, e.g. after a restore to another phone). */
    fun open(sealed: String): String?
}

/** How this phone gets its tasks: only on it, syncing on its own with the other devices, or a connected computer's. */
enum class PhoneMode { ON_THIS_PHONE, SYNCING, CONNECTED }

/** What sync is set to on this phone: where it meets the other devices, the library, and the GitHub sign-in (sealed). */
class SyncSettings(private val values: KeyValues, private val box: SecretBox) {
    companion object {
        /** The same default as the computers (their tasks folder's name), so a new phone meets them. */
        const val DEFAULT_LIBRARY = "Todo Tracker"
        const val GIST = "gist"

        /** A folder on the phone (debug builds only: to try sync against a computer without GitHub). */
        const val FOLDER = "folder"
    }

    /** Where it syncs ([GIST], [FOLDER]); null: sync is off. */
    var place: String?
        get() = values["place"]
        set(value) {
            values["place"] = value
        }

    val enabled: Boolean get() = place != null

    var library: String
        get() = values["library"]?.trim()?.takeIf { it.isNotEmpty() } ?: DEFAULT_LIBRARY
        set(value) {
            values["library"] = value.trim().takeIf { it.isNotEmpty() && it != DEFAULT_LIBRARY }
        }

    /** The library's gist, once found or made (GistRemote's loadId/saveId). */
    var gistId: String?
        get() = values["gistId"]
        set(value) {
            values["gistId"] = value
        }

    /** The GitHub token (with the gist scope), or null (none, or it can't be read on this phone any more). */
    var token: String?
        get() = values["token"]?.let(box::open)
        set(value) {
            values["token"] = value?.let(box::seal)
        }

    /** Which place and library this phone has joined (pulled everything once, then published): "<place>|<library>". */
    var joinedTo: String?
        get() = values["joinedTo"]
        set(value) {
            values["joinedTo"] = value
        }

    val joined: Boolean get() = place != null && joinedTo == "$place|$library"

    /** Something to say about the last sync that isn't an error (contents still on their way). */
    var notice: String?
        get() = values["notice"]
        set(value) {
            values["notice"] = value
        }
}

/** What one sync did here: [changed] when the tasks on this phone changed (reload and redraw). */
data class PhoneSyncOutcome(val changed: Boolean, val joined: Boolean, val result: SyncResult)

/**
 * Sync on a phone, on its own (phase 2 of docs/design/phone-sync.md): the phone's tasks folder ([store], the one the
 * app uses) syncs through a gist with the user's other devices. The first sync with a place joins it
 * ([SyncEngine.join]: everything from the others comes in before anything is published). What sync remembers lives
 * in [stateDir] (`filesDir/sync`), never in the tasks folder. Calls block (network): run them off the main thread.
 */
class PhoneSync(
    private val store: VaultStore,
    val settings: SyncSettings,
    private val stateDir: File,
    private val deviceName: () -> String,
    private val remoteFor: (SyncSettings) -> SyncRemote,
    /** The board.json an older version kept, if it's still there (brought in beside what came, at the first join). */
    private val legacyBoard: () -> File? = { null },
    private val clock: () -> Instant = Instant::now,
) {
    companion object {
        const val WAITING = "Waiting for everything from your other devices before this phone's tasks go out. It tries again in a minute."
        const val NOT_ARRIVED = "Some changes from another device haven't arrived yet. They come in with a later sync."
    }

    private val lock = Any()
    private var engine: SyncEngine? = null
    private var engineKey: String? = null

    val on: Boolean get() = settings.enabled

    private fun state(place: String): SyncState = SyncState(File(stateDir, place), stateDir, deviceName())

    private fun engine(): SyncEngine {
        val place = settings.place ?: throw IllegalStateException("Sync is off on this phone.")
        val key = "$place|${settings.library}|${deviceName()}"
        engine?.takeIf { engineKey == key }?.let { return it }
        return SyncEngine(store, remoteFor(settings), state(place), clock).also {
            engine = it
            engineKey = key
        }
    }

    /** How the last sync went (kept while sync is off, for when it's on again). Read without waiting for a sync. */
    fun status(): SyncStatus = state(settings.place ?: SyncSettings.GIST).status

    /** This phone's id among the devices. */
    val deviceId: String get() = state(settings.place ?: SyncSettings.GIST).deviceId

    /**
     * Turns sync on (or signs in again): [token] is kept sealed. Not while the phone uses a computer's tasks
     * ([connected]): the two are exclusive. The next [sync] joins: it pulls first and publishes after.
     */
    fun turnOn(place: String, token: String?, library: String, connected: Boolean) = synchronized(lock) {
        check(!connected) { "This phone uses a computer's tasks. Disconnect it first (Settings › Connect to a computer), then turn on sync." }
        val clean = library.trim().ifEmpty { SyncSettings.DEFAULT_LIBRARY }
        if (settings.place != place || settings.library != clean) settings.gistId = null
        settings.place = place
        settings.library = clean
        if (token != null) settings.token = token.trim()
        settings.notice = null
        engine = null
    }

    /** Another library: the next sync joins it (pulling first). */
    fun changeLibrary(library: String) = synchronized(lock) {
        val clean = library.trim().ifEmpty { SyncSettings.DEFAULT_LIBRARY }
        if (clean == settings.library) return@synchronized
        settings.library = clean
        settings.gistId = null
        settings.notice = null
        engine = null
    }

    /** Stops syncing: the tasks stay on this phone as they are, and the GitHub sign-in is forgotten here. */
    fun turnOff() = synchronized(lock) {
        settings.place = null
        settings.token = null
        settings.gistId = null
        settings.joinedTo = null
        settings.notice = null
        engine = null
    }

    /** One sync: the first with a place joins it; after a change came in, the store has already re-read its files. */
    fun sync(): PhoneSyncOutcome = synchronized(lock) {
        val e = engine()
        if (!settings.joined) {
            val r = try {
                e.join(legacyBoard())
            } catch (ex: Exception) {
                // Said on the Sync screen like any sync's problem (join doesn't keep it by itself).
                e.state.status = e.state.status.copy(lastError = ex.message)
                throw ex
            }
            if (r.joined) settings.joinedTo = "${settings.place}|${settings.library}"
            settings.notice = if (r.joined) null else WAITING
            return@synchronized PhoneSyncOutcome(r.pulled.changes > 0 || r.imported > 0, r.joined, r.published ?: r.pulled)
        }
        val r = e.sync()
        settings.notice = if (!r.complete && r.problem == null) NOT_ARRIVED else null
        PhoneSyncOutcome(r.changes > 0, joined = false, r)
    }

    /** Stops merging a device that's gone. */
    fun forget(device: String) = synchronized(lock) { engine().forget(device) }

    /** Hides a conflict from the list (both versions stay in the task). */
    fun dismiss(key: String) = synchronized(lock) { engine().dismiss(key) }
}
