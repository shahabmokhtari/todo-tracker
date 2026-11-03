package dev.todotracker.core.sync

import java.io.File
import java.io.IOException

/** How a backend is signed in to with a pasted token, and what the Sync screen says about it. */
data class TokenSignIn(
    /** Who the token is for, and where it's sent ("GitHub"). */
    val site: String,
    /** The token field's label ("GitHub token"). */
    val label: String,
    /** Where a new token is made (opened from the Sync screen). */
    val url: String,
    /** What to do there, in a sentence or two. */
    val steps: String,
    /** Said when turning sync on without one. */
    val missing: String,
)

/**
 * One kind of place your devices can meet in (a GitHub gist today; another service later): what it's called, how it's
 * set up, and how to reach it ([SyncRemote]). The app lists its backends in one [SyncBackends]; [PhoneSync], the app's
 * SyncHub and the Sync screen only ever pick through it, so another backend is one more entry there.
 */
class SyncBackend(
    /** Kept in [SyncSettings.place] and in the names of what sync remembers: never change it once shipped. */
    val id: String,
    /** What the Sync screen calls it: "a private GitHub gist". */
    val name: String,
    /** How it works and how it's set up, in a sentence or two (shown before it's turned on). */
    val about: String,
    /** Signed in with a pasted token (null: nothing to sign in to). */
    val token: TokenSignIn?,
    private val connect: (SyncSettings) -> SyncRemote,
) {
    /** The place these settings name (blocking calls: use it off the main thread). */
    fun remote(settings: SyncSettings): SyncRemote = connect(settings)

    companion object {
        /** A private GitHub gist (TodoTracker.Core.Sync.GistRemote): signed in with a token that has the gist scope. */
        fun gist(http: HttpTransport, newTokenUrl: String): SyncBackend = SyncBackend(
            SyncSettings.GIST,
            "a private GitHub gist",
            "Your tasks stay on this phone and sync with your computers and other phones through a private GitHub gist, even when no computer is on. Changes made on more than one device are merged.",
            TokenSignIn(
                site = "GitHub",
                label = "GitHub token",
                url = newTokenUrl,
                steps = "GitHub opens with the gist scope chosen (that's all Onething needs). Choose when it expires, then Generate token, and copy it.",
                missing = "Paste a GitHub token with the gist scope first.",
            ),
        ) { s -> GistRemote(http, { s.token ?: throw IOException("Sign in to GitHub again (Settings › Sync with your other devices).") }, { s.gistId }, { s.gistId = it }, s.library) }

        /** A folder the phone can write to directly (FolderRemote): one folder per library under [root]. */
        fun folder(name: String, about: String, root: (library: String) -> File): SyncBackend =
            SyncBackend(SyncSettings.FOLDER, name, about, token = null) { s -> FolderRemote(root(s.library)) }
    }
}

/** The backends this app can sync through, the first one offered first. */
class SyncBackends(val all: List<SyncBackend>) {
    init {
        require(all.isNotEmpty()) { "At least one sync backend." }
        require(all.map { it.id }.toSet().size == all.size) { "Sync backends need different ids." }
    }

    constructor(vararg backends: SyncBackend) : this(backends.toList())

    /** The one offered first. */
    val default: SyncBackend get() = all.first()

    /** The backend with this id (null: none, or this app doesn't offer it). */
    operator fun get(id: String?): SyncBackend? = all.firstOrNull { it.id == id }

    /** The backend with this id, or why sync can't use it. */
    fun of(id: String?): SyncBackend =
        get(id) ?: throw IllegalStateException("This phone can't sync that way. Choose another in Settings › Sync with your other devices.")
}
