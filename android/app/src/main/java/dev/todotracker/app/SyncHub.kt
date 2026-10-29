package dev.todotracker.app

import android.content.Context
import android.os.Handler
import android.os.Looper
import androidx.work.Constraints
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.Worker
import androidx.work.WorkerParameters
import dev.todotracker.BuildConfig
import dev.todotracker.core.LocalBoard
import dev.todotracker.core.sync.FolderRemote
import dev.todotracker.core.sync.GistRemote
import dev.todotracker.core.sync.PhoneSync
import dev.todotracker.core.sync.PhoneSyncOutcome
import dev.todotracker.core.sync.SyncRemote
import dev.todotracker.core.sync.SyncSettings
import dev.todotracker.core.sync.UrlConnectionTransport
import java.io.File
import java.io.IOException
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/**
 * Sync on this phone, for the whole app (Settings › Sync with your other devices): one [PhoneSync] over the one tasks
 * folder ([BoardHub.store]), every sync on one background thread, one at a time. While the app is open its view model
 * says when ([dev.todotracker.core.sync.SyncSchedule]); with the app closed, WorkManager does (every 15 minutes or so
 * with a network, and a few seconds after a change made from a notification).
 */
object SyncHub {
    private const val PERIODIC = "sync"
    private const val SOON = "sync-soon"

    /** GitHub's page for a new classic token with only the gist scope (there's no OAuth app for sign-in yet). */
    const val NEW_TOKEN_URL = "https://github.com/settings/tokens/new?scopes=gist&description=Todo%20Tracker"

    private val worker = Executors.newSingleThreadExecutor { r -> Thread(r, "sync").apply { isDaemon = true } }
    private var sync: PhoneSync? = null

    /** This phone's sync (null when its tasks can't be opened: changes are off then). */
    fun get(context: Context): PhoneSync? = synchronized(this) {
        sync?.let { return it }
        val app = context.applicationContext
        val store = BoardHub.store(app) ?: return null
        val settings = SyncSettings(PrefsValues(app.getSharedPreferences("sync", Context.MODE_PRIVATE)), KeystoreBox("sync-token"))
        PhoneSync(
            store, settings, File(app.filesDir, "sync"), { PhoneConnection.phoneName(app) }, { s -> remote(app, s) },
            { LocalBoard.legacyFile(app.filesDir).takeIf { it.exists() } },
            installId(app),
        ).also { sync = it }
    }

    /**
     * This install's id, kept in no-backup storage (never restored or moved to another phone): sync state that came
     * along with a backup anyway belongs to another install, and this phone joins as a new device.
     */
    private fun installId(context: Context): String {
        val file = File(context.noBackupFilesDir, "install-id")
        file.takeIf { it.exists() }?.readText()?.trim()?.takeIf { it.isNotEmpty() }?.let { return it }
        val id = java.util.UUID.randomUUID().toString()
        dev.todotracker.core.vault.VaultFiles.writeAtomic(file, id.toByteArray(Charsets.UTF_8))
        return id
    }

    /** Debug builds can sync through a folder on the phone (to try it against a computer without a GitHub account). */
    val folderAllowed: Boolean get() = BuildConfig.DEBUG

    /** The folder a debug build syncs through: `Android/data/dev.todotracker/files/TodoTrackerSync/<library>`. */
    fun debugFolder(context: Context, library: String): File = File(File(context.getExternalFilesDir(null) ?: context.filesDir, "TodoTrackerSync"), library)

    private fun remote(context: Context, s: SyncSettings): SyncRemote = when (s.place) {
        SyncSettings.FOLDER -> if (folderAllowed) FolderRemote(debugFolder(context, s.library)) else throw IllegalStateException("Folder sync is for debug builds only.")
        else -> GistRemote(UrlConnectionTransport, { s.token ?: throw IOException("Sign in to GitHub again (Settings › Sync with your other devices).") }, { s.gistId }, { s.gistId = it }, s.library)
    }

    /** Runs [work] on the sync thread, then [done] on the main thread with its result. */
    fun <T> run(work: () -> T, done: (Result<T>) -> Unit) {
        val main = Handler(Looper.getMainLooper())
        worker.execute {
            val result = runCatching(work)
            main.post { done(result) }
        }
    }

    /** One sync on the calling thread (a worker's). */
    fun syncHere(context: Context): PhoneSyncOutcome? {
        val s = get(context) ?: return null
        if (!s.on || PhoneConnection(context).load() != null) return null
        return worker.submit<PhoneSyncOutcome> { s.sync() }.get()
    }

    /** With the app closed: a sync now and then (15 minutes at least, with a network). */
    fun startBackground(context: Context) {
        val request = PeriodicWorkRequestBuilder<SyncWorker>(15, TimeUnit.MINUTES)
            .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
            .build()
        WorkManager.getInstance(context).enqueueUniquePeriodicWork(PERIODIC, ExistingPeriodicWorkPolicy.KEEP, request)
    }

    fun stopBackground(context: Context) {
        WorkManager.getInstance(context).cancelUniqueWork(PERIODIC)
        WorkManager.getInstance(context).cancelUniqueWork(SOON)
    }

    /** Changed with the app closed (a notification's button): synced a few seconds later (later ones push it back). */
    fun soonInBackground(context: Context) {
        if (get(context)?.on != true) return
        val request = OneTimeWorkRequestBuilder<SyncWorker>()
            .setInitialDelay(5, TimeUnit.SECONDS)
            .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
            .build()
        WorkManager.getInstance(context).enqueueUniqueWork(SOON, ExistingWorkPolicy.REPLACE, request)
    }
}

/** A sync with the app closed; what came in is shown (if the app runs) and its reminders set. */
class SyncWorker(context: Context, params: WorkerParameters) : Worker(context, params) {
    override fun doWork(): Result {
        val app = applicationContext
        val sync = SyncHub.get(app)
        if (sync == null || !sync.on || PhoneConnection(app).load() != null) {
            SyncHub.stopBackground(app)
            return Result.success()
        }
        val outcome = try {
            SyncHub.syncHere(app)
        } catch (e: Exception) {
            // Kept in the sync status (the Sync screen says it); the next run tries again.
            null
        }
        if (outcome?.changed == true) BoardHub.reloaded(app)
        return Result.success()
    }
}
