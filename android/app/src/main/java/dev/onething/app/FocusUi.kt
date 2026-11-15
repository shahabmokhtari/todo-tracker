package dev.onething.app

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.height
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.VerticalDivider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
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
import dev.onething.core.Breaks
import dev.onething.core.PomodoroPhase
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

/** What the focus timer is doing, in words ("Focus · Write the plan (paused)"). */
private fun focusLabel(f: FocusState): String = when (f.phase) {
    PomodoroPhase.IDLE -> "Focus"
    PomodoroPhase.FOCUS -> "Focus"
    PomodoroPhase.SHORT_BREAK -> "Break"
    PomodoroPhase.LONG_BREAK -> "Long break"
} + (f.itemTitle?.takeIf { f.phase != PomodoroPhase.IDLE }?.let { " · $it" } ?: "") + (if (f.phase != PomodoroPhase.IDLE && !f.running) " (paused)" else "")

/** Whether the strip has something to show beyond Today: a focus session, a break, or a task being timed. */
fun timersBusy(vm: BoardViewModel): Boolean = vm.focus.phase != PomodoroPhase.IDLE || vm.timer != null

/**
 * The timers in one compact strip: the focus timer (its ring, clock and what it's on; Start, Pause or Resume, and Skip
 * and Reset under ⋯) and, while a task is timed, its clock, its name and Stop. One timer runs at a time.
 */
@Composable
fun TimerStrip(vm: BoardViewModel, onStartFocus: () -> Unit, modifier: Modifier = Modifier) {
    val f = vm.focus
    val total = vm.focusTotal()
    val fraction = if (f.phase == PomodoroPhase.IDLE || total.isZero) 0f else (1f - f.remaining.toMillis().toFloat() / total.toMillis()).coerceIn(0f, 1f)
    val ring = when (f.phase) {
        PomodoroPhase.FOCUS -> Brand.pink
        PomodoroPhase.IDLE -> MaterialTheme.colorScheme.primary
        else -> Color(0xFF10B981)
    }
    val running = vm.timer
    Surface(shape = RoundedCornerShape(20.dp), color = MaterialTheme.colorScheme.surfaceContainerHigh, modifier = modifier.fillMaxWidth()) {
        Row(Modifier.height(52.dp).padding(start = 12.dp, end = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            // The focus timer.
            Box(contentAlignment = Alignment.Center) {
                CircularProgressIndicator(progress = { fraction }, modifier = Modifier.size(26.dp), color = ring, strokeWidth = 3.dp, trackColor = MaterialTheme.colorScheme.outlineVariant)
            }
            Column(Modifier.weight(1f).padding(start = 10.dp)) {
                Text(clockText(f.remaining), style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold, maxLines = 1)
                Text(focusLabel(f), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            if (f.phase == PomodoroPhase.IDLE) {
                TextButton(onClick = onStartFocus) { Text("Start") }
            } else {
                if (f.running) {
                    IconButton(onClick = { vm.pauseFocus() }) { Icon(ViewIcons.pause, contentDescription = "Pause") }
                } else {
                    IconButton(onClick = { vm.resumeFocus() }) { Icon(Icons.Filled.PlayArrow, contentDescription = "Resume") }
                }
                Box {
                    var menu by remember { mutableStateOf(false) }
                    IconButton(onClick = { menu = true }) { Icon(Icons.Filled.MoreVert, contentDescription = "More for the focus timer") }
                    DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                        DropdownMenuItem(text = { Text(if (f.phase == PomodoroPhase.FOCUS) "Skip to the break" else "Skip the break") }, onClick = { menu = false; vm.skipFocus() })
                        DropdownMenuItem(text = { Text("Reset") }, onClick = { menu = false; vm.resetFocus() })
                    }
                }
            }
            // The task being timed.
            if (running != null) {
                val elapsed = running.entry.duration(vm.now)
                VerticalDivider(Modifier.height(28.dp).padding(horizontal = 4.dp))
                Box(Modifier.padding(start = 8.dp).size(8.dp).background(ViewColors.danger, CircleShape))
                Column(
                    Modifier.weight(1f).padding(start = 8.dp).clickable { vm.openTask(running.item.id) }
                        .semantics(mergeDescendants = true) { contentDescription = "Timing ${running.item.title} for ${durationText(elapsed)}" },
                ) {
                    Text(clockText(elapsed), style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold, maxLines = 1)
                    Text(
                        running.item.title + if (running.entry.source == dev.onething.core.TimeSource.FOCUS) " · focus" else "",
                        style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis,
                    )
                }
                IconButton(onClick = { vm.stopTimer() }) { Icon(ViewIcons.stop, contentDescription = "Stop the timer", tint = ViewColors.danger) }
            }
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
