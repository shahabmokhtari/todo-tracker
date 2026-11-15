package dev.onething.app

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
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.repeatOnLifecycle
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {
    private val vm: BoardViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        // When a focus session or break ends with the app closed, the phone says so.
        // (Not for the demo board's made-up timer.)
        vm.onPhaseEnds = { ends -> if (!vm.demo) FocusAlarm.schedule(applicationContext, ends) }
        setContent {
            // The status bar's icons follow the chosen theme (dark icons on the light one).
            val dark = vm.theme == "dark" || (vm.theme == "system" && isSystemInDarkTheme())
            androidx.compose.runtime.LaunchedEffect(dark) {
                val clear = android.graphics.Color.TRANSPARENT
                val style = if (dark) androidx.activity.SystemBarStyle.dark(clear) else androidx.activity.SystemBarStyle.light(clear, clear)
                enableEdgeToEdge(statusBarStyle = style, navigationBarStyle = style)
            }
            OneThingTheme(vm.theme) {
                val moving = vm.moving
                if (moving != null) MovingTasks(moving) else AppShell(vm)
            }
        }
        // Started by a onething://task/<id> link (not again after rotation, nor when reopened from Recents, which
        // hands back the old link).
        val fromHistory = (intent?.flags ?: 0) and Intent.FLAG_ACTIVITY_LAUNCHED_FROM_HISTORY != 0
        if (savedInstanceState == null && !fromHistory) vm.openLink(intent?.dataString)
        // The clocks, the lists and sync move on only while the app is on screen (WorkManager syncs when it isn't).
        lifecycleScope.launch { repeatOnLifecycle(Lifecycle.State.STARTED) { vm.whileOnScreen() } }
    }

    // A link clicked while the app runs (singleTask: links always come to this one board, never a second copy).
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        // Opened again without asking for the demo board (the launcher, a link): back to your own tasks.
        vm.useIntent(if (intent.hasExtra(DemoBoard.EXTRA)) intent.getBooleanExtra(DemoBoard.EXTRA, false) else null)
        vm.openLink(intent.dataString)
    }

    // A keyboard (a tablet's, a Chromebook's): Ctrl+N adds a task, Ctrl+F searches. Not while you type in a box: the
    // keys are the box's then, and its focus stays where it is.
    override fun dispatchKeyEvent(event: android.view.KeyEvent): Boolean {
        if (event.action == android.view.KeyEvent.ACTION_DOWN && event.isCtrlPressed && event.repeatCount == 0 && !typing()) {
            when (event.keyCode) {
                android.view.KeyEvent.KEYCODE_N -> { vm.addRequested = true; return true }
                android.view.KeyEvent.KEYCODE_F -> { vm.requestSearch(); return true }
            }
        }
        return super.dispatchKeyEvent(event)
    }

    /** Whether a text box has the keyboard's input now (the input method is serving one). */
    private fun typing(): Boolean {
        val imm = getSystemService(android.view.inputmethod.InputMethodManager::class.java) ?: return false
        val focused = window?.decorView?.findFocus() ?: return false
        return imm.isActive(focused) && imm.isAcceptingText
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

/** The brand: the icon's indigo → violet → pink (the same in every app). */
object Brand {
    val indigo = Color(0xFF4F46E5)
    val violet = Color(0xFF7C3AED)
    val pink = Color(0xFFDB2777)

    /** Indigo that stays readable as text on a dark background. */
    val indigoOnDark = Color(0xFFA5B4FC)

    /** The icon's gradient, for the one thing to do now. */
    val gradient = androidx.compose.ui.graphics.Brush.linearGradient(listOf(indigo, violet, pink))
}

/** The brand colors, light or dark as chosen in Settings ("system": as the phone is). Calm surfaces; the accent is for what matters. */
@Composable
fun OneThingTheme(theme: String = "system", content: @Composable () -> Unit) {
    val dark = when (theme) {
        "light" -> false
        "dark" -> true
        else -> isSystemInDarkTheme()
    }
    val colors = if (dark) {
        darkColorScheme(
            primary = Brand.indigoOnDark, onPrimary = Color(0xFF1E1B4B),
            primaryContainer = Color(0xFF312E81), onPrimaryContainer = Color(0xFFE0E7FF),
            secondary = Color(0xFFC4B5FD), secondaryContainer = Color(0xFF3B2764), onSecondaryContainer = Color(0xFFEDE9FE),
            tertiary = Color(0xFFF9A8D4),
            background = Color(0xFF0F1020), surface = Color(0xFF0F1020),
            surfaceContainerLowest = Color(0xFF0B0C19), surfaceContainerLow = Color(0xFF15162A), surfaceContainer = Color(0xFF1A1B30),
            surfaceContainerHigh = Color(0xFF222339), surfaceContainerHighest = Color(0xFF2A2B43),
            surfaceVariant = Color(0xFF24253C), onSurfaceVariant = Color(0xFFB4B6CC), outlineVariant = Color(0xFF34364F),
        )
    } else {
        lightColorScheme(
            primary = Brand.indigo, onPrimary = Color.White,
            primaryContainer = Color(0xFFE0E7FF), onPrimaryContainer = Color(0xFF312E81),
            secondary = Brand.violet, secondaryContainer = Color(0xFFEDE9FE), onSecondaryContainer = Color(0xFF4C1D95),
            tertiary = Brand.pink,
            background = Color(0xFFF8F8FC), surface = Color(0xFFF8F8FC),
            surfaceContainerLowest = Color.White, surfaceContainerLow = Color(0xFFF3F3F9), surfaceContainer = Color(0xFFEFEFF6),
            surfaceContainerHigh = Color(0xFFE9E9F2), surfaceContainerHighest = Color(0xFFE3E3EE),
            surfaceVariant = Color(0xFFEDEDF5), onSurfaceVariant = Color(0xFF5B5D72), outlineVariant = Color(0xFFDADBE8),
        )
    }
    MaterialTheme(colorScheme = colors, content = content)
}
