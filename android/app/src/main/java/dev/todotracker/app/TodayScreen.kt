package dev.todotracker.app

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilterChip
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarDuration
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.SnackbarResult
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import dev.todotracker.core.Agenda
import dev.todotracker.core.AgendaEntry
import dev.todotracker.core.ItemState
import dev.todotracker.core.OverviewEntry
import dev.todotracker.core.Priority
import dev.todotracker.core.WorkItem
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.util.UUID

/** Asks (once) for what reminders need: notifications, and alarms on time. Called before a snooze, reminder or focus. */
val LocalAskToNotify = staticCompositionLocalOf<() -> Unit> { {} }

/** Today: the one thing to do now (big), then the rest of Do now, what's waiting, what got done, workstreams, notes. */
@Composable
fun TodayScreen(vm: BoardViewModel = viewModel()) {
    // Kept across rotation and theme changes (as ids and text).
    var noteFor by rememberSaveable { mutableStateOf<String?>(null) }
    var addingGroup by rememberSaveable { mutableStateOf(false) }
    var editingGroup by rememberSaveable { mutableStateOf<String?>(null) }
    var settings by rememberSaveable { mutableStateOf(false) }
    var searching by rememberSaveable { mutableStateOf(false) }
    val snackbar = remember { SnackbarHostState() }
    // A snooze, a reminder or a focus session is when notifications start to matter (they tell you with the app closed).
    val context = androidx.compose.ui.platform.LocalContext.current
    val askNotifications = androidx.activity.compose.rememberLauncherForActivityResult(androidx.activity.result.contract.ActivityResultContracts.RequestPermission()) {}
    var askExact by rememberSaveable { mutableStateOf(false) }
    val askToNotify: () -> Unit = {
        if (android.os.Build.VERSION.SDK_INT >= 33 &&
            androidx.core.content.ContextCompat.checkSelfPermission(context, android.Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED
        ) {
            askNotifications.launch(android.Manifest.permission.POST_NOTIFICATIONS)
        }
        // On time to the minute needs the person's OK on recent Android: offered once, with why.
        val prefs = context.getSharedPreferences("focus-alarm", android.content.Context.MODE_PRIVATE)
        if (!FocusAlarm.exactAllowed(context) && !prefs.getBoolean("asked-exact", false)) {
            prefs.edit().putBoolean("asked-exact", true).apply()
            askExact = true
        }
    }
    val startFocusOn: (UUID?) -> Unit = { id ->
        askToNotify()
        vm.startFocus(id)
    }
    val undoable = vm.undoable
    LaunchedEffect(undoable) {
        if (undoable != null) {
            val result = snackbar.showSnackbar(undoable.message, actionLabel = "Undo", duration = SnackbarDuration.Short)
            if (result == SnackbarResult.ActionPerformed) undoable.undo()
            vm.undoable = null
        }
    }
    CompositionLocalProvider(LocalAskToNotify provides askToNotify) {
      // The task's details and Settings are drawn over Today (full screen); Undo shows over either.
      Box(Modifier.fillMaxSize()) {
        Scaffold(
            bottomBar = { FocusBar(vm, onStart = { startFocusOn(null) }) },
            containerColor = MaterialTheme.colorScheme.background,
        ) { padding ->
            Column(Modifier.padding(padding).imePadding().padding(horizontal = 16.dp)) {
                Header(vm, onSettings = { settings = true }, onSearch = { searching = !searching; if (!searching) vm.search("") })
                RemoteBanner(vm)
                GroupTabs(vm, onAdd = { addingGroup = true }, onEdit = { editingGroup = it.toString() })
                Capture(vm)
                if (searching || vm.query.isNotEmpty()) SearchBox(vm, onClose = { searching = false; vm.search("") })
                vm.status?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(vertical = 4.dp)) }
                TimerBar(vm)
                val results = vm.searchResults
                if (results != null) {
                    SearchResults(results, vm)
                } else {
                    Lists(vm, onNote = { noteFor = it.toString() }, onFocus = startFocusOn)
                }
            }
        }
        noteFor?.let { id ->
            val uuid = UUID.fromString(id)
            TextDialog("Note on “${vm.titleOf(uuid).orEmpty()}”", "What happened, what's next…", onDone = { vm.addNote(uuid, it) }, onDismiss = { noteFor = null })
        }
        if (addingGroup) TextDialog("New group", "Name", onDone = { vm.addGroup(it) }, onDismiss = { addingGroup = false })
        editingGroup?.let { GroupDialog(UUID.fromString(it), vm, onDismiss = { editingGroup = null }) }
        // A task's details (from a card, a search, a todotracker://task/<id> link or a reminder's notification).
        vm.openTaskId?.let { id -> TaskScreen(id, vm, onClose = { vm.openTaskId = null }) }
        if (settings) SettingsScreen(vm, onClose = { settings = false })
        // A todotracker://pair link (opened, scanned or pasted): asked first.
        PairingDialog(vm)
        SnackbarHost(snackbar, Modifier.align(Alignment.BottomCenter).navigationBarsPadding().padding(bottom = 88.dp))
      }
        if (askExact) {
            AlertDialog(
                onDismissRequest = { askExact = false },
                title = { Text("Ring on time?") },
                text = { Text("So reminders, and the end of a focus session or a break, are told right when they happen (not a few minutes later), allow Todo Tracker to set alarms.") },
                confirmButton = {
                    TextButton(onClick = {
                        askExact = false
                        if (android.os.Build.VERSION.SDK_INT >= 31) {
                            context.startActivity(android.content.Intent(android.provider.Settings.ACTION_REQUEST_SCHEDULE_EXACT_ALARM, android.net.Uri.parse("package:${context.packageName}")))
                        }
                    }) { Text("Allow") }
                },
                dismissButton = { TextButton(onClick = { askExact = false }) { Text("Not now") } },
            )
        }
        BreakScreen(vm)
    }
}

