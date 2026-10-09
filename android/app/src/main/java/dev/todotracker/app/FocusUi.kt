package dev.todotracker.app

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import dev.todotracker.core.Breaks
import dev.todotracker.core.PomodoroPhase
import java.time.Duration

/** "4:07", "1:02:09": a running clock (same as the other apps). */
fun clockText(d: Duration): String {
    val s = d.seconds.coerceAtLeast(0)
    val (h, m, sec) = Triple(s / 3600, (s % 3600) / 60, s % 60)
    return if (h > 0) "$h:${m.toString().padStart(2, '0')}:${sec.toString().padStart(2, '0')}" else "$m:${sec.toString().padStart(2, '0')}"
}

/** "45 s", "25 min", "1 h 05 min", "12 h": time spent (same as the other apps). */
fun durationText(d: Duration): String {
    val s = d.seconds.coerceAtLeast(0)
    if (s < 60) return "$s s"
    val minutes = Math.round(s / 60.0)
    if (minutes < 60) return "$minutes min"
    val (h, m) = minutes / 60 to minutes % 60
    return if (m == 0L) "$h h" else "$h h ${m.toString().padStart(2, '0')} min"
}

/** The focus timer: its ring and clock, what it's on, and start / pause / resume / skip / reset. */
@Composable
fun FocusBar(vm: BoardViewModel, onStart: () -> Unit) {
    val f = vm.focus
    val total = vm.focusTotal()
    val fraction = if (f.phase == PomodoroPhase.IDLE || total.isZero) 0f else (1f - f.remaining.toMillis().toFloat() / total.toMillis()).coerceIn(0f, 1f)
    val ring = when (f.phase) {
        PomodoroPhase.FOCUS -> Color(0xFFF43F5E)
        PomodoroPhase.IDLE -> MaterialTheme.colorScheme.primary
        else -> Color(0xFF10B981)
    }
    val label = when (f.phase) {
        PomodoroPhase.IDLE -> "Focus timer"
        PomodoroPhase.FOCUS -> "Focus"
        PomodoroPhase.SHORT_BREAK -> "Break"
        PomodoroPhase.LONG_BREAK -> "Long break"
    } + (f.itemTitle?.takeIf { f.phase != PomodoroPhase.IDLE }?.let { " · $it" } ?: "") + (if (f.phase != PomodoroPhase.IDLE && !f.running) " (paused)" else "")
    // Above the gesture bar (the app draws edge to edge).
    Surface(shape = RoundedCornerShape(18.dp), color = MaterialTheme.colorScheme.surfaceVariant, modifier = Modifier.navigationBarsPadding().fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp)) {
        Row(Modifier.padding(horizontal = 12.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            Box(contentAlignment = Alignment.Center) {
                CircularProgressIndicator(progress = { fraction }, modifier = Modifier.size(34.dp), color = ring, strokeWidth = 3.dp, trackColor = MaterialTheme.colorScheme.outlineVariant)
            }
            Column(Modifier.weight(1f)) {
                Text(clockText(f.remaining), style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                Text(label, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            if (f.phase == PomodoroPhase.IDLE) {
                TextButton(onClick = onStart) { Text("Start") }
            } else {
                if (f.running) TextButton(onClick = { vm.pauseFocus() }) { Text("Pause") } else TextButton(onClick = { vm.resumeFocus() }) { Text("Resume") }
                TextButton(onClick = { vm.skipFocus() }) { Text("Skip") }
                TextButton(onClick = { vm.resetFocus() }) { Text("Reset") }
            }
        }
    }
}

/** The task being timed: its clock, its name and Stop. One timer runs at a time. */
@Composable
fun TimerBar(vm: BoardViewModel) {
    val running = vm.timer ?: return
    val elapsed = running.entry.duration(vm.now)
    Surface(shape = RoundedCornerShape(16.dp), color = MaterialTheme.colorScheme.surfaceVariant, modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp)) {
        Row(Modifier.padding(horizontal = 12.dp, vertical = 6.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            Box(Modifier.size(8.dp).background(Color(0xFFEF4444), CircleShape))
            Text(
                clockText(elapsed),
                style = MaterialTheme.typography.titleSmall,
                fontWeight = FontWeight.SemiBold,
                modifier = Modifier.semantics { contentDescription = "Timing for ${durationText(elapsed)}" },
            )
            Text(running.item.title, style = MaterialTheme.typography.bodyMedium, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
            if (running.entry.source == dev.todotracker.core.TimeSource.FOCUS) Text("focus", style = MaterialTheme.typography.labelSmall, color = Color(0xFFB45309))
            TextButton(onClick = { vm.stopTimer() }) { Text("Stop") }
        }
    }
}

/**
 * The full-screen break after a focus session: what to do, how long it lasts, "I'm taking it", "Start next focus now"
 * and "Skip the break". When it runs out it turns into "Break's over": "Start next focus" or "Not now". Taps in the
 * first moment after it appears (or changes) are ignored, so a tap meant for what was under it can't answer it.
 */
@Composable
fun BreakScreen(vm: BoardViewModel) {
    val prompt = vm.breakPrompt ?: return
    val shownAt = remember(prompt.isOver, prompt.until) { mutableLongStateOf(System.currentTimeMillis()) }
    fun settled() = System.currentTimeMillis() - shownAt.longValue > Breaks.settleTime.toMillis()
    Dialog(onDismissRequest = { if (settled()) vm.takeBreak() }, properties = DialogProperties(usePlatformDefaultWidth = false, dismissOnClickOutside = false)) {
        Box(Modifier.fillMaxSize().background(Color(0xF00A0D1A)), contentAlignment = Alignment.Center) {
            Column(Modifier.widthIn(max = 560.dp).padding(28.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(14.dp)) {
                Text(if (prompt.isOver) "🎯" else "☕", fontSize = 48.sp)
                Text(prompt.title, color = Color.White, fontSize = 30.sp, fontWeight = FontWeight.Bold, textAlign = TextAlign.Center)
                Text(prompt.tip, color = Color(0xFFD1FAE5), style = MaterialTheme.typography.titleMedium, textAlign = TextAlign.Center)
                if (!prompt.isOver) {
                    Text(Breaks.clock(prompt.until, vm.now), color = Color.White, fontSize = 64.sp, fontWeight = FontWeight.SemiBold)
                    Text("Your focus session is done. Step away; the timer tells you when to come back.", color = Color(0xBFF8FAFC), textAlign = TextAlign.Center)
                }
                vm.nextFocusTitle?.let { Text("Next: $it", color = Color.White, fontWeight = FontWeight.SemiBold, textAlign = TextAlign.Center) }
                Spacer(Modifier.size(4.dp))
                val light = ButtonDefaults.outlinedButtonColors(contentColor = Color.White)
                if (prompt.isOver) {
                    Button(onClick = { if (settled()) vm.startNextFocus() }, modifier = Modifier.fillMaxWidth()) { Text("▶ Start next focus") }
                    OutlinedButton(onClick = { if (settled()) vm.takeBreak() }, colors = light, modifier = Modifier.fillMaxWidth()) { Text("Not now") }
                } else {
                    Button(onClick = { if (settled()) vm.takeBreak() }, modifier = Modifier.fillMaxWidth()) { Text("I’m taking it") }
                    OutlinedButton(onClick = { if (settled()) vm.startNextFocus() }, colors = light, modifier = Modifier.fillMaxWidth()) { Text("▶ Start next focus now") }
                    TextButton(onClick = { if (settled()) vm.skipBreak() }) { Text("Skip the break", color = Color(0xBFF8FAFC)) }
                }
            }
        }
    }
}
