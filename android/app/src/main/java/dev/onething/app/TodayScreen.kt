package dev.onething.app

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.IconButtonDefaults
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import dev.onething.core.Agenda
import dev.onething.core.AgendaEntry
import dev.onething.core.ItemState
import dev.onething.core.OverviewEntry
import dev.onething.core.Priority
import dev.onething.core.WorkItem
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.util.UUID

/** Asks (once) for what reminders need: notifications, and alarms on time. Called before a snooze, reminder or focus. */
val LocalAskToNotify = staticCompositionLocalOf<() -> Unit> { {} }

/** How wide Today must be for two columns: the one thing and Do now, then Workstreams, Coming up and Done today. */
val TODAY_TWO_COLUMNS: Dp = 840.dp

/**
 * Today: the one thing to do now (big, in the brand's colors), then the rest of Do now, what's coming up, what got done,
 * workstreams and notes. Wide enough (a tablet), it's two columns: what to do on the left, the rest on the right. The
 * app's shell (AppShell) draws the timers, the task's details, Settings, the break and Send feedback.
 */
@Composable
fun TodayScreen(vm: BoardViewModel, onSettings: () -> Unit, onFeedback: () -> Unit, onBusy: (Boolean) -> Unit = {}) {
    // Kept across rotation and theme changes (as ids and text).
    var noteFor by rememberSaveable { mutableStateOf<String?>(null) }
    var addingGroup by rememberSaveable { mutableStateOf(false) }
    var editingGroup by rememberSaveable { mutableStateOf<String?>(null) }
    var searching by rememberSaveable { mutableStateOf(false) }
    var showStreams by rememberSaveable { mutableStateOf(true) }
    var showNotes by rememberSaveable { mutableStateOf(true) }
    // Ctrl+F (a keyboard): the search opens.
    var searchesSeen by remember { mutableIntStateOf(vm.searchRequests) }
    LaunchedEffect(vm.searchRequests) {
        if (vm.searchRequests != searchesSeen) {
            searchesSeen = vm.searchRequests
            searching = true
        }
    }
    val askToNotify = LocalAskToNotify.current
    val startFocusOn: (UUID?) -> Unit = { id ->
        askToNotify()
        vm.startFocus(id)
    }
    val onNote: (UUID) -> Unit = { noteFor = it.toString() }
    // A dialog of Today's is open: a shake doesn't open Send feedback over it.
    androidx.compose.runtime.SideEffect { onBusy(noteFor != null || addingGroup || editingGroup != null) }
    BoxWithConstraints(Modifier.fillMaxSize()) {
        val twoColumns = maxWidth >= TODAY_TWO_COLUMNS
        // Short (a phone on its side): the top scrolls away with the list, so the list has room.
        val short = maxHeight < 520.dp && !twoColumns
        val gutter = if (twoColumns) 24.dp else 16.dp
        val top: @Composable () -> Unit = {
            Header(vm, onSettings = onSettings, onSearch = { searching = !searching; if (!searching) vm.search("") }, onFeedback = onFeedback)
            GroupTabs(vm, onAdd = { addingGroup = true }, onEdit = { editingGroup = it.toString() })
            Capture(vm)
            if (searching || vm.query.isNotEmpty()) SearchBox(vm, onClose = { searching = false; vm.search("") })
            vm.status?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(vertical = 4.dp)) }
        }
        Column(Modifier.fillMaxSize().imePadding().padding(horizontal = gutter)) {
            if (!short) top()
            val results = vm.searchResults
            val bottom = PaddingValues(top = 4.dp, bottom = 24.dp)
            when {
                results != null -> SearchResults(results, vm, if (short) top else null)
                twoColumns -> Row(Modifier.fillMaxSize(), horizontalArrangement = Arrangement.spacedBy(24.dp)) {
                    LazyColumn(Modifier.weight(1.25f).fillMaxHeight(), contentPadding = bottom, verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        doNow(vm, onNote, startFocusOn)
                    }
                    LazyColumn(Modifier.weight(1f).fillMaxHeight(), contentPadding = bottom, verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        aside(vm, showStreams, { showStreams = !showStreams }, showNotes, { showNotes = !showNotes })
                    }
                }
                else -> LazyColumn(Modifier.fillMaxSize(), contentPadding = bottom, verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    if (short) item(key = "top") { Column { top() } }
                    doNow(vm, onNote, startFocusOn)
                    aside(vm, showStreams, { showStreams = !showStreams }, showNotes, { showNotes = !showNotes })
                }
            }
        }
        noteFor?.let { id ->
            val uuid = UUID.fromString(id)
            TextDialog("Note on “${vm.titleOf(uuid).orEmpty()}”", "What happened, what's next…", onDone = { vm.addNote(uuid, it) }, onDismiss = { noteFor = null })
        }
        if (addingGroup) TextDialog("New group", "Name", onDone = { vm.addGroup(it) }, onDismiss = { addingGroup = false })
        editingGroup?.let { GroupDialog(UUID.fromString(it), vm, onDismiss = { editingGroup = null }) }
    }
}