@Composable
private fun Lists(vm: BoardViewModel, onNote: (UUID) -> Unit, onFocus: (UUID?) -> Unit) {
    var showStreams by rememberSaveable { mutableStateOf(true) }
    var showNotes by rememberSaveable { mutableStateOf(true) }
    LazyColumn(verticalArrangement = Arrangement.spacedBy(10.dp), modifier = Modifier.fillMaxSize()) {
        val focus = vm.dashboard.focus
        if (focus == null) {
            item { Text("Nothing is due. Nice.", style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(top = 24.dp)) }
        } else {
            item { FocusCard(focus, vm, onNote = { onNote(focus.item.id) }, onFocus = onFocus) }
        }
        val rest = vm.dashboard.now.drop(1)
        if (rest.isNotEmpty()) {
            item { SectionTitle("Do now", rest.size) }
            items(rest, key = { it.item.id }) { entry -> TaskRow(entry, vm, onNote = { onNote(entry.item.id) }, onFocus = onFocus) }
        }
        if (vm.dashboard.waiting.isNotEmpty()) {
            item { SectionTitle("Waiting", vm.dashboard.waiting.size) }
            items(vm.dashboard.waiting, key = { "w" + it.item.id }) { WaitingRow(it, vm) }
        }
        if (vm.doneToday.isNotEmpty()) {
            item { SectionTitle("Done today", vm.doneToday.size) }
            Agenda.cheer(vm.doneToday.size)?.let { cheer -> item { CheerLine(cheer) } }
            items(vm.doneToday, key = { "d" + it.id }) { DoneRow(it, vm) }
        }
        val streams = Agenda.workstreams(vm.dashboard)
        if (streams.isNotEmpty()) {
            item { FoldTitle("Workstreams", streams.size, showStreams) { showStreams = !showStreams } }
            if (showStreams) items(streams, key = { "s" + it.item.id }) { WorkstreamRow(it, vm) }
        }
        val notes = vm.dashboard.recentNotes
        if (notes.isNotEmpty()) {
            item { FoldTitle("Recent notes", notes.size, showNotes) { showNotes = !showNotes } }
            if (showNotes) items(notes, key = { "n" + it.note.id }) { n ->
                Column(Modifier.fillMaxWidth().clickable { vm.openTaskId = n.item.id }.padding(vertical = 4.dp)) {
                    Text(n.note.text, style = MaterialTheme.typography.bodyMedium, maxLines = 4)
                    Text("${n.note.author.displayName} · ${n.item.title} · ${ago(n.note.at, vm.now)}", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1)
                }
            }
        }
        item { Spacer(Modifier.height(24.dp)) }
    }
}

