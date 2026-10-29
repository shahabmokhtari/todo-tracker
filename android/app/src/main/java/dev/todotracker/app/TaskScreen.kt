package dev.todotracker.app

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
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
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.unit.dp
import dev.todotracker.core.Agenda
import dev.todotracker.core.ItemState
import dev.todotracker.core.Priority
import dev.todotracker.core.TaskChanges
import dev.todotracker.core.WorkItem
import java.time.Instant
import java.time.temporal.ChronoUnit
import java.util.UUID

/** Repeat rules to pick from (the same list as the web app's repeat box); any other rule in words works too. */
private val REPEAT_CHOICES = listOf(
    "every day", "every weekday", "every week", "every 2 weeks", "every month", "every year",
    "every week on Monday", "every month on the 1st", "every month on the last day", "every month on the last Friday",
    "every 3 days when done",
)

/**
 * A task's details, full screen: what it is (title, details, priority, deadline, energy, repeat, group), what to do
 * with it (done, Later, archive, delete), its subtasks and steps, reminders and notes. What's typed is saved when you
 * leave a box's screen (Close, Back, or opening another task) or with Save.
 */
@Composable
fun TaskScreen(id: UUID, vm: BoardViewModel, onClose: () -> Unit) {
    @Suppress("UNUSED_VARIABLE") val redraw = vm.version // Redraws when the board changes.
    val item = vm.itemOf(id)
    if (item == null) {
        // Deleted meanwhile (or never on this phone).
        androidx.compose.runtime.LaunchedEffect(id) { onClose() }
        return
    }
    var title by rememberSaveable(id) { mutableStateOf(item.title) }
    var details by rememberSaveable(id) { mutableStateOf(item.details.orEmpty()) }
    var note by rememberSaveable(id) { mutableStateOf("") }
    // Typing isn't lost: closing (or going to another task) saves what changed.
    val saveTyped: () -> Boolean = {
        val changes = TaskChanges(
            title = title.takeIf { it.trim() != item.title },
            details = details.takeIf { it.trim() != item.details.orEmpty() },
        )
        if (changes.title == null && changes.details == null) true else vm.edit(id, changes)
    }
    val close = { if (saveTyped()) onClose() }
    val open: (UUID) -> Unit = { other -> if (saveTyped()) vm.openTaskId = other }
    BackHandler(onBack = close)

    run {
        Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
            Column(Modifier.safeDrawingPadding().imePadding()) {
                Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                    TextButton(onClick = close) { Text("Close") }
                    Spacer(Modifier.weight(1f))
                    val dirty = title.trim() != item.title || details.trim() != item.details.orEmpty()
                    TextButton(onClick = { saveTyped() }, enabled = dirty) { Text(if (dirty) "Save" else "Saved") }
                }
                vm.status?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(horizontal = 16.dp)) }
                Column(
                    Modifier.verticalScroll(androidx.compose.runtime.key(id) { rememberScrollState() }).padding(horizontal = 16.dp, vertical = 8.dp),
                    verticalArrangement = Arrangement.spacedBy(12.dp),
                ) {
                    // Where it is: the tasks it's part of (each opens).
                    if (item.ancestors.isNotEmpty()) {
                        Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                            item.ancestors.reversed().forEachIndexed { i, a ->
                                if (i > 0) Text("›", color = MaterialTheme.colorScheme.onSurfaceVariant)
                                Text(a.title, color = MaterialTheme.colorScheme.primary, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.clickable { open(a.id) })
                            }
                        }
                    }
                    OutlinedTextField(
                        value = title, onValueChange = { title = it }, label = { Text("Title") },
                        textStyle = MaterialTheme.typography.titleLarge, modifier = Modifier.fillMaxWidth(),
                    )
                    StateLine(item, vm)
                    TaskButtons(item, vm, onClose)
                    OutlinedTextField(
                        value = details, onValueChange = { details = it }, label = { Text("Details") },
                        placeholder = { Text("Links, context, what done looks like…") }, minLines = 3, modifier = Modifier.fillMaxWidth(),
                    )
                    Field("Priority") {
                        ChoiceRow(Priority.entries.map { it to priorityName(it) }, item.priority) { vm.setPriority(id, it) }
                    }
                    DeadlineField(item, vm)
                    Field("Energy an hour") {
                        ChoiceRow(listOf(null to "Not said", 1 to "⚡", 2 to "⚡⚡", 3 to "⚡⚡⚡"), item.energy) { vm.setEnergy(id, it) }
                    }
                    RepeatField(item, vm)
                    if (item.parent == null) GroupField(item, vm)
                    HorizontalDivider()
                    Subtasks(item, vm, open)
                    HorizontalDivider()
                    RemindersSection(item, vm)
                    HorizontalDivider()
                    Section("Notes") {
                        OutlinedTextField(value = note, onValueChange = { note = it }, placeholder = { Text("What happened, what's next…") }, minLines = 2, modifier = Modifier.fillMaxWidth())
                        Button(onClick = { if (vm.addNote(id, note)) note = "" }, enabled = note.isNotBlank()) { Text("Add note") }
                        CardNotes(item)
                    }
                    Spacer(Modifier.height(32.dp))
                }
            }
        }
    }
}

