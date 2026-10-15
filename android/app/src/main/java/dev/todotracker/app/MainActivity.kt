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

class MainActivity : ComponentActivity() {
    private val vm: BoardViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        // When a focus session or break ends with the app closed, the phone says so.
        vm.onPhaseEnds = { ends -> FocusAlarm.schedule(applicationContext, ends) }
        vm.onPhaseEnded = { end -> FocusAlarm.notify(applicationContext, end) }
        setContent {
            TodoTrackerTheme {
                TodayScreen(vm)
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

/** The brand colors, light or dark as the phone is. */
@Composable
fun TodoTrackerTheme(content: @Composable () -> Unit) {
    val colors = if (isSystemInDarkTheme()) {
        darkColorScheme(primary = Color(0xFF8B8DF8), secondary = Color(0xFFF472B6), tertiary = Color(0xFF34D399))
    } else {
        lightColorScheme(primary = Color(0xFF6366F1), secondary = Color(0xFFEC4899), tertiary = Color(0xFF10B981))
    }
    MaterialTheme(colorScheme = colors, content = content)
}
