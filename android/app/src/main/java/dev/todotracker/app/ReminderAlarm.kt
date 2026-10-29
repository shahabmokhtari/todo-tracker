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
import dev.todotracker.core.BoardCodec
import dev.todotracker.core.BoardException
import dev.todotracker.core.BoardFileStore
import dev.todotracker.core.DueReminder
import dev.todotracker.core.PhoneException
import dev.todotracker.core.Reminders
import dev.todotracker.core.RemoteAction
import dev.todotracker.core.RemoteReminders
import dev.todotracker.core.TaskBoard
import dev.todotracker.core.TaskLinks
import java.io.File
import java.time.Instant
import java.time.temporal.ChronoUnit
import java.util.UUID
import kotlinx.coroutines.launch

/**
 * The one board on this phone, whoever changes it: the app while it runs (its view model registers as [live]), or,
 * with the app closed, an alarm or a notification button reading and writing the file. One at a time ([lock]).
 */
object BoardHub {
    /** Held while the board file is read or written. */
    val lock = Any()

    /**
     * Where the file is read and written, one thing at a time and in order, for the whole app (not one screen): a save
     * asked for just before the app closes still happens, and is done before anything reads the file again.
     */
    @OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
    val disk = kotlinx.coroutines.CoroutineScope(kotlinx.coroutines.SupervisorJob() + kotlinx.coroutines.Dispatchers.IO.limitedParallelism(1))

    /** The running app's board: changes go through it (so the screen and the file agree). Read and set on the main thread. */
    @Volatile
    var live: LiveBoard? = null

    interface LiveBoard {
        /** Applies [action] (on the main thread), saves, and redraws; false when the board refused it (with why). */
        fun applyOutside(action: (TaskBoard, Instant) -> Unit): Result<Unit>

        /** Connected to a computer: something changed there (from a notification), or a reminder is due: read it again. */
        fun remoteChanged()
    }

    fun file(context: Context) = File(context.filesDir, "board.json")

    /** Runs [read] on the disk queue (after every save asked for before it) and waits for it. */
    fun <T> readNow(read: () -> T): T = kotlinx.coroutines.runBlocking(disk.coroutineContext) { synchronized(lock) { read() } }

