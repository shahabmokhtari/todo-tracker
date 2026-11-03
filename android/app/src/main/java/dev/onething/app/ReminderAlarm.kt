package dev.onething.app

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
import dev.onething.R
import dev.onething.core.Actor
import dev.onething.core.BoardException
import dev.onething.core.LocalBoard
import dev.onething.core.DueReminder
import dev.onething.core.Reminders
import dev.onething.core.TaskBoard
import dev.onething.core.TaskLinks
import dev.onething.core.vault.VaultStore
import java.time.Instant
import java.time.temporal.ChronoUnit
import java.util.UUID
import kotlinx.coroutines.launch

/**
 * The one board on this phone, whoever changes it: its tasks folder ([store], one for the whole app: the screens, the
 * alarms, notification buttons and sync all go through it, one change at a time). While the app runs its view model
 * registers as [live] (it shows the board and is told when it changed underneath).
 */
object BoardHub {
    /**
     * Where every change to the tasks folder is written (the app's, and those made with it closed), one at a time and
     * in order, off the main thread. It outlives the screen, so a change on its way is never dropped.
     */
    @OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
    val disk = kotlinx.coroutines.CoroutineScope(kotlinx.coroutines.SupervisorJob() + kotlinx.coroutines.Dispatchers.IO.limitedParallelism(1))

    /** The running app's board: changes go through it (so the screen and the files agree). Read and set on the main thread. */
    @Volatile
    var live: LiveBoard? = null

    interface LiveBoard {
        /** Applies [action] (called on the main thread), saves it off the main thread, redraws, then tells [done] (on the main thread) whether the board took it. */
        fun applyOutside(action: (TaskBoard, Instant) -> Unit, done: (Result<Unit>) -> Unit)

        /** The tasks folder changed underneath (a sync with the app in the background): show it again. */
        fun reloadLocal()
    }

    private var opened: LocalBoard.Opened? = null

    /** The phone's tasks folder, opened once per process (the first time, board.json moves into it). */
    fun local(context: Context): LocalBoard.Opened = synchronized(this) {
        opened ?: LocalBoard.open(context.applicationContext.filesDir).also { opened = it }
    }

    /** The one store on the tasks folder; null when it can't be used (a board.json from a newer app: changes are off). */
    fun store(context: Context): VaultStore? = local(context).store

    /**
     * Applies [action] to the board: through the running app, or (the app closed meanwhile, or not running) on the
     * tasks folder, then the next reminder set. The result says whether the board took it.
     */
    fun change(context: Context, action: (TaskBoard, Instant) -> Unit, done: (Result<Unit>) -> Unit) {
        val main = Handler(Looper.getMainLooper())
        main.post {
            // Decided on the main thread, where the app's board comes and goes: no change is lost in between.
            val app = live
            if (app != null) {
                app.applyOutside(action, done)
                return@post
            }
            disk.launch {
                val result = runCatching {
                    // A board this app can't read (a newer version) is never written over from here.
                    val store = store(context) ?: throw BoardException("Open the app to check your board.")
                    val now = Instant.now()
                    val canNotify = ReminderAlarm.canNotify(context)
                    // The change may be made again (a file changed underneath): what's told is what the saved one marked.
                    var told = emptyList<ReminderAlarm.Notice>()
                    store.update { board ->
                        action(board, now)
                        told = ReminderAlarm.mark(board, now, canNotify)
                    }
                    ReminderAlarm.post(context, told)
                    ReminderAlarm.schedule(context, store.read { Reminders.nextAt(it, now) })
                }
                if (result.isSuccess) {
                    SyncHub.soonInBackground(context)
                    Widgets.update(context)
                }
                main.post { done(result) }
            }
        }
    }

    /** Sync changed the tasks folder (from a worker): shown again in the app, or its reminders told and set. */
    fun reloaded(context: Context) {
        val main = Handler(Looper.getMainLooper())
        main.post {
            val app = live
            if (app != null) {
                app.reloadLocal()
                return@post
            }
            disk.launch {
                runCatching {
                    val store = store(context) ?: return@runCatching
                    val now = Instant.now()
                    if (store.read { Reminders.due(it, now).isNotEmpty() }) {
                        val canNotify = ReminderAlarm.canNotify(context)
                        var told = emptyList<ReminderAlarm.Notice>()
                        store.update { told = ReminderAlarm.mark(it, now, canNotify) }
                        ReminderAlarm.post(context, told)
                    }
                    ReminderAlarm.schedule(context, store.read { Reminders.nextAt(it, now) })
                }
                Widgets.update(context)
            }
        }
    }