/** The one thing to do now, then the rest of Do now. */
private fun LazyListScope.doNow(vm: BoardViewModel, onNote: (UUID) -> Unit, onFocus: (UUID?) -> Unit) {
    val focus = vm.dashboard.focus
    if (focus == null) {
        // Not while the tasks folder opens (a moment): "all clear" would flash by.
        if (vm.loaded) item(key = "clear") { AllClear() }
    } else {
        item(key = "focus") { FocusCard(focus, vm, onNote = { onNote(focus.item.id) }, onFocus = onFocus) }
    }
    val rest = vm.dashboard.now.drop(1)
    if (rest.isNotEmpty()) {
        item(key = "do-now") { SectionTitle("Do now", rest.size) }
        items(rest, key = { it.item.id }) { entry -> TaskRow(entry, vm, onNote = { onNote(entry.item.id) }, onFocus = onFocus) }
    }
}

/** What's beside it: workstreams, what's coming up, what got done today, and recent notes. */
private fun LazyListScope.aside(vm: BoardViewModel, showStreams: Boolean, toggleStreams: () -> Unit, showNotes: Boolean, toggleNotes: () -> Unit) {
    val streams = Agenda.workstreams(vm.dashboard)
    if (streams.isNotEmpty()) {
        item(key = "streams") { FoldTitle("Workstreams", streams.size, showStreams, toggleStreams) }
        if (showStreams) items(streams, key = { "s" + it.item.id }) { WorkstreamRow(it, vm) }
    }
    if (vm.dashboard.waiting.isNotEmpty()) {
        item(key = "coming") { SectionTitle("Coming up", vm.dashboard.waiting.size) }
        items(vm.dashboard.waiting, key = { "w" + it.item.id }) { ComingUpRow(it, vm) }
    }
    if (vm.doneToday.isNotEmpty()) {
        item(key = "done") { SectionTitle("Done today", vm.doneToday.size) }
        Agenda.cheer(vm.doneToday.size)?.let { cheer -> item(key = "cheer") { CheerLine(cheer) } }
        items(vm.doneToday, key = { "d" + it.id }) { DoneRow(it, vm) }
    }
    val notes = vm.dashboard.recentNotes
    if (notes.isNotEmpty()) {
        item(key = "notes") { FoldTitle("Recent notes", notes.size, showNotes, toggleNotes) }
        if (showNotes) items(notes, key = { "n" + it.note.id }) { n ->
            Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).clickable { vm.openTask(n.item.id) }.padding(horizontal = 4.dp, vertical = 6.dp)) {
                Text(n.note.text, style = MaterialTheme.typography.bodyMedium, maxLines = 4)
                Text("${n.note.author.displayName} · ${n.item.title} · ${ago(n.note.at, vm.now)}", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1)
            }
        }
    }
}

