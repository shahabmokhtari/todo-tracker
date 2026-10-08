package dev.todotracker.app

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilterChip
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
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarDuration
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.SnackbarResult
import java.util.UUID
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import dev.todotracker.core.AgendaEntry
import dev.todotracker.core.Priority
import java.time.Duration
import java.time.Instant
import java.time.LocalTime

/** Today: the one thing to do now (big), then the rest of Do now, then what's waiting. */
@Composable
fun TodayScreen(vm: BoardViewModel = viewModel()) {
    // Kept across rotation and theme changes (as ids and text).
    var noteFor by rememberSaveable { mutableStateOf<String?>(null) }
    var addingGroup by rememberSaveable { mutableStateOf(false) }
    val snackbar = remember { SnackbarHostState() }
    val undoable = vm.undoable
    LaunchedEffect(undoable) {
        if (undoable != null) {
            val result = snackbar.showSnackbar(undoable.message, actionLabel = "Undo", duration = SnackbarDuration.Short)
            if (result == SnackbarResult.ActionPerformed) undoable.undo()
            vm.undoable = null
        }
    }
    Scaffold(snackbarHost = { SnackbarHost(snackbar) }, containerColor = MaterialTheme.colorScheme.background) { padding ->
        Column(Modifier.padding(padding).imePadding().padding(horizontal = 16.dp)) {
            Header(vm)
            GroupTabs(vm, onAdd = { addingGroup = true })
            Capture(vm)
            vm.status?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(vertical = 4.dp)) }
            LazyColumn(verticalArrangement = Arrangement.spacedBy(10.dp), modifier = Modifier.fillMaxSize()) {
                val focus = vm.dashboard.focus
                if (focus == null) {
                    item { Text("Nothing is due. Nice.", style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(top = 24.dp)) }
                } else {
                    item { FocusCard(focus, vm, onNote = { noteFor = focus.item.id.toString() }) }
                }
                val rest = vm.dashboard.now.drop(1)
                if (rest.isNotEmpty()) {
                    item { SectionTitle("Do now", rest.size) }
                    items(rest, key = { it.item.id }) { entry -> TaskRow(entry, vm, onNote = { noteFor = entry.item.id.toString() }) }
                }
                if (vm.dashboard.waiting.isNotEmpty()) {
                    item { SectionTitle("Waiting", vm.dashboard.waiting.size) }
                    items(vm.dashboard.waiting, key = { "w" + it.item.id }) { WaitingRow(it, vm) }
                }
                item { Spacer(Modifier.height(24.dp)) }
            }
        }
    }
    noteFor?.let { id ->
        val uuid = UUID.fromString(id)
        TextDialog("Note on “${vm.titleOf(uuid).orEmpty()}”", "What happened, what's next…", onDone = { vm.addNote(uuid, it) }, onDismiss = { noteFor = null })
    }
    if (addingGroup) TextDialog("New group", "Name", onDone = { vm.addGroup(it) }, onDismiss = { addingGroup = false })
}

