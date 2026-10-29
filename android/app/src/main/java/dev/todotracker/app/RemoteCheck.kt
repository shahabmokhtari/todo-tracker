package dev.todotracker.app

import android.content.Context
import androidx.work.Constraints
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.NetworkType
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.Worker
import androidx.work.WorkerParameters
import java.util.concurrent.TimeUnit

/**
 * While connected to a computer, with the app closed: every 15 minutes or so (when there's a network), the phone reads
 * the computer's board again, so a reminder added there meanwhile still rings on the phone (its alarm is set then).
 * Only while connected; the phone decides when, to save battery.
 */
object RemoteCheck {
    private const val NAME = "remote-check"

    fun start(context: Context) {
        val request = PeriodicWorkRequestBuilder<RemoteCheckWorker>(15, TimeUnit.MINUTES)
            .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
            .build()
        WorkManager.getInstance(context).enqueueUniquePeriodicWork(NAME, ExistingPeriodicWorkPolicy.KEEP, request)
    }

    fun stop(context: Context) {
        WorkManager.getInstance(context).cancelUniqueWork(NAME)
    }
}

class RemoteCheckWorker(context: Context, params: WorkerParameters) : Worker(context, params) {
    override fun doWork(): Result {
        val connection = PhoneConnection(applicationContext)
        if (connection.load() == null) {
            // Disconnected meanwhile: nothing to check any more.
            RemoteCheck.stop(applicationContext)
            return Result.success()
        }
        // Each reminder is told once whoever reads it (the app, if it runs in the background, keeps the same record).
        RemoteAlarm.readAndDeliver(applicationContext, connection)
        // Not reaching it is normal (another network): the next check tries again.
        return Result.success()
    }
}
