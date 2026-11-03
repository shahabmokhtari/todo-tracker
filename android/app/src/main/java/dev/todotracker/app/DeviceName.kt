package dev.todotracker.app

import android.content.Context
import android.os.Build
import android.provider.Settings

/** The name your other devices list this phone by when it syncs ("Pixel 8"). */
object DeviceName {
    /** The name set in the phone's settings, else its make and model. */
    fun of(context: Context): String =
        runCatching { Settings.Global.getString(context.contentResolver, Settings.Global.DEVICE_NAME) }.getOrNull()?.trim()?.ifEmpty { null }
            ?: listOf(Build.MANUFACTURER, Build.MODEL).filterNotNull().joinToString(" ").trim().ifEmpty { "Android phone" }
}
