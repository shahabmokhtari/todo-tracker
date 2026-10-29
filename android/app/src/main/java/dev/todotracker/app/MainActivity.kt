package dev.todotracker.app

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.viewModels
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.ui.unit.dp

class MainActivity : ComponentActivity() {
    private val vm: BoardViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        // When a focus session or break ends with the app closed, the phone says so.
        vm.onPhaseEnds = { ends -> FocusAlarm.schedule(applicationContext, ends) }
        vm.onPhaseEnded = { end -> FocusAlarm.notify(applicationContext, end) }
        setContent {
            // The status bar's icons follow the chosen theme (dark icons on the light one).
            val dark = vm.theme == "dark" || (vm.theme == "system" && isSystemInDarkTheme())
            androidx.compose.runtime.LaunchedEffect(dark) {
                val clear = android.graphics.Color.TRANSPARENT
                val style = if (dark) androidx.activity.SystemBarStyle.dark(clear) else androidx.activity.SystemBarStyle.light(clear, clear)
                enableEdgeToEdge(statusBarStyle = style, navigationBarStyle = style)
            }
            TodoTrackerTheme(vm.theme) {
                val moving = vm.moving
                if (moving != null) MovingTasks(moving) else TodayScreen(vm)
            }
        }
        // Started by a todotracker://task/<id> link (not again after rotation, nor when reopened from Recents, which
        // hands back the old link).
        val fromHistory = (intent?.flags ?: 0) and Intent.FLAG_ACTIVITY_LAUNCHED_FROM_HISTORY != 0
        if (savedInstanceState == null && !fromHistory) vm.openLink(intent?.dataString)
    }

    // A link clicked while the app runs (singleTask: links always come to this one board, never a second copy).
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        vm.openLink(intent.dataString)
    }

    // Back to the app: what came back meanwhile (this morning's "tomorrow" tasks) shows at once.
    override fun onResume() {
        super.onResume()
        vm.resume()
    }

    override fun onStart() {
        super.onStart()
        vm.appVisible = true
    }

    override fun onStop() {
        super.onStop()
        vm.appVisible = false
    }
}

/** The first start of this version, while the old board file moves into the tasks folder (a moment). */
@Composable
private fun MovingTasks(text: String) {
    androidx.compose.material3.Surface(androidx.compose.ui.Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        androidx.compose.foundation.layout.Box(contentAlignment = androidx.compose.ui.Alignment.Center) {
            androidx.compose.material3.Text(text, style = MaterialTheme.typography.bodyLarge, modifier = androidx.compose.ui.Modifier.padding(24.dp))
        }
    }
}

/** The brand colors, light or dark as chosen in Settings ("system": as the phone is). */
@Composable
fun TodoTrackerTheme(theme: String = "system", content: @Composable () -> Unit) {
    val dark = when (theme) {
        "light" -> false
        "dark" -> true
        else -> isSystemInDarkTheme()
    }
    val colors = if (dark) {
        darkColorScheme(primary = Color(0xFF8B8DF8), secondary = Color(0xFFF472B6), tertiary = Color(0xFF34D399))
    } else {
        lightColorScheme(primary = Color(0xFF6366F1), secondary = Color(0xFFEC4899), tertiary = Color(0xFF10B981))
    }
    MaterialTheme(colorScheme = colors, content = content)
}
