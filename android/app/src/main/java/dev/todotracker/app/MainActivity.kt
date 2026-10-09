package dev.todotracker.app

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
        vm.onFocusEnd = { at, focus -> FocusAlarm.schedule(applicationContext, at, focus) }
        setContent {
            TodoTrackerTheme {
                TodayScreen(vm)
            }
        }
    }

    // Back to the app: what came back meanwhile (this morning's "tomorrow" tasks) shows at once.
    override fun onResume() {
        super.onResume()
        vm.refresh()
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
