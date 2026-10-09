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

/**
 * Says when a focus session or a break ends while the app is closed or the phone is locked: an alarm at the end of the
 * phase posts a notification (tapping it opens the app, where the break or "Break's over" is waiting). The alarm needs
 * no special permission; the phone may deliver it a little late when it's deeply asleep.
 */
object FocusAlarm {
    private const val CHANNEL = "focus-timer"
    private const val NOTIFICATION_ID = 7417
    private const val EXTRA_FOCUS = "focus"

    fun schedule(context: Context, at: Instant?, focusEnds: Boolean) {
        val alarms = context.getSystemService(AlarmManager::class.java) ?: return
        val pending = PendingIntent.getBroadcast(
            context, 0,
            Intent(context, FocusAlarmReceiver::class.java).putExtra(EXTRA_FOCUS, focusEnds),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        alarms.cancel(pending)
        if (at != null && at.isAfter(Instant.now())) {
            alarms.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, at.toEpochMilli(), pending)
        }
    }

    internal fun notify(context: Context, focusEnded: Boolean) {
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
            .setContentTitle(if (focusEnded) "🍅 Focus session done" else "Break is over")
            .setContentText(if (focusEnded) "Nice work. Stand up, stretch, drink some water." else "Ready for the next focus block? Open to start it.")
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_ALARM)
            .setAutoCancel(true)
            .setContentIntent(open)
            .build()
        manager.notify(NOTIFICATION_ID, notification)
    }

    internal fun isFocus(intent: Intent) = intent.getBooleanExtra(EXTRA_FOCUS, false)
}

class FocusAlarmReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) = FocusAlarm.notify(context, FocusAlarm.isFocus(intent))
}