@Composable
private fun Header(vm: BoardViewModel, onSettings: () -> Unit, onSearch: () -> Unit) {
    // The view model's clock (it moves every second), so the greeting changes when the day part does.
    val hour = vm.now.atZone(ZoneId.systemDefault()).hour
    val greeting = when {
        hour < 5 -> "Still up?"
        hour < 12 -> "Good morning"
        hour < 18 -> "Good afternoon"
        else -> "Good evening"
    }
    val count = vm.dashboard.now.size
    Row(Modifier.padding(top = 12.dp, bottom = 8.dp), verticalAlignment = Alignment.Top) {
        Column(Modifier.weight(1f)) {
            Text(greeting, style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Bold)
            Text(
                if (count == 0) "All clear. Nothing needs you right now." else "$count thing${if (count == 1) "" else "s"} to do now",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            // Connected: whose tasks these are.
            vm.computer?.let { Text("On $it", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.primary, maxLines = 1) }
            vm.energyToday?.let { energy ->
                Text(energy, style = MaterialTheme.typography.bodySmall, color = if (vm.energyOver) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        TextButton(onClick = onSearch, modifier = Modifier.semantics { contentDescription = "Search" }) { Text("🔍", style = MaterialTheme.typography.titleLarge) }
        TextButton(onClick = onSettings, modifier = Modifier.semantics { contentDescription = "Settings" }) { Text("⚙", style = MaterialTheme.typography.titleLarge) }
    }
}

@Composable
private fun GroupTabs(vm: BoardViewModel, onAdd: () -> Unit, onEdit: (UUID) -> Unit) {
    LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
        item { FilterChip(selected = vm.selectedGroup == null, onClick = { vm.select(null) }, label = { Text("All ${vm.count(null)}") }) }
        items(vm.groups, key = { it.first }) { (id, name) ->
            FilterChip(
                selected = vm.selectedGroup == id,
                // Tapping the chosen group again opens it (name, color, delete).
                onClick = { if (vm.selectedGroup == id) onEdit(id) else vm.select(id) },
                label = { Text("$name ${vm.count(id)}") },
                leadingIcon = parseColor(vm.groupColors[id])?.let { c -> { Box(Modifier.size(10.dp).background(c, CircleShape)) } },
            )
        }
        vm.selectedGroup?.let { id -> item { TextButton(onClick = { onEdit(id) }) { Text("Edit") } } }
        item { TextButton(onClick = onAdd) { Text("+ Group") } }
    }
}

@Composable
private fun Capture(vm: BoardViewModel) {
    var text by rememberSaveable { mutableStateOf("") }
    fun add() {
        if (vm.capture(text)) text = ""
    }
    Row(Modifier.fillMaxWidth().padding(top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        OutlinedTextField(
            value = text,
            onValueChange = { text = it },
            placeholder = { Text("Add: @tomorrow !high due:3d") },
            singleLine = true,
            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
            keyboardActions = KeyboardActions(onDone = { add() }),
            modifier = Modifier.weight(1f),
        )
        Button(onClick = { add() }, enabled = text.isNotBlank(), modifier = Modifier.padding(start = 8.dp)) { Text("Add") }
    }
}

/** Finds any task (done and archived ones too, when asked): words, is:done, is:archived, group:work, #tag. */
@Composable
private fun SearchBox(vm: BoardViewModel, onClose: () -> Unit) {
    val focus = remember { androidx.compose.ui.focus.FocusRequester() }
    OutlinedTextField(
        value = vm.query,
        onValueChange = { vm.search(it) },
        placeholder = { Text("Find a task (is:done, is:archived)") },
        singleLine = true,
        trailingIcon = { TextButton(onClick = onClose) { Text("Close") } },
        keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
        modifier = Modifier.fillMaxWidth().padding(vertical = 6.dp).focusRequester(focus).semantics { contentDescription = "Search box" },
    )
    LaunchedEffect(Unit) { if (vm.query.isEmpty()) focus.requestFocus() }
}

@Composable
private fun SearchResults(results: List<WorkItem>, vm: BoardViewModel) {
    LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.fillMaxSize()) {
        item { SectionTitle("Found", results.size) }
        if (results.isEmpty()) item { Text("Nothing matches. Done tasks are found with is:done, archived ones with is:archived.", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant) }
        items(results, key = { "f" + it.id }) { item ->
            Card(Modifier.fillMaxWidth().clickable { vm.openTaskId = item.id }) {
                Column(Modifier.padding(12.dp)) {
                    Text(
                        titleWithIcon(item),
                        style = MaterialTheme.typography.bodyLarge.copy(textDecoration = if (item.isDone) TextDecoration.LineThrough else null),
                    )
                    val state = Agenda.stateOf(item, vm.now)
                    val parts = listOfNotNull(
                        item.ancestors.reversed().takeIf { it.isNotEmpty() }?.joinToString(" › ") { it.title },
                        vm.groupName(item.groupId),
                        when {
                            item.isArchived -> "archived"
                            state == ItemState.DONE -> "done"
                            state == ItemState.WAITING -> "waiting"
                            state == ItemState.LOCKED -> "after an earlier step"
                            else -> null
                        },
                    )
                    Text(parts.joinToString(" · "), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
        }
        item { Spacer(Modifier.height(24.dp)) }
    }
}

@Composable
private fun SectionTitle(title: String, count: Int) {
    Text("$title · $count", style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 12.dp))
}

/** A section title that folds its section away (and back). */
@Composable
private fun FoldTitle(title: String, count: Int, open: Boolean, onToggle: () -> Unit) {
    Text(
        "$title · $count ${if (open) "▴" else "▾"}",
        style = MaterialTheme.typography.titleSmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = Modifier.fillMaxWidth().clickable(onClick = onToggle).padding(top = 12.dp, bottom = 4.dp),
    )
}

private fun priorityLabel(p: Priority) = when (p) {
    Priority.CRITICAL -> "Critical"
    Priority.HIGH -> "High"
    Priority.LOW -> "Low"
    Priority.NORMAL -> null
}

private fun relative(at: Instant, now: Instant): String {
    val minutes = Duration.between(now, at).toMinutes()
    return when {
        minutes < 1 -> "now"
        minutes < 60 -> "in ${minutes}m"
        minutes < 24 * 60 -> "in ${minutes / 60}h"
        else -> "in ${minutes / (24 * 60)}d"
    }
}

@Composable
private fun Meta(entry: AgendaEntry, now: Instant) {
    val parts = listOfNotNull(
        entry.breadcrumb.takeIf { it.isNotEmpty() }?.joinToString(" › "),
        entry.stepLabel,
        priorityLabel(entry.effectivePriority),
        if (entry.isOverdue) "overdue" else entry.item.deadline?.let { "due ${relative(it, now)}" },
        entry.dueReminder?.let { "⏰ ${it.message}" },
        entry.item.energy?.let { "⚡".repeat(it) },
    )
    if (parts.isNotEmpty()) Text(parts.joinToString(" · "), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
}

/** A card's notes, newest first: the text, then who wrote it and when. */
@Composable
internal fun CardNotes(item: WorkItem) {
    if (item.notes.isEmpty()) return
    val now = Instant.now()
    Column(verticalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.semantics { contentDescription = "Notes" }) {
        for (note in item.notes.sortedByDescending { it.at }) {
            Column(Modifier.padding(start = 8.dp)) {
                Text(note.text, style = MaterialTheme.typography.bodyMedium)
                Text("${note.author.displayName} · ${ago(note.at, now)}", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
    }
}

/** How long ago: "now", "5m ago", "2h ago", "3d ago". */
private fun ago(at: Instant, now: Instant): String {
    val minutes = Duration.between(at, now).toMinutes()
    return when {
        minutes < 1 -> "now"
        minutes < 60 -> "${minutes}m ago"
        minutes < 24 * 60 -> "${minutes / 60}h ago"
        else -> "${minutes / (24 * 60)}d ago"
    }
}

@Composable
private fun FocusCard(entry: AgendaEntry, vm: BoardViewModel, onNote: () -> Unit, onFocus: (UUID?) -> Unit) {
    Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.primaryContainer), modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("Do this now", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onPrimaryContainer)
            // The title opens the task's details.
            Text(titleWithIcon(entry.item), style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold, modifier = Modifier.clickable { vm.openTaskId = entry.item.id })
            Meta(entry, vm.now)
            // The task you're on: every note, newest first.
            CardNotes(entry.item)
            Actions(entry, vm, onNote, onFocus, focus = true)
        }
    }
}

@Composable
private fun TaskRow(entry: AgendaEntry, vm: BoardViewModel, onNote: () -> Unit, onFocus: (UUID?) -> Unit) {
    Card(modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Text(titleWithIcon(entry.item), style = MaterialTheme.typography.titleMedium, modifier = Modifier.fillMaxWidth().clickable { vm.openTaskId = entry.item.id })
            Meta(entry, vm.now)
            if (entry.item.notes.isNotEmpty()) {
                var open by rememberSaveable(entry.item.id) { mutableStateOf(false) }
                val count = entry.item.notes.size
                TextButton(onClick = { open = !open }, contentPadding = androidx.compose.foundation.layout.PaddingValues(0.dp)) {
                    Text(if (open) "Hide notes ▴" else if (count == 1) "1 note ▾" else "$count notes ▾", style = MaterialTheme.typography.labelMedium)
                }
                if (open) CardNotes(entry.item)
            }
            Actions(entry, vm, onNote, onFocus)
        }
    }
}

@Composable
private fun Actions(entry: AgendaEntry, vm: BoardViewModel, onNote: () -> Unit, onFocus: (UUID?) -> Unit, focus: Boolean = false) {
    // Wraps onto a second line on a narrow phone (every button stays in sight).
    @OptIn(androidx.compose.foundation.layout.ExperimentalLayoutApi::class)
    androidx.compose.foundation.layout.FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        // "Mark done", not "Done": a bare "Done" reads as "close this".
        if (!entry.item.hasOpenChildren) Button(onClick = { vm.complete(entry.item.id) }) { Text("Mark done") }
        LaterMenu(entry.item.id, vm)
        TextButton(onClick = onNote) { Text("Note") }
        TextButton(onClick = { onFocus(entry.item.id) }) { Text("Focus") }
        val timing = vm.timer?.item?.id == entry.item.id
        TextButton(onClick = { vm.toggleTimer(entry.item.id) }) { Text(if (timing) "Stop timer" else "Timer") }
        // Do something else first: this one moves down the list.
        if (focus && vm.dashboard.now.size > 1) TextButton(onClick = { vm.skip(entry.item.id) }) { Text("Skip") }
        MoreMenu(entry, vm)
    }
}

/** The rarer actions behind ⋯ (opening the details, dismissing a reminder, copying a link), so the buttons stay few. */
@Composable
private fun MoreMenu(entry: AgendaEntry, vm: BoardViewModel) {
    var open by remember { mutableStateOf(false) }
    val context = androidx.compose.ui.platform.LocalContext.current
    Box {
        TextButton(onClick = { open = true }, modifier = Modifier.semantics { contentDescription = "More" }) { Text("⋯") }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            // The task's details: edit it, its subtasks, reminders, every note.
            DropdownMenuItem(text = { Text("Open") }, onClick = { open = false; vm.openTaskId = entry.item.id })
            entry.dueReminder?.let { r ->
                DropdownMenuItem(text = { Text("Dismiss reminder") }, onClick = { open = false; vm.dismissReminder(entry.item.id, r.id) })
            }
            DropdownMenuItem(text = { Text("Copy link") }, onClick = {
                open = false
                context.getSystemService(android.content.ClipboardManager::class.java)
                    ?.setPrimaryClip(android.content.ClipData.newPlainText("Task link", dev.todotracker.core.TaskLinks.forTask(entry.item.id)))
            })
        }
    }
}

/** Later: the quick choices (each says when), a date and time, or after another task. Each can be undone. */
@Composable
internal fun LaterMenu(id: UUID, vm: BoardViewModel) {
    var open by remember { mutableStateOf(false) }
    var pickingTask by remember { mutableStateOf(false) }
    var pickingTime by remember { mutableStateOf(false) }
    val ask = LocalAskToNotify.current
    Box {
        OutlinedButton(onClick = { open = true }) { Text("Later") }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            vm.snoozeChoices().forEach { choice ->
                DropdownMenuItem(
                    text = { Text(choice.label) },
                    trailingIcon = { Text(vm.describe(choice.at), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) },
                    onClick = { open = false; ask(); vm.snoozeUntil(id, choice.at) },
                )
            }
            androidx.compose.material3.HorizontalDivider()
            DropdownMenuItem(text = { Text("Pick a time…") }, onClick = { open = false; pickingTime = true })
            DropdownMenuItem(text = { Text("After another task…") }, onClick = { open = false; pickingTask = true })
        }
    }
    if (pickingTime) {
        val start = dev.todotracker.core.QuickCaptureParser.tomorrowMorning(Instant.now(), ZoneId.systemDefault())
        DateTimePicker("Later until", start, future = true, onPicked = { ask(); vm.snoozeUntil(id, it) }, onDismiss = { pickingTime = false })
    }
    if (pickingTask) {
        val others = vm.waitCandidates(id)
        AlertDialog(
            onDismissRequest = { pickingTask = false },
            title = { Text("Waits until this is done") },
            text = {
                if (others.isEmpty()) {
                    Text("No other open tasks.")
                } else {
                    LazyColumn {
                        items(others.size) { i ->
                            val (otherId, title) = others[i]
                            TextButton(onClick = { pickingTask = false; ask(); vm.waitFor(id, otherId) }, modifier = Modifier.fillMaxWidth()) {
                                Text(title, modifier = Modifier.fillMaxWidth())
                            }
                        }
                    }
                }
            },
            confirmButton = {},
            dismissButton = { TextButton(onClick = { pickingTask = false }) { Text("Cancel") } },
        )
    }
}

@Composable
private fun WaitingRow(entry: AgendaEntry, vm: BoardViewModel) {
    Card(modifier = Modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant)) {
        Row(Modifier.padding(12.dp).fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f).clickable { vm.openTaskId = entry.item.id }) {
                Text(titleWithIcon(entry.item), style = MaterialTheme.typography.bodyLarge)
                entry.wakeAt?.let { Text("back ${relative(it, vm.now)}", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
                entry.waitingFor?.let { Text("after “${it.title}”", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            }
            TextButton(onClick = { vm.bringBack(entry.item.id) }) { Text("Do now") }
        }
    }
}

/** A task with subtasks: how far along it is, what's to do now or waiting (tap: its details). */
@Composable
private fun WorkstreamRow(entry: OverviewEntry, vm: BoardViewModel) {
    Card(Modifier.fillMaxWidth().clickable { vm.openTaskId = entry.item.id }) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Text(titleWithIcon(entry.item), style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold)
            LinearProgressIndicator(
                progress = { if (entry.totalLeaves == 0) 0f else entry.doneLeaves.toFloat() / entry.totalLeaves },
                modifier = Modifier.fillMaxWidth(),
            )
            val parts = listOfNotNull(
                "${entry.doneLeaves}/${entry.totalLeaves} done",
                entry.actionableCount.takeIf { it > 0 }?.let { "$it now" },
                entry.waitingCount.takeIf { it > 0 }?.let { "$it waiting" },
                entry.nextWakeAt?.let { "next ${relative(it, vm.now)}" },
                priorityLabel(entry.topPriority),
            )
            Text(parts.joinToString(" · "), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
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

/** Something finished today, when, where it belongs, and a way to undo a slip. */
@Composable
private fun DoneRow(item: WorkItem, vm: BoardViewModel) {
    val context = androidx.compose.ui.platform.LocalContext.current
    Row(Modifier.fillMaxWidth().padding(vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
        Text("✓", color = Color(0xFF16A34A), style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(end = 10.dp))
        Column(Modifier.weight(1f).clickable { vm.openTaskId = item.id }) {
            Text(item.title, style = MaterialTheme.typography.bodyLarge.copy(textDecoration = TextDecoration.LineThrough), color = MaterialTheme.colorScheme.onSurfaceVariant)
            val at = vm.doneAt(item)?.let { android.text.format.DateFormat.getTimeFormat(context).format(java.util.Date(it.toEpochMilli())) }
            val line = listOfNotNull(at, item.parent?.title).joinToString(" · ")
            if (line.isNotEmpty()) Text(line, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        TextButton(onClick = { vm.reopen(item.id) }) { Text("Reopen") }
    }
}

/** A cheer for what got done today. */
@Composable
private fun CheerLine(text: String) {
    Text(
        "🎉 $text",
        color = Color(0xFF16A34A),
        style = MaterialTheme.typography.titleSmall,
        modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp)
            .background(Color(0x1F22C55E), RoundedCornerShape(10.dp)).padding(horizontal = 12.dp, vertical = 8.dp),
    )
}

/** A task's title with its icon (an emoji, set in the other apps) in front. */
internal fun titleWithIcon(item: WorkItem): String =
    (item.icon?.takeIf { it.isNotBlank() }?.let { "$it ${item.title}" } ?: item.title) + if (item.repeat != null) " 🔁" else ""