/** Done, waiting (until when, or for what), locked behind an earlier step, or archived. */
@Composable
private fun StateLine(item: WorkItem, vm: BoardViewModel) {
    val state = Agenda.stateOf(item, vm.now)
    val text = when {
        item.isArchived -> "Done · archived"
        state == ItemState.DONE -> vm.doneAt(item)?.let { "Done ${vm.describe(it)}" } ?: "Done"
        state == ItemState.LOCKED -> dev.todotracker.core.TaskBoard.findBlockingStep(item)?.let { "Waits for the step before: “${it.title}”" } ?: "Waits for an earlier step"
        item.waitingFor != null -> "Snoozed until “${item.waitingFor!!.title}” is done"
        item.nextActionAt?.isAfter(vm.now) == true -> "Snoozed until ${vm.describe(item.nextActionAt!!)}"
        else -> null
    }
    val repeat = item.repeat?.let { "🔁 Repeats $it" }
    listOfNotNull(text, repeat).forEach { Text(it, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant) }
}

/** Mark done or Reopen, Later or Do now, Archive (a finished task), Copy link, Delete (asked first). */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun TaskButtons(item: WorkItem, vm: BoardViewModel, onClose: () -> Unit) {
    var confirmDelete by remember { mutableStateOf(false) }
    var confirmDone by remember { mutableStateOf(false) }
    val context = androidx.compose.ui.platform.LocalContext.current
    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        if (item.isDone) {
            OutlinedButton(onClick = { vm.reopen(item.id) }) { Text("Reopen") }
            if (item.parent == null) {
                if (item.archivedAt == null) OutlinedButton(onClick = { vm.archive(item.id); onClose() }) { Text("Archive") }
                else OutlinedButton(onClick = { vm.unarchive(item.id) }) { Text("Unarchive") }
            }
        } else {
            Button(onClick = { if (item.hasOpenChildren) confirmDone = true else vm.complete(item.id) }) { Text("Mark done") }
            val waiting = item.nextActionAt?.isAfter(vm.now) == true || item.waitingFor != null
            if (waiting) OutlinedButton(onClick = { vm.bringBack(item.id) }) { Text("Do now") }
            LaterMenu(item.id, vm)
        }
        TextButton(onClick = {
            context.getSystemService(android.content.ClipboardManager::class.java)
                ?.setPrimaryClip(android.content.ClipData.newPlainText("Task link", dev.todotracker.core.TaskLinks.forTask(item.id)))
            vm.status = "Link copied: it opens this task in the app"
        }) { Text("Copy link") }
        TextButton(onClick = { confirmDelete = true }) { Text("Delete…", color = MaterialTheme.colorScheme.error) }
    }
    if (confirmDone) {
        val open = item.selfAndDescendants.drop(1).count { !it.isDone }
        AlertDialog(
            onDismissRequest = { confirmDone = false },
            title = { Text("Also mark $open open subtask${if (open == 1) "" else "s"} as done?") },
            confirmButton = { TextButton(onClick = { confirmDone = false; vm.complete(item.id) }) { Text("Mark all done") } },
            dismissButton = { TextButton(onClick = { confirmDone = false }) { Text("Cancel") } },
        )
    }
    if (confirmDelete) {
        val subtasks = item.selfAndDescendants.size - 1
        AlertDialog(
            onDismissRequest = { confirmDelete = false },
            title = { Text("Delete “${item.title}”?") },
            text = {
                Text(
                    (if (subtasks > 0) "Its $subtasks subtask${if (subtasks == 1) "" else "s"} and notes go too. " else "Its notes go too. ") +
                        "This can't be undone." +
                        when {
                            item.isArchived -> " Archived, it's already out of the way."
                            item.parent == null -> " To keep it out of the way instead, mark it done and archive it."
                            else -> ""
                        },
                )
            },
            confirmButton = { TextButton(onClick = { confirmDelete = false; vm.delete(item.id) }) { Text("Delete", color = MaterialTheme.colorScheme.error) } },
            dismissButton = { TextButton(onClick = { confirmDelete = false }) { Text("Keep it") } },
        )
    }
}

@Composable
private fun Field(label: String, content: @Composable () -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Text(label, style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
        content()
    }
}

@Composable
private fun Section(title: String, content: @Composable () -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text(title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
        content()
    }
}