    /**
     * The phone's time zone changed (a trip): the tasks folder reads its local times in the new one, and the app and the
     * reminders follow.
     */
    fun zoneChanged(context: Context) {
        disk.launch {
            runCatching { store(context)?.changeZone(java.time.ZoneId.systemDefault()) }
            reloaded(context)
        }
    }
}

/** The phone's time zone changed: the tasks' local times follow (see [BoardHub.zoneChanged]). */
class TimeZoneReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action == Intent.ACTION_TIMEZONE_CHANGED) BoardHub.zoneChanged(context.applicationContext)
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
    internal const val ACTION_DONE = "dev.onething.REMINDER_DONE"
    internal const val ACTION_LATER = "dev.onething.REMINDER_LATER"

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

    /** A reminder to tell, as the notification says it (read from the board when it was marked). */
    data class Notice(val itemId: UUID, val title: String, val message: String, val where: String, val hasOpenChildren: Boolean)

    /** Whether the phone shows reminders now (notifications on for the app and its reminders). */
    fun canNotify(context: Context): Boolean {
        if (Build.VERSION.SDK_INT >= 33 && ContextCompat.checkSelfPermission(context, android.Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return false
        val manager = context.getSystemService(NotificationManager::class.java) ?: return false
        manager.createNotificationChannel(NotificationChannel(CHANNEL, "Reminders", NotificationManager.IMPORTANCE_HIGH).apply {
            description = "When a task's reminder is due, or a snooze ends"
        })
        return manager.areNotificationsEnabled() && manager.getNotificationChannel(CHANNEL)?.importance != NotificationManager.IMPORTANCE_NONE
    }

    /**
     * Marks what's due on [board] now as delivered, when the phone can show it ([canNotify]): with notifications off, a
     * reminder stays pending (Today shows it) and is told once they're on. Only marks (a change that may be made again):
     * what it returns is posted with [post] once the change is saved.
     */
    fun mark(board: TaskBoard, now: Instant, canNotify: Boolean): List<Notice> {
        val told = mutableListOf<Notice>()
        Reminders.deliver(board, now) { due ->
            if (canNotify) {
                told.add(Notice(due.item.id, due.item.title, due.reminder.message, due.item.ancestors.reversed().joinToString(" › ") { it.title }, due.item.hasOpenChildren))
            }
            canNotify
        }
        return told
    }

    /** Shows the reminders [mark] marked (one notification per task). */
    fun post(context: Context, notices: List<Notice>) {
        if (notices.isEmpty()) return
        val manager = context.getSystemService(NotificationManager::class.java) ?: return
        for (due in notices) {
            val id = notificationId(due.itemId)
            val open = PendingIntent.getActivity(
                context, id,
                Intent(Intent.ACTION_VIEW, Uri.parse(TaskLinks.forTask(due.itemId)), context, MainActivity::class.java),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
            )
            val builder = NotificationCompat.Builder(context, CHANNEL)
                .setSmallIcon(R.drawable.ic_notification)
                .setContentTitle(due.title)
                .setContentText(due.message)
                .setSubText(due.where.ifEmpty { null })
                .setPriority(NotificationCompat.PRIORITY_HIGH)
                .setCategory(NotificationCompat.CATEGORY_REMINDER)
                .setAutoCancel(true)
                .setContentIntent(open)
            // Mark done would also close its open subtasks without asking: then it's Open (the app asks there).
            if (due.hasOpenChildren) builder.addAction(0, "Open", open) else builder.addAction(0, "Mark done", action(context, ACTION_DONE, due.itemId, id))
            builder.addAction(0, "In an hour", action(context, ACTION_LATER, due.itemId, id))
            // Permission can be taken back in between: nothing to do then (it was marked; Today still shows the task).
            runCatching { manager.notify(id, builder.build()) }
        }
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
