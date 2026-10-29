package dev.todotracker.app

import android.app.AlarmManager
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.widget.Toast
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import dev.todotracker.R
import dev.todotracker.core.Actor
import dev.todotracker.core.BoardException
import dev.todotracker.core.BoardFileStore
import dev.todotracker.core.DueReminder
import dev.todotracker.core.Reminders
import dev.todotracker.core.TaskBoard
import dev.todotracker.core.TaskLinks
import java.io.File
import java.time.Instant
import java.time.temporal.ChronoUnit
import java.util.UUID

/**
 * The one board on this phone, whoever changes it: the app while it runs (its view model registers as [live]), or,
 * with the app closed, an alarm or a notification button reading and writing the file. One at a time ([lock]).
 */
object BoardHub {
    /** Held while the board file is read or written. */
    val lock = Any()

    /** The running app's board: changes go through it (so the screen and the file agree). */
    @Volatile
    var live: LiveBoard? = null

    interface LiveBoard {
        /** Applies [action] (on the main thread), saves, and redraws; false when the board refused it (with why). */
        fun applyOutside(action: (TaskBoard, Instant) -> Unit): Result<Unit>
    }

    fun file(context: Context) = File(context.filesDir, "board.json")

    /**
     * Applies [action] to the board: through the running app, or on the saved file (then saved, and the next reminder
     * set). The result says whether the board took it.
     */
    fun change(context: Context, action: (TaskBoard, Instant) -> Unit, done: (Result<Unit>) -> Unit) {
        val app = live
        if (app != null) {
            Handler(Looper.getMainLooper()).post { done(app.applyOutside(action)) }
            return
        }
        Thread {
            val result = synchronized(lock) {
                runCatching {
                    val store = BoardFileStore(file(context))
                    // A board this app can't read (a newer version, damaged) is never written over from here.
                    val board = store.load().also { if (store.problem != null) throw BoardException("Open the app to check your board.") }
                    val now = Instant.now()
                    action(board, now)
                    ReminderAlarm.deliver(context, board, now)
                    store.save(board)
                    ReminderAlarm.schedule(context, Reminders.nextAt(board, now))
                }
            }
            Handler(Looper.getMainLooper()).post { done(result) }
        }.start()
    }
}

/**
 * Says when a reminder is due, with the app closed or the phone locked: an alarm at the next one posts a notification
 * (the task, its reminder, and Mark done / In an hour); tapping it opens the task. Snoozes with a reminder end with
 * one. Exact when the phone allows exact alarms; otherwise the phone may deliver it a little late. Each reminder is
 * delivered once (it's marked as delivered on the board, like on the desktop).
 */
object ReminderAlarm {
    private const val CHANNEL = "reminders"
    internal const val EXTRA_ITEM = "item"
    internal const val EXTRA_NOTIFICATION = "notification"
    internal const val ACTION_DONE = "dev.todotracker.REMINDER_DONE"
    internal const val ACTION_LATER = "dev.todotracker.REMINDER_LATER"

    /** Sets the alarm for the next reminder (null: none, the alarm is cancelled). */
    fun schedule(context: Context, at: Instant?) {
        val alarms = context.getSystemService(AlarmManager::class.java) ?: return
        val pending = PendingIntent.getBroadcast(
            context, 0, Intent(context, ReminderAlarmReceiver::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        alarms.cancel(pending)
        if (at == null) return
        if (FocusAlarm.exactAllowed(context)) {
            alarms.setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, at.toEpochMilli(), pending)
        } else {
            alarms.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, at.toEpochMilli(), pending)
        }
    }

    /** Delivers what's due on [board] now: marked as delivered, then told. True when something was. */
    fun deliver(context: Context, board: TaskBoard, now: Instant): Boolean {
        val due = Reminders.due(board, now)
        for (d in due) board.markNotified(d.item.id, d.reminder.id, now)
        for (d in due) notify(context, d)
        return due.isNotEmpty()
    }

    private fun notify(context: Context, due: DueReminder) {
        if (Build.VERSION.SDK_INT >= 33 && ContextCompat.checkSelfPermission(context, android.Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return
        val manager = context.getSystemService(NotificationManager::class.java) ?: return
        manager.createNotificationChannel(NotificationChannel(CHANNEL, "Reminders", NotificationManager.IMPORTANCE_HIGH).apply {
            description = "When a task's reminder is due, or a snooze ends"
        })
        val id = notificationId(due.item.id)
        val open = PendingIntent.getActivity(
            context, id,
            Intent(Intent.ACTION_VIEW, Uri.parse(TaskLinks.forTask(due.item.id)), context, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val where = due.item.ancestors.reversed().joinToString(" › ") { it.title }
        val notification = NotificationCompat.Builder(context, CHANNEL)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(due.item.title)
            .setContentText(due.reminder.message)
            .setSubText(where.ifEmpty { null })
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_REMINDER)
            .setAutoCancel(true)
            .setContentIntent(open)
            .addAction(0, "Mark done", action(context, ACTION_DONE, due.item.id, id))
            .addAction(0, "In an hour", action(context, ACTION_LATER, due.item.id, id))
            .build()
        manager.notify(id, notification)
    }

    /** One notification per task: a newer reminder replaces an older one about the same task. */
    fun notificationId(item: UUID): Int = item.hashCode() and 0x7fffffff or 0x10000

    private fun action(context: Context, action: String, item: UUID, notification: Int): PendingIntent =
        PendingIntent.getBroadcast(
            context, notification * 2 + if (action == ACTION_DONE) 0 else 1,
            Intent(context, ReminderActionReceiver::class.java).setAction(action).putExtra(EXTRA_ITEM, item.toString()).putExtra(EXTRA_NOTIFICATION, notification),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
}

/** The next reminder is due: delivered (through the app if it runs), and the alarm set for the one after. */
class ReminderAlarmReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val pending = goAsync()
        // Delivering is part of every change (the app's or the file's): an empty change is enough.
        BoardHub.change(context.applicationContext, { _, _ -> }) { pending.finish() }
    }
}

/** Mark done or In an hour, from a reminder's notification (the person did it: it's theirs on the timeline). */
class ReminderActionReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val id = intent.getStringExtra(ReminderAlarm.EXTRA_ITEM)?.let { runCatching { UUID.fromString(it) }.getOrNull() } ?: return
        val app = context.applicationContext
        val pending = goAsync()
        app.getSystemService(NotificationManager::class.java)?.cancel(intent.getIntExtra(ReminderAlarm.EXTRA_NOTIFICATION, 0))
        val action: (TaskBoard, Instant) -> Unit = when (intent.action) {
            ReminderAlarm.ACTION_DONE -> { board, now -> board.complete(id, Actor.USER, now) }
            ReminderAlarm.ACTION_LATER -> { board, now -> board.scheduleNextAction(id, now.plus(1, ChronoUnit.HOURS), notify = true, actor = Actor.USER, now = now) }
            else -> return pending.finish()
        }
        BoardHub.change(app, action) { result ->
            result.exceptionOrNull()?.let { Toast.makeText(app, it.message ?: "Couldn't change the task.", Toast.LENGTH_LONG).show() }
            pending.finish()
        }
    }
}