/** Chips to pick one of a few (the chosen one is marked). */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun <T> ChoiceRow(choices: List<Pair<T, String>>, chosen: T, onPick: (T) -> Unit) {
    FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        choices.forEach { (value, label) -> FilterChip(selected = value == chosen, onClick = { if (value != chosen) onPick(value) }, label = { Text(label) }) }
    }
}

private fun priorityName(p: Priority) = when (p) {
    Priority.LOW -> "Low"
    Priority.NORMAL -> "Normal"
    Priority.HIGH -> "High"
    Priority.CRITICAL -> "Critical"
}

@Composable
private fun DeadlineField(item: WorkItem, vm: BoardViewModel) {
    var picking by remember { mutableStateOf(false) }
    Field("Deadline") {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedButton(onClick = { picking = true }) { Text(item.deadline?.let { vm.describe(it) } ?: "Set a deadline") }
            if (item.deadline != null) TextButton(onClick = { vm.setDeadline(item.id, null) }) { Text("Clear") }
        }
    }
    if (picking) {
        val start = item.deadline ?: dev.todotracker.core.QuickCaptureParser.tomorrowMorning(Instant.now(), java.time.ZoneId.systemDefault()).plus(8, ChronoUnit.HOURS)
        DateTimePicker("Deadline", start, future = false, onPicked = { vm.setDeadline(item.id, it) }, onDismiss = { picking = false })
    }
}

/** A rule in words ("every weekday"); empty stops it repeating. Saved with Save (a rule it doesn't understand says why). */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun RepeatField(item: WorkItem, vm: BoardViewModel) {
    val current = item.repeat?.toString().orEmpty()
    var text by rememberSaveable(item.id, current) { mutableStateOf(current) }
    var choosing by remember { mutableStateOf(false) }
    Field("Repeat") {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.weight(1f)) {
                OutlinedTextField(
                    value = text, onValueChange = { text = it }, singleLine = true,
                    placeholder = { Text("Doesn't repeat (e.g. every weekday)") }, modifier = Modifier.fillMaxWidth(),
                    trailingIcon = { TextButton(onClick = { choosing = true }) { Text("▾") } },
                )
                DropdownMenu(expanded = choosing, onDismissRequest = { choosing = false }) {
                    REPEAT_CHOICES.forEach { rule -> DropdownMenuItem(text = { Text(rule) }, onClick = { choosing = false; text = rule; vm.setRepeat(item.id, rule) }) }
                }
            }
            if (text.trim() != current) TextButton(onClick = { vm.setRepeat(item.id, text) }) { Text("Save") }
            else if (current.isNotEmpty()) TextButton(onClick = { text = ""; vm.setRepeat(item.id, "") }) { Text("Stop") }
        }
        if (item.repeat != null) Text("Mark done moves it to its next time (its deadline and when it comes back move along).", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

@Composable
private fun GroupField(item: WorkItem, vm: BoardViewModel) {
    var open by remember { mutableStateOf(false) }
    Field("Group") {
        Box {
            OutlinedButton(onClick = { open = true }) { Text("${vm.groupName(item.groupId) ?: "—"} ▾") }
            DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
                vm.groups.forEach { (gid, name) ->
                    DropdownMenuItem(text = { Text(if (gid == item.groupId) "✓ $name" else name) }, onClick = { open = false; if (gid != item.groupId) vm.moveToGroup(item.id, gid) })
                }
            }
        }
    }
}

