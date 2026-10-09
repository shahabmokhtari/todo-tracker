package dev.todotracker.app

import android.app.AlarmManager
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import dev.todotracker.R
import java.time.Instant

/** When the focus timer's phases end: a focus session (and the break after it), or a break. */
data class PhaseEnd(val at: Instant, val focus: Boolean)

/**
 * Says when a focus session or a break ends while the app is closed or the phone is locked: alarms at the ends of the
 * phases post a notification (tapping it opens the app, where the break or "Break's over" is waiting). Exact when the
 * phone allows exact alarms; otherwise the phone may deliver them a little late when it's deeply asleep. Each end is
 * announced once, whoever notices it first (the alarm, or the app itself running in the background).
 */
object FocusAlarm {
    private const val CHANNEL = "focus-timer"
    private const val NOTIFICATION_ID = 7417
    private const val EXTRA_FOCUS = "focus"
    private const val EXTRA_AT = "at"
    private const val PREFS = "focus-alarm"
    private const val LAST = "last-announced"

    /** Replaces the alarms with [ends] (a focus session's end and its break's, or a break's). */
    fun schedule(context: Context, ends: List<PhaseEnd>) {
        val alarms = context.getSystemService(AlarmManager::class.java) ?: return
        for (code in 0..1) alarms.cancel(intent(context, code, null))
        ends.filter { it.at.isAfter(Instant.now()) }.take(2).forEachIndexed { code, end ->
            val pending = intent(context, code, end)
            val exact = Build.VERSION.SDK_INT < 31 || alarms.canScheduleExactAlarms()
            if (exact) {
                alarms.setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, end.at.toEpochMilli(), pending)
            } else {
                alarms.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, end.at.toEpochMilli(), pending)
            }
        }
    }

    private fun intent(context: Context, code: Int, end: PhaseEnd?): PendingIntent {
        val intent = Intent(context, FocusAlarmReceiver::class.java)
        if (end != null) intent.putExtra(EXTRA_FOCUS, end.focus).putExtra(EXTRA_AT, end.at.toEpochMilli())
        return PendingIntent.getBroadcast(context, code, intent, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
    }

    /** Posts "Focus session done" or "Break is over" for the phase that ended [at] (once per end). */
    fun notify(context: Context, end: PhaseEnd) {
        val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        if (prefs.getLong(LAST, 0L) == end.at.toEpochMilli()) return
        prefs.edit().putLong(LAST, end.at.toEpochMilli()).apply()
        if (Build.VERSION.SDK_INT >= 33 && ContextCompat.checkSelfPermission(context, android.Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return
        val manager = context.getSystemService(NotificationManager::class.java) ?: return
        manager.createNotificationChannel(NotificationChannel(CHANNEL, "Focus timer", NotificationManager.IMPORTANCE_HIGH).apply {
            description = "When a focus session or a break ends"
        })
        val open = PendingIntent.getActivity(
            context, 0,
            Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val notification = NotificationCompat.Builder(context, CHANNEL)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(if (end.focus) "🍅 Focus session done" else "Break is over")
            .setContentText(if (end.focus) "Nice work. Stand up, stretch, drink some water." else "Ready for the next focus block? Open to start it.")
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_ALARM)
            .setOnlyAlertOnce(true)
            .setAutoCancel(true)
            .setContentIntent(open)
            .build()
        manager.notify(NOTIFICATION_ID, notification)
    }

    internal fun endOf(intent: Intent): PhaseEnd? {
        val at = intent.getLongExtra(EXTRA_AT, 0L).takeIf { it > 0 } ?: return null
        return PhaseEnd(Instant.ofEpochMilli(at), intent.getBooleanExtra(EXTRA_FOCUS, false))
    }
}

class FocusAlarmReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        FocusAlarm.endOf(intent)?.let { FocusAlarm.notify(context, it) }
    }
}