@Composable
private fun Header(vm: BoardViewModel) {
    val hour = LocalTime.now().hour
    val greeting = when {
        hour < 5 -> "Still up?"
        hour < 12 -> "Good morning"
        hour < 18 -> "Good afternoon"
        else -> "Good evening"
    }
    val count = vm.dashboard.now.size
    Column(Modifier.padding(top = 12.dp, bottom = 8.dp)) {
        Text(greeting, style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Bold)
        Text(
            if (count == 0) "All clear. Nothing needs you right now." else "$count thing${if (count == 1) "" else "s"} to do now",
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

@Composable
private fun GroupTabs(vm: BoardViewModel, onAdd: () -> Unit) {
    LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        item { FilterChip(selected = vm.selectedGroup == null, onClick = { vm.select(null) }, label = { Text("All ${vm.count(null)}") }) }
        items(vm.groups, key = { it.first }) { (id, name) ->
            FilterChip(selected = vm.selectedGroup == id, onClick = { vm.select(id) }, label = { Text("$name ${vm.count(id)}") })
        }
        item { TextButton(onClick = onAdd) { Text("+ Group") } }
    }
}

@Composable
private fun Capture(vm: BoardViewModel) {
    var text by rememberSaveable { mutableStateOf("") }
    fun add() {
        if (vm.capture(text)) text = ""
    }
    Row(Modifier.fillMaxWidth().padding(vertical = 8.dp), verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
        OutlinedTextField(
            value = text,
            onValueChange = { text = it },
            placeholder = { Text("Add a task: @tomorrow !high due:3d") },
            singleLine = true,
            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
            keyboardActions = KeyboardActions(onDone = { add() }),
            modifier = Modifier.weight(1f),
        )
        Button(onClick = { add() }, enabled = text.isNotBlank(), modifier = Modifier.padding(start = 8.dp)) { Text("Add") }
    }
}

@Composable
private fun SectionTitle(title: String, count: Int) {
    Text("$title · $count", style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 12.dp))
}

private fun priorityLabel(p: Priority) = when (p) {
    Priority.CRITICAL -> "Critical"
    Priority.HIGH -> "High"
    Priority.LOW -> "Low"
    Priority.NORMAL -> null
}

private fun relative(at: Instant): String {
    val minutes = Duration.between(Instant.now(), at).toMinutes()
    return when {
        minutes < 1 -> "now"
        minutes < 60 -> "in ${minutes}m"
        minutes < 24 * 60 -> "in ${minutes / 60}h"
        else -> "in ${minutes / (24 * 60)}d"
    }
}

@Composable
private fun Meta(entry: AgendaEntry) {
    val parts = listOfNotNull(
        entry.breadcrumb.takeIf { it.isNotEmpty() }?.joinToString(" › "),
        entry.stepLabel,
        priorityLabel(entry.effectivePriority),
        if (entry.isOverdue) "overdue" else entry.item.deadline?.let { "due ${relative(it)}" },
        entry.dueReminder?.let { "⏰ ${it.message}" },
    )
    if (parts.isNotEmpty()) Text(parts.joinToString(" · "), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
}

@Composable
private fun FocusCard(entry: AgendaEntry, vm: BoardViewModel, onNote: () -> Unit) {
    Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.primaryContainer), modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("Do this now", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onPrimaryContainer)
            Text(entry.item.title, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
            Meta(entry)
            Actions(entry, vm, onNote)
        }
    }
}

@Composable
private fun TaskRow(entry: AgendaEntry, vm: BoardViewModel, onNote: () -> Unit) {
    Card(modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Text(entry.item.title, style = MaterialTheme.typography.titleMedium)
            Meta(entry)
            Actions(entry, vm, onNote)
        }
    }
}

@Composable
private fun Actions(entry: AgendaEntry, vm: BoardViewModel, onNote: () -> Unit) {
    var later by remember { mutableStateOf(false) }
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        if (!entry.item.hasOpenChildren) Button(onClick = { vm.complete(entry.item.id) }) { Text("Done") }
        entry.dueReminder?.let { r -> OutlinedButton(onClick = { vm.dismissReminder(entry.item.id, r.id) }) { Text("Dismiss") } }
        Box {
            OutlinedButton(onClick = { later = true }) { Text("Later") }
            DropdownMenu(expanded = later, onDismissRequest = { later = false }) {
                listOf("15 min" to 15L, "1 hour" to 60L, "3 hours" to 180L).forEach { (label, minutes) ->
                    DropdownMenuItem(text = { Text(label) }, onClick = { later = false; vm.snooze(entry.item.id, minutes) })
                }
                DropdownMenuItem(text = { Text("Tomorrow 9:00") }, onClick = { later = false; vm.tomorrow(entry.item.id) })
            }
        }
        TextButton(onClick = onNote) { Text("Note") }
    }
}

@Composable
private fun WaitingRow(entry: AgendaEntry, vm: BoardViewModel) {
    Card(modifier = Modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant)) {
        Row(Modifier.padding(12.dp).fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(entry.item.title, style = MaterialTheme.typography.bodyLarge)
                entry.wakeAt?.let { Text("back ${relative(it)}", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            }
            TextButton(onClick = { vm.bringBack(entry.item.id) }) { Text("Do now") }
        }
    }
}

@Composable
private fun TextDialog(title: String, placeholder: String, onDone: (String) -> Boolean, onDismiss: () -> Unit) {
    var text by rememberSaveable { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = { OutlinedTextField(value = text, onValueChange = { text = it }, placeholder = { Text(placeholder) }) },
        // Closes only when it was saved (else what was typed stays, and the reason shows).
        confirmButton = { TextButton(onClick = { if (onDone(text)) onDismiss() }, enabled = text.isNotBlank()) { Text("Save") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}