/** Subtasks (done or not, each opens), adding one, steps in order with time between, and rollout steps. */
@Composable
private fun Subtasks(item: WorkItem, vm: BoardViewModel, open: (UUID) -> Unit) {
    var subtask by rememberSaveable(item.id) { mutableStateOf("") }
    var steps by rememberSaveable(item.id) { mutableStateOf("") }
    var stepHours by rememberSaveable(item.id) { mutableStateOf("24") }
    val delayHours = item.stepDelay?.let { it.toMinutes() / 60.0 }
    var between by rememberSaveable(item.id, delayHours) { mutableStateOf(delayHours?.let { hours(it) }.orEmpty()) }
    var showSteps by rememberSaveable(item.id) { mutableStateOf(false) }
    Section(if (item.sequential) "Steps · in order" else "Subtasks") {
        item.children.forEach { child ->
            val locked = Agenda.stateOf(child, vm.now) == ItemState.LOCKED
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
                Checkbox(
                    checked = child.isDone,
                    onCheckedChange = { if (it) vm.complete(child.id) else vm.reopen(child.id) },
                    enabled = child.isDone || !locked,
                    modifier = Modifier.semantics { contentDescription = if (child.isDone) "Reopen ${child.title}" else "Mark ${child.title} done" },
                )
                Column(Modifier.weight(1f).clickable { open(child.id) }.padding(vertical = 6.dp)) {
                    Text(
                        titleWithIcon(child),
                        style = MaterialTheme.typography.bodyLarge.copy(textDecoration = if (child.isDone) TextDecoration.LineThrough else null),
                        color = if (child.isDone) MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.onSurface,
                    )
                    val meta = listOfNotNull(
                        if (locked) "after the step before" else null,
                        child.nextActionAt?.takeIf { it.isAfter(vm.now) }?.let { "back ${vm.describe(it)}" },
                        child.children.takeIf { it.isNotEmpty() }?.let { c -> "${c.count { it.isDone }}/${c.size} done" },
                    )
                    if (meta.isNotEmpty()) Text(meta.joinToString(" · "), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                Text("›", color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(horizontal = 8.dp))
            }
        }
        Row(verticalAlignment = Alignment.CenterVertically) {
            OutlinedTextField(value = subtask, onValueChange = { subtask = it }, placeholder = { Text("Add a subtask…") }, singleLine = true, modifier = Modifier.weight(1f))
            TextButton(onClick = { if (vm.addSubtask(item.id, subtask)) subtask = "" }, enabled = subtask.isNotBlank()) { Text("Add") }
        }
        TextButton(onClick = { showSteps = !showSteps }) { Text(if (showSteps) "Steps in order & rollout steps ▴" else "Steps in order & rollout steps ▾") }
        if (showSteps) {
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
                Text("Steps in order", modifier = Modifier.weight(1f))
                Switch(checked = item.sequential, onCheckedChange = { vm.setSequential(item.id, it) })
            }
            Row(verticalAlignment = Alignment.CenterVertically) {
                OutlinedTextField(
                    value = between, onValueChange = { between = it }, label = { Text("Hours between steps") }, singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal), modifier = Modifier.weight(1f),
                )
                TextButton(onClick = { vm.setStepDelay(item.id, between.replace(',', '.').toDoubleOrNull()) }, enabled = between != delayHours?.let { hours(it) }.orEmpty()) { Text("Save") }
            }
            OutlinedTextField(value = steps, onValueChange = { steps = it }, label = { Text("Rollout steps, one per line") }, minLines = 3, modifier = Modifier.fillMaxWidth())
            Row(verticalAlignment = Alignment.CenterVertically) {
                OutlinedTextField(
                    value = stepHours, onValueChange = { stepHours = it }, label = { Text("Hours between the new steps") }, singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal), modifier = Modifier.weight(1f),
                )
                TextButton(onClick = { if (vm.addSteps(item.id, steps, stepHours.replace(',', '.').toDoubleOrNull())) steps = "" }, enabled = steps.isNotBlank()) { Text("Add steps") }
            }
        }
    }
}

private fun hours(h: Double): String = if (h == Math.rint(h)) h.toLong().toString() else h.toString()

/** The reminders still to come or due (each can be dismissed), and a new one: when, and what it says. */
@Composable
private fun RemindersSection(item: WorkItem, vm: BoardViewModel) {
    var choosing by remember { mutableStateOf(false) }
    var picking by remember { mutableStateOf(false) }
    var message by rememberSaveable(item.id) { mutableStateOf("") }
    val ask = LocalAskToNotify.current
    Section("Reminders") {
        val pending = item.reminders.filter { it.dismissedAt == null }.sortedBy { it.dueAt }
        if (pending.isEmpty()) Text("None. A reminder brings the task to the top of Do now and tells you, even with the app closed.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        pending.forEach { r ->
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text("⏰ ${r.message}", style = MaterialTheme.typography.bodyMedium)
                    Text(if (r.isDue(vm.now)) "due" else vm.describe(r.dueAt), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                TextButton(onClick = { vm.dismissReminder(item.id, r.id) }) { Text("Dismiss") }
            }
        }
        OutlinedTextField(value = message, onValueChange = { message = it }, placeholder = { Text("Reminder message (optional)") }, singleLine = true, modifier = Modifier.fillMaxWidth())
        Box {
            OutlinedButton(onClick = { choosing = true }) { Text("Remind me…") }
            DropdownMenu(expanded = choosing, onDismissRequest = { choosing = false }) {
                vm.snoozeChoices().forEach { choice ->
                    DropdownMenuItem(
                        text = { Text(choice.label) },
                        trailingIcon = { Text(vm.describe(choice.at), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) },
                        onClick = { choosing = false; ask(); if (vm.addReminder(item.id, choice.at, message)) message = "" },
                    )
                }
                HorizontalDivider()
                DropdownMenuItem(text = { Text("Pick a time…") }, onClick = { choosing = false; picking = true })
            }
        }
    }
    if (picking) {
        val start = dev.todotracker.core.QuickCaptureParser.tomorrowMorning(Instant.now(), java.time.ZoneId.systemDefault())
        DateTimePicker("Remind me", start, future = true, onPicked = { ask(); if (vm.addReminder(item.id, it, message)) message = "" }, onDismiss = { picking = false })
    }
}