/** Nothing to do now: said calmly, with a way to add something. */
@Composable
private fun AllClear() {
    Column(
        Modifier.fillMaxWidth().padding(top = 8.dp).border(1.dp, MaterialTheme.colorScheme.outlineVariant, RoundedCornerShape(24.dp)).padding(vertical = 36.dp, horizontal = 20.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        Text("🌿", style = MaterialTheme.typography.displaySmall)
        Text("Nothing is due. Nice.", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
        Text("Add something above, or take a break.", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

@Composable
private fun Header(vm: BoardViewModel, onSettings: () -> Unit, onSearch: () -> Unit, onFeedback: () -> Unit) {
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
            vm.energyToday?.let { energy ->
                Text(energy, style = MaterialTheme.typography.bodySmall, color = if (vm.energyOver) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        IconButton(onClick = onSearch) { Icon(Icons.Filled.Search, contentDescription = "Search") }
        IconButton(onClick = onSettings) { Icon(Icons.Filled.Settings, contentDescription = "Settings") }
        // More: what isn't used every day.
        Box {
            var more by remember { mutableStateOf(false) }
            IconButton(onClick = { more = true }) { Icon(Icons.Filled.MoreVert, contentDescription = "More") }
            DropdownMenu(expanded = more, onDismissRequest = { more = false }) {
                DropdownMenuItem(text = { Text("Settings") }, onClick = { more = false; onSettings() })
                DropdownMenuItem(text = { Text("Send feedback…") }, onClick = { more = false; onFeedback() })
            }
        }
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
private fun SearchResults(results: List<WorkItem>, vm: BoardViewModel, top: (@Composable () -> Unit)? = null) {
    LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.fillMaxSize()) {
        top?.let { item(key = "top") { Column { it() } } }
        item { SectionTitle("Found", results.size) }
        if (results.isEmpty()) item { Text("Nothing matches. Done tasks are found with is:done, archived ones with is:archived.", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant) }
        items(results, key = { "f" + it.id }) { item ->
            Card(Modifier.fillMaxWidth().clickable { vm.openTask(item.id) }) {
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
    Text(
        "$title · $count", style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold,
        color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 14.dp, bottom = 2.dp),
    )
}

/** A section title that folds its section away (and back). */
@Composable
private fun FoldTitle(title: String, count: Int, open: Boolean, onToggle: () -> Unit) {
    Text(
        "$title · $count ${if (open) "▴" else "▾"}",
        style = MaterialTheme.typography.titleSmall,
        fontWeight = FontWeight.SemiBold,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = Modifier.fillMaxWidth().clickable(onClick = onToggle).padding(top = 14.dp, bottom = 2.dp),
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
private fun Meta(entry: AgendaEntry, now: Instant, color: Color = MaterialTheme.colorScheme.onSurfaceVariant) {
    val parts = listOfNotNull(
        entry.breadcrumb.takeIf { it.isNotEmpty() }?.joinToString(" › "),
        entry.stepLabel,
        priorityLabel(entry.effectivePriority),
        if (entry.isOverdue) "overdue" else entry.item.deadline?.let { "due ${relative(it, now)}" },
        entry.dueReminder?.let { "⏰ ${it.message}" },
        entry.item.energy?.let { "⚡".repeat(it) },
    )
    if (parts.isNotEmpty()) Text(parts.joinToString(" · "), style = MaterialTheme.typography.bodySmall, color = color, maxLines = 2, overflow = TextOverflow.Ellipsis)
}

/** A card's notes, newest first: the text, then who wrote it and when. */
@Composable
internal fun CardNotes(item: WorkItem, color: Color = Color.Unspecified, quiet: Color = MaterialTheme.colorScheme.onSurfaceVariant) {
    if (item.notes.isEmpty()) return
    val now = Instant.now()
    Column(verticalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.semantics { contentDescription = "Notes" }) {
        for (note in item.notes.sortedByDescending { it.at }) {
            Column(Modifier.padding(start = 8.dp)) {
                Text(note.text, style = MaterialTheme.typography.bodyMedium, color = color)
                Text("${note.author.displayName} · ${ago(note.at, now)}", style = MaterialTheme.typography.labelSmall, color = quiet)
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

/**
 * The one thing to do now, in the brand's gradient so it stands out: its title, what to know, its notes, and four
 * buttons (Mark done, Later, Focus, More). The rarer actions are under More.
 */
@Composable
private fun FocusCard(entry: AgendaEntry, vm: BoardViewModel, onNote: () -> Unit, onFocus: (UUID?) -> Unit) {
    val onBrand = Color.White
    val soft = Color.White.copy(alpha = 0.82f)
    var more by remember { mutableStateOf(false) }
    var later by remember { mutableStateOf(false) }
    Column(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(24.dp)).background(Brand.gradient)
            .rowGestures(onClick = { vm.openTask(entry.item.id) }, onMenu = { more = true }, hover = false)
            .padding(horizontal = 20.dp, vertical = 18.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Text("Do this now", style = MaterialTheme.typography.labelLarge, color = soft)
        Text(titleWithIcon(entry.item), style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.SemiBold, color = onBrand)
        Meta(entry, vm.now, soft)
        // The task you're on: every note, newest first.
        CardNotes(entry.item, onBrand, soft)
        Row(Modifier.padding(top = 6.dp), horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
            // "Mark done", not "Done": a bare "Done" reads as "close this".
            if (!entry.item.hasOpenChildren) {
                Button(
                    onClick = { vm.complete(entry.item.id) },
                    colors = ButtonDefaults.buttonColors(containerColor = Color.White, contentColor = Brand.indigo),
                ) { Text("Mark done", fontWeight = FontWeight.SemiBold) }
            } else {
                Button(onClick = { vm.openTask(entry.item.id) }, colors = ButtonDefaults.buttonColors(containerColor = Color.White, contentColor = Brand.indigo)) { Text("Open") }
            }
            Box {
                OutlinedButton(
                    onClick = { later = true },
                    colors = ButtonDefaults.outlinedButtonColors(contentColor = onBrand),
                    border = androidx.compose.foundation.BorderStroke(1.dp, Color.White.copy(alpha = 0.6f)),
                ) { Text("Later") }
                LaterMenu(entry.item.id, vm, later) { later = false }
            }
            TextButton(onClick = { onFocus(entry.item.id) }, colors = ButtonDefaults.textButtonColors(contentColor = onBrand)) { Text("Focus") }
            Spacer(Modifier.weight(1f))
            Box {
                IconButton(onClick = { more = true }, colors = IconButtonDefaults.iconButtonColors(contentColor = onBrand)) {
                    Icon(Icons.Filled.MoreVert, contentDescription = "More")
                }
                TaskMoreMenu(entry, vm, more, onDismiss = { more = false }, onNote = onNote, onFocus = null, hero = true)
            }
        }
    }
}

/**
 * A task in Do now: a circle to finish it, its title and what to know about it, then Later and More. Swipe right to
 * mark it done, left for Later; touch and hold (or right-click) for More. Tap it to open it.
 */
@Composable
private fun TaskRow(entry: AgendaEntry, vm: BoardViewModel, onNote: () -> Unit, onFocus: (UUID?) -> Unit) {
    var more by remember { mutableStateOf(false) }
    var later by remember { mutableStateOf(false) }
    val item = entry.item
    SwipeActions(
        onDone = if (item.hasOpenChildren) null else ({ vm.complete(item.id) }),
        onLater = { later = true },
    ) {
        Surface(
            shape = RoundedCornerShape(16.dp),
            color = rowColor(),
            // The task open beside the list (a tablet) is outlined.
            border = if (vm.openTaskId == item.id) androidx.compose.foundation.BorderStroke(2.dp, MaterialTheme.colorScheme.primary)
            else androidx.compose.foundation.BorderStroke(1.dp, MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.7f)),
            modifier = Modifier.fillMaxWidth(),
        ) {
            Row(
                Modifier.rowGestures(onClick = { vm.openTask(item.id) }, onMenu = { more = true }).padding(start = 4.dp, end = 2.dp, top = 6.dp, bottom = 6.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                DoneCircle(entry, onDone = { if (item.hasOpenChildren) vm.openTask(item.id) else vm.complete(item.id) })
                Column(Modifier.weight(1f).padding(vertical = 4.dp), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    Text(titleWithIcon(item), style = MaterialTheme.typography.bodyLarge, fontWeight = FontWeight.Medium, maxLines = 3, overflow = TextOverflow.Ellipsis)
                    Meta(entry, vm.now)
                    if (item.notes.isNotEmpty()) {
                        var open by rememberSaveable(item.id) { mutableStateOf(false) }
                        val count = item.notes.size
                        Text(
                            if (open) "Hide notes ▴" else if (count == 1) "1 note ▾" else "$count notes ▾",
                            style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.primary,
                            modifier = Modifier.clip(RoundedCornerShape(6.dp)).clickable { open = !open }.padding(vertical = 4.dp),
                        )
                        if (open) CardNotes(item)
                    }
                }
                if (vm.timer?.item?.id == item.id) {
                    Box(Modifier.padding(horizontal = 4.dp).size(8.dp).background(ViewColors.danger, CircleShape).semantics { contentDescription = "Being timed" })
                }
                Box {
                    IconButton(onClick = { later = true }) {
                        Icon(ViewIcons.later, contentDescription = "Later", tint = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.size(20.dp))
                    }
                    LaterMenu(item.id, vm, later) { later = false }
                }
                Box {
                    IconButton(onClick = { more = true }) {
                        Icon(Icons.Filled.MoreVert, contentDescription = "More", tint = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    TaskMoreMenu(entry, vm, more, onDismiss = { more = false }, onNote = onNote, onFocus = onFocus, hero = false)
                }
            }
        }
    }
}

/** A row's color: white on the light theme, a step up from the background on the dark one. */
@Composable
private fun rowColor(): Color =
    if (MaterialTheme.colorScheme.background.luminance() < 0.5f) MaterialTheme.colorScheme.surfaceContainerLow else MaterialTheme.colorScheme.surfaceContainerLowest

/** The circle in front of a task: tap it to mark it done (in the task's priority color). */
@Composable
private fun DoneCircle(entry: AgendaEntry, onDone: () -> Unit) {
    val color = ViewColors.priority(entry.effectivePriority)
    IconButton(onClick = onDone, modifier = Modifier.semantics { contentDescription = "Mark “${entry.item.title}” done" }) {
        Box(Modifier.size(22.dp).border(2.dp, color, CircleShape).padding(3.dp).background(color.copy(alpha = 0.12f), CircleShape))
    }
}

/**
 * More (⋯, a long press or a right-click): the rarer actions, so the buttons stay few. On the one thing to do now
 * ([hero]) it has Skip for now; on a row, Focus.
 */
@Composable
private fun TaskMoreMenu(entry: AgendaEntry, vm: BoardViewModel, expanded: Boolean, onDismiss: () -> Unit, onNote: () -> Unit, onFocus: ((UUID?) -> Unit)?, hero: Boolean) {
    val context = androidx.compose.ui.platform.LocalContext.current
    val item = entry.item
    DropdownMenu(expanded = expanded, onDismissRequest = onDismiss) {
        // The task's details: edit it, its subtasks, reminders, every note.
        DropdownMenuItem(text = { Text("Open details") }, onClick = { onDismiss(); vm.openTask(item.id) })
        DropdownMenuItem(text = { Text("Add a note…") }, onClick = { onDismiss(); onNote() })
        onFocus?.let { focus -> DropdownMenuItem(text = { Text("Focus on it") }, onClick = { onDismiss(); focus(item.id) }) }
        val timing = vm.timer?.item?.id == item.id
        DropdownMenuItem(text = { Text(if (timing) "Stop the timer" else "Start the timer") }, onClick = { onDismiss(); if (timing) vm.stopTimer() else vm.toggleTimer(item.id) })
        // Do something else first: this one moves down the list.
        if (hero && vm.dashboard.now.size > 1) DropdownMenuItem(text = { Text("Skip for now") }, onClick = { onDismiss(); vm.skip(item.id) })
        entry.dueReminder?.let { r ->
            DropdownMenuItem(text = { Text("Dismiss reminder") }, onClick = { onDismiss(); vm.dismissReminder(item.id, r.id) })
        }
        HorizontalDivider()
        DropdownMenuItem(text = { Text("Copy link") }, onClick = {
            onDismiss()
            context.getSystemService(android.content.ClipboardManager::class.java)
                ?.setPrimaryClip(android.content.ClipData.newPlainText("Task link", dev.onething.core.TaskLinks.forTask(item.id)))
            vm.status = "Link copied: it opens this task in the app"
        })
    }
}

/** Later as a button (a task's details): the menu opens under it. */
@Composable
internal fun LaterButton(id: UUID, vm: BoardViewModel) {
    var open by remember { mutableStateOf(false) }
    Box {
        OutlinedButton(onClick = { open = true }) { Text("Later") }
        LaterMenu(id, vm, open) { open = false }
    }
}

/** Later: the quick choices (each says when), a date and time, or after another task. Each can be undone. */
@Composable
internal fun LaterMenu(id: UUID, vm: BoardViewModel, expanded: Boolean, onDismiss: () -> Unit) {
    var pickingTask by remember { mutableStateOf(false) }
    var pickingTime by remember { mutableStateOf(false) }
    val ask = LocalAskToNotify.current
    DropdownMenu(expanded = expanded, onDismissRequest = onDismiss) {
        vm.snoozeChoices().forEach { choice ->
            DropdownMenuItem(
                text = { Text(choice.label) },
                trailingIcon = { Text(vm.describe(choice.at), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) },
                onClick = { onDismiss(); ask(); vm.snoozeUntil(id, choice.at) },
            )
        }
        HorizontalDivider()
        DropdownMenuItem(text = { Text("Pick a time…") }, onClick = { onDismiss(); pickingTime = true })
        DropdownMenuItem(text = { Text("After another task…") }, onClick = { onDismiss(); pickingTask = true })
    }
    if (pickingTime) {
        val start = dev.onething.core.QuickCaptureParser.tomorrowMorning(Instant.now(), ZoneId.systemDefault())
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

/** A task put off: its title, when it comes back (or what it waits for), and Do now to bring it back. */
@Composable
private fun ComingUpRow(entry: AgendaEntry, vm: BoardViewModel) {
    Row(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(14.dp)).background(MaterialTheme.colorScheme.surfaceContainerLow)
            .rowGestures(onClick = { vm.openTask(entry.item.id) }, onMenu = { vm.openTask(entry.item.id) })
            .padding(start = 14.dp, end = 4.dp, top = 6.dp, bottom = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(ViewIcons.later, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.size(16.dp))
        Column(Modifier.weight(1f).padding(start = 12.dp)) {
            Text(titleWithIcon(entry.item), style = MaterialTheme.typography.bodyMedium, maxLines = 2, overflow = TextOverflow.Ellipsis)
            val why = listOfNotNull(entry.wakeAt?.let { "back ${relative(it, vm.now)}" }, entry.waitingFor?.let { "after “${it.title}”" }).joinToString(" · ")
            if (why.isNotEmpty()) Text(why, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        TextButton(onClick = { vm.bringBack(entry.item.id) }) { Text("Do now") }
    }
}

/** A task with subtasks: how far along it is, what's to do now or waiting (tap: its details). */
@Composable
private fun WorkstreamRow(entry: OverviewEntry, vm: BoardViewModel) {
    Surface(
        shape = RoundedCornerShape(16.dp), color = MaterialTheme.colorScheme.surfaceContainerLow,
        modifier = Modifier.fillMaxWidth().clip(RoundedCornerShape(16.dp)).rowGestures(onClick = { vm.openTask(entry.item.id) }, onMenu = { vm.openTask(entry.item.id) }),
    ) {
        Column(Modifier.padding(horizontal = 14.dp, vertical = 12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Text(titleWithIcon(entry.item), style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold)
            LinearProgressIndicator(
                progress = { if (entry.totalLeaves == 0) 0f else entry.doneLeaves.toFloat() / entry.totalLeaves },
                modifier = Modifier.fillMaxWidth().clip(RoundedCornerShape(50)),
                color = Brand.violet,
                trackColor = MaterialTheme.colorScheme.surfaceContainerHighest,
                drawStopIndicator = {},
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
        Column(Modifier.weight(1f).clickable { vm.openTask(item.id) }) {
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