    /**
     * Applies [action] to the board: through the running app, or (the app closed meanwhile, or not running) on the
     * saved file, then saved and the next reminder set. The result says whether the board took it.
     */
    fun change(context: Context, action: (TaskBoard, Instant) -> Unit, done: (Result<Unit>) -> Unit) {
        val main = Handler(Looper.getMainLooper())
        main.post {
            // Decided on the main thread, where the app's board comes and goes: no change is lost in between.
            val app = live
            if (app != null) {
                done(app.applyOutside(action))
                return@post
            }
            disk.launch {
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
                main.post { done(result) }
            }
        }
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

    /**
     * Tells what's due on [board] now. Only what was shown is marked delivered: with notifications off, a reminder
     * stays pending (Today shows it) and is told once they're on. True when something was delivered.
     */
    fun deliver(context: Context, board: TaskBoard, now: Instant): Boolean = Reminders.deliver(board, now) { notify(context, it) } > 0

    /**
     * Connected to a computer: tells the reminders on its board that came due since the phone last looked (once each),
     * and gives when the next one is due (for its alarm). Nothing is written to the computer (it tells its own). With
     * notifications off, one that couldn't be shown (and those after it) is told once they're on.
     */
    fun deliverRemote(context: Context, connection: PhoneConnection, board: TaskBoard, now: Instant): Instant? {
        val due = RemoteReminders.due(board, connection.remindersSince, now)
        if (due.isNotEmpty()) {
            val missed = due.firstOrNull { !notify(context, it) }
            connection.remindersSince = missed?.reminder?.dueAt?.minusMillis(1) ?: now
        }
        return RemoteReminders.nextAt(board, now)
    }

    /** A button on a reminder couldn't reach the computer: the notification says so, and opens the task. */
    fun couldNotReach(context: Context, item: UUID, notification: Int, message: String) {
        if (Build.VERSION.SDK_INT >= 33 && ContextCompat.checkSelfPermission(context, android.Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return
        val manager = context.getSystemService(NotificationManager::class.java) ?: return
        val open = PendingIntent.getActivity(
            context, notification,
            Intent(Intent.ACTION_VIEW, Uri.parse(TaskLinks.forTask(item)), context, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        manager.notify(
            notification,
            NotificationCompat.Builder(context, CHANNEL)
                .setSmallIcon(R.drawable.ic_notification)
                .setContentTitle("Not changed yet")
                .setContentText(message)
                .setStyle(NotificationCompat.BigTextStyle().bigText(message))
                .setAutoCancel(true)
                .setContentIntent(open)
                .build(),
        )
    }

    /** Shows the reminder; false when the phone won't (notifications off for the app or its reminders). */
    private fun notify(context: Context, due: DueReminder): Boolean {
        if (Build.VERSION.SDK_INT >= 33 && ContextCompat.checkSelfPermission(context, android.Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return false
        val manager = context.getSystemService(NotificationManager::class.java) ?: return false
        manager.createNotificationChannel(NotificationChannel(CHANNEL, "Reminders", NotificationManager.IMPORTANCE_HIGH).apply {
            description = "When a task's reminder is due, or a snooze ends"
        })
        if (!manager.areNotificationsEnabled() || manager.getNotificationChannel(CHANNEL)?.importance == NotificationManager.IMPORTANCE_NONE) return false
        val id = notificationId(due.item.id)
        val open = PendingIntent.getActivity(
            context, id,
            Intent(Intent.ACTION_VIEW, Uri.parse(TaskLinks.forTask(due.item.id)), context, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val where = due.item.ancestors.reversed().joinToString(" › ") { it.title }
        val builder = NotificationCompat.Builder(context, CHANNEL)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(due.item.title)
            .setContentText(due.reminder.message)
            .setSubText(where.ifEmpty { null })
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_REMINDER)
            .setAutoCancel(true)
            .setContentIntent(open)
        // Mark done would also close its open subtasks without asking: then it's Open (the app asks there).
        if (due.item.hasOpenChildren) builder.addAction(0, "Open", open) else builder.addAction(0, "Mark done", action(context, ACTION_DONE, due.item.id, id))
        builder.addAction(0, "In an hour", action(context, ACTION_LATER, due.item.id, id))
        manager.notify(id, builder.build())
        return true
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
        val app = context.applicationContext
        val connection = PhoneConnection(app)
        if (connection.load() != null) {
            RemoteAlarm.due(app, connection) { pending.finish() }
            return
        }
        // Delivering is part of every change (the app's or the file's): an empty change is enough.
        BoardHub.change(app, { _, _ -> }) { pending.finish() }
    }
}

/** Mark done or In an hour, from a reminder's notification (the person did it: it's theirs on the timeline). */
class ReminderActionReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val id = intent.getStringExtra(ReminderAlarm.EXTRA_ITEM)?.let { runCatching { UUID.fromString(it) }.getOrNull() } ?: return
        val app = context.applicationContext
        val pending = goAsync()
        app.getSystemService(NotificationManager::class.java)?.cancel(intent.getIntExtra(ReminderAlarm.EXTRA_NOTIFICATION, 0))
        val connection = PhoneConnection(app)
        connection.load()?.let { pairing ->
            val remote = when (intent.action) {
                ReminderAlarm.ACTION_DONE -> RemoteAction.Complete(id)
                ReminderAlarm.ACTION_LATER -> RemoteAction.Schedule(id, Instant.now().plus(1, ChronoUnit.HOURS))
                else -> return pending.finish()
            }
            RemoteAlarm.perform(app, pairing, remote) { error ->
                if (error != null) ReminderAlarm.couldNotReach(app, id, intent.getIntExtra(ReminderAlarm.EXTRA_NOTIFICATION, 0), error)
                pending.finish()
            }
            return
        }
        val action: (TaskBoard, Instant) -> Unit = when (intent.action) {
            ReminderAlarm.ACTION_DONE -> { board, now ->
                // An older notification, from before it had subtasks: finishing them all is asked in the app.
                val item = board.get(id)
                if (item.hasOpenChildren) throw BoardException("“${item.title}” has open subtasks: open it to finish them.")
                board.complete(id, Actor.USER, now)
            }
            ReminderAlarm.ACTION_LATER -> { board, now -> board.scheduleNextAction(id, now.plus(1, ChronoUnit.HOURS), notify = true, actor = Actor.USER, now = now) }
            else -> return pending.finish()
        }
        BoardHub.change(app, action) { result ->
            result.exceptionOrNull()?.let { Toast.makeText(app, it.message ?: "Couldn't change the task.", Toast.LENGTH_LONG).show() }
            pending.finish()
        }
    }
}

/**
 * Reminders and their buttons while connected to a computer: through the running app when it's open, else straight
 * to the computer (its board read again, or the last one kept when it can't be reached).
 */
object RemoteAlarm {
    /** A reminder is due: the board is read again (briefly), then what came due is told and the next alarm set. */
    fun due(context: Context, connection: PhoneConnection, done: () -> Unit) {
        BoardHub.live?.let { live ->
            Handler(Looper.getMainLooper()).post {
                live.remoteChanged()
                done()
            }
            return
        }
        Thread {
            try {
                readAndDeliver(context, connection)
            } finally {
                done()
            }
        }.start()
    }

    /**
     * Off the screen's thread: reads the computer's board (briefly; the kept one when it can't be reached), tells what
     * came due, and sets the alarm for the next reminder. False when it couldn't reach the computer.
     */
    fun readAndDeliver(context: Context, connection: PhoneConnection): Boolean {
        val pairing = connection.load() ?: return true
        val now = Instant.now()
        var reached = true
        val text = try {
            val client = connection.client(pairing, connectMillis = 3_000, readMillis = 5_000)
            val export = client.export(connection.cached()?.etag)
            connection.answered(client)
            if (export == null) {
                connection.stillCurrent(now)
                connection.cached()?.text
            } else {
                connection.cache(export.text, export.etag, now)
                export.text
            }
        } catch (e: PhoneException) {
            reached = false
            connection.cached()?.text
        }
        val board = text?.let { runCatching { BoardCodec.decode(it) }.getOrNull() } ?: return reached
        ReminderAlarm.schedule(context, ReminderAlarm.deliverRemote(context, connection, board, now))
        return reached
    }

    /** Mark done / In an hour, sent to the computer; [done] gets why it didn't happen (null: it did). */
    fun perform(context: Context, pairing: PhoneConnection.Pairing, action: RemoteAction, done: (String?) -> Unit) {
        val connection = PhoneConnection(context)
        Thread {
            val error = try {
                val client = connection.client(pairing, connectMillis = 3_000, readMillis = 8_000)
                client.perform(action)
                connection.answered(client)
                null
            } catch (e: PhoneException.Unreachable) {
                "Couldn't reach ${pairing.computer}. Open the app to do it when you're back on its Wi-Fi or Tailscale."
            } catch (e: PhoneException) {
                e.message ?: "${pairing.computer} couldn't do that."
            }
            Handler(Looper.getMainLooper()).post {
                BoardHub.live?.remoteChanged()
                done(error)
            }
        }.start()
    }
}
