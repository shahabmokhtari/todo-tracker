package dev.todotracker.app

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import java.util.UUID

/** Settings on this phone: sync or a connected computer, the theme, energy in a day, the focus timer's lengths, and what may notify you. */
@OptIn(ExperimentalLayoutApi::class)
@Composable
fun SettingsScreen(vm: BoardViewModel, onClose: () -> Unit) {
    var syncOpen by rememberSaveable { mutableStateOf(false) }
    if (syncOpen) {
        SyncScreen(vm) { syncOpen = false }
        return
    }
    BackHandler(onBack = onClose)
    val context = LocalContext.current
    run {
        Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
            Column(Modifier.safeDrawingPadding()) {
                Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                    TextButton(onClick = onClose) { Text("Close") }
                    Text("Settings", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold, modifier = Modifier.padding(start = 8.dp))
                }
                vm.status?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(horizontal = 16.dp)) }
                Column(Modifier.verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
                    SyncSection(vm) { syncOpen = true }
                    HorizontalDivider()
                    ConnectToComputer(vm)
                    HorizontalDivider()
                    Setting("Theme", "Light, dark, or as your phone is.") {
                        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            listOf("system" to "System", "light" to "Light", "dark" to "Dark").forEach { (value, label) ->
                                FilterChip(selected = vm.theme == value, onClick = { vm.chooseTheme(value) }, label = { Text(label) })
                            }
                        }
                    }
                    HorizontalDivider()
                    Setting("Energy in a day", "How much you have in a day, in ⚡. Each task takes ⚡ to ⚡⚡⚡ an hour (set it in its details; not said counts as ⚡). Every app uses this number.") {
                        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            listOf(4, 6, 8, 10, 12, 14, 16).forEach { n -> FilterChip(selected = vm.dailyEnergy == n, onClick = { vm.setDailyEnergy(n) }, label = { Text("$n ⚡") }) }
                        }
                    }
                    HorizontalDivider()
                    if (vm.canSetFocusLengths) {
                        FocusLengths(vm)
                        HorizontalDivider()
                    }
                    Setting("Notifications", "Reminders, snoozes ending, and the end of a focus session or a break are told with a notification, even with the app closed.") {
                        val notifyOff = android.os.Build.VERSION.SDK_INT >= 33 &&
                            androidx.core.content.ContextCompat.checkSelfPermission(context, android.Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED
                        Text(if (notifyOff) "Notifications are off for Todo Tracker." else "Notifications are on.", style = MaterialTheme.typography.bodyMedium)
                        if (!FocusAlarm.exactAllowed(context)) Text("Alarms may come a few minutes late: allow alarms so they come on time.", style = MaterialTheme.typography.bodyMedium)
                        OutlinedButton(onClick = {
                            context.startActivity(
                                android.content.Intent(android.provider.Settings.ACTION_APP_NOTIFICATION_SETTINGS)
                                    .putExtra(android.provider.Settings.EXTRA_APP_PACKAGE, context.packageName),
                            )
                        }) { Text("Notification settings") }
                        if (!FocusAlarm.exactAllowed(context) && android.os.Build.VERSION.SDK_INT >= 31) {
                            OutlinedButton(onClick = {
                                context.startActivity(android.content.Intent(android.provider.Settings.ACTION_REQUEST_SCHEDULE_EXACT_ALARM, android.net.Uri.parse("package:${context.packageName}")))
                            }) { Text("Allow alarms") }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun Setting(title: String, hint: String, content: @Composable () -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text(title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
        Text(hint, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        content()
    }
}

@Composable
private fun FocusLengths(vm: BoardViewModel) {
    val s = vm.focusSettings
    var focus by rememberSaveable(s) { mutableStateOf(s.focusMinutes.toString()) }
    var short by rememberSaveable(s) { mutableStateOf(s.shortBreakMinutes.toString()) }
    var long by rememberSaveable(s) { mutableStateOf(s.longBreakMinutes.toString()) }
    Setting("Focus timer", "Minutes of a focus session, the break after it, and the longer break after every ${s.focusesBeforeLongBreak}th.") {
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            listOf(Triple("Focus", focus) { v: String -> focus = v }, Triple("Break", short) { v: String -> short = v }, Triple("Long break", long) { v: String -> long = v }).forEach { (label, value, set) ->
                OutlinedTextField(
                    value = value, onValueChange = set, label = { Text(label) }, singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number), modifier = Modifier.weight(1f),
                )
            }
        }
        val changed = focus != s.focusMinutes.toString() || short != s.shortBreakMinutes.toString() || long != s.longBreakMinutes.toString()
        OutlinedButton(
            onClick = { vm.setFocusLengths(focus.toIntOrNull() ?: 0, short.toIntOrNull() ?: 0, long.toIntOrNull() ?: 0) },
            enabled = changed,
        ) { Text("Save") }
    }
}

/** Colors a group can have (the web app's palette); the first is no color. */
val GROUP_COLORS = listOf(null, "#3b82f6", "#22c55e", "#f59e0b", "#ef4444", "#a855f7", "#ec4899", "#14b8a6", "#64748b")

fun parseColor(hex: String?): Color? = hex?.takeIf { it.length == 7 }?.let { runCatching { Color(android.graphics.Color.parseColor(it)) }.getOrNull() }

/** A group's name and color, and deleting it (its tasks move to another group first: none are lost). */
@OptIn(ExperimentalLayoutApi::class)
@Composable
fun GroupDialog(id: UUID, vm: BoardViewModel, onDismiss: () -> Unit) {
    val name0 = vm.groupName(id)
    if (name0 == null) {
        androidx.compose.runtime.LaunchedEffect(id) { onDismiss() }
        return
    }
    var name by rememberSaveable(id) { mutableStateOf(name0) }
    var color by rememberSaveable(id) { mutableStateOf(vm.groupColors[id]) }
    var deleting by remember { mutableStateOf(false) }
    val others = vm.groups.filter { it.first != id }
    if (deleting) {
        var target by remember { mutableStateOf(others.firstOrNull()?.first) }
        AlertDialog(
            onDismissRequest = { deleting = false },
            title = { Text("Delete “$name0”?") },
            text = {
                Column {
                    Text("Its tasks move to:")
                    others.forEach { (gid, gname) ->
                        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth().clickable { target = gid }.padding(vertical = 4.dp)) {
                            androidx.compose.material3.RadioButton(selected = target == gid, onClick = { target = gid })
                            Text(gname)
                        }
                    }
                }
            },
            confirmButton = {
                TextButton(onClick = { target?.let { if (vm.deleteGroup(id, it)) { deleting = false; onDismiss() } } }, enabled = target != null) {
                    Text("Delete group", color = MaterialTheme.colorScheme.error)
                }
            },
            dismissButton = { TextButton(onClick = { deleting = false }) { Text("Cancel") } },
        )
        return
    }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Group") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                OutlinedTextField(value = name, onValueChange = { name = it }, label = { Text("Name") }, singleLine = true)
                FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    GROUP_COLORS.forEach { hex ->
                        val c = parseColor(hex)
                        val chosen = hex == color
                        Text(
                            if (c == null) "∅" else if (chosen) "✓" else "",
                            color = if (c == null) MaterialTheme.colorScheme.onSurfaceVariant else Color.White,
                            modifier = Modifier.size(32.dp).background(c ?: MaterialTheme.colorScheme.surfaceVariant, CircleShape)
                                .border(if (chosen) 3.dp else 0.dp, MaterialTheme.colorScheme.onSurface, CircleShape)
                                .clickable { color = hex }.padding(top = 6.dp)
                                .semantics { contentDescription = hex?.let { "Color $it" } ?: "No color" },
                            textAlign = androidx.compose.ui.text.style.TextAlign.Center,
                        )
                    }
                }
                if (others.isNotEmpty()) TextButton(onClick = { deleting = true }) { Text("Delete group…", color = MaterialTheme.colorScheme.error) }
            }
        },
        confirmButton = { TextButton(onClick = { if (vm.updateGroup(id, name, color)) onDismiss() }, enabled = name.isNotBlank()) { Text("Save") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}
