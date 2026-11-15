package dev.onething.app

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.draganddrop.dragAndDropTarget
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draganddrop.DragAndDropEvent
import androidx.compose.ui.draganddrop.DragAndDropTarget
import androidx.compose.ui.draganddrop.toAndroidDragEvent
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.layout.boundsInRoot
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import dev.onething.core.OutlineMoves
import dev.onething.core.OutlineRow
import dev.onething.core.RelativeTime
import dev.onething.core.Stage
import dev.onething.core.WorkItem
import dev.onething.core.durationWords
import java.time.Duration
import java.util.UUID

/** Where a new task is being typed: right after a task (as its sibling), first under one, or at the top. */
private data class AddPlace(val after: UUID?, val parent: UUID?)

/**
 * Tasks (the web's outline.js, the iPhone's TasksOutlineView): every task of the group as an outline. Tap a task to
 * pick it, tap it again to rename it (Done on the keyboard saves it and starts the next one below); ⋯ on a row moves it
 * up, down, into the task above or out one level, adds a subtask or times it; hold a row and drop it on the top of
 * another (before it), its middle (inside it) or its bottom (after it).
 */
@Composable
fun TasksScreen(vm: BoardViewModel, open: (UUID) -> Unit) {
    @Suppress("UNUSED_VARIABLE") val redraw = vm.version
    val showDone = vm.outlineShowDone
    val expanded = vm.outlineExpanded
    val roots = vm.scope.matchingRoots
    val rows = OutlineRow.flatten(roots, expanded, showDone)
    var selectedText by rememberSaveable { mutableStateOf<String?>(null) }
    val selected = selectedText?.let { runCatching { UUID.fromString(it) }.getOrNull() }
    var editingText by rememberSaveable { mutableStateOf<String?>(null) }
    var adding by remember { mutableStateOf<AddPlace?>(null) }
    var draft by rememberSaveable { mutableStateOf("") }

    fun finishRename() {
        val id = editingText?.let { UUID.fromString(it) } ?: return
        vm.rename(id, draft)
        editingText = null
    }

    fun startAdding(place: AddPlace) {
        finishRename()
        draft = ""
        adding = place
    }

    fun addTyped() {
        val place = adding ?: return
        if (draft.isBlank()) {
            adding = null
            return
        }
        place.parent?.let { vm.setExpanded(it, true) }
        val id = vm.addTask(draft, place.parent, place.after, if (place.parent == null) Stage.NEXT else null) ?: return
        draft = ""
        selectedText = id.toString()
        // The box moves below the new task, for the next one (empty: done adding).
        adding = AddPlace(after = id, parent = place.parent)
    }

    Column(Modifier.fillMaxSize()) {
        ViewHeader("Tasks", vm, onAdd = { startAdding(AddPlace(after = selected, parent = selected?.let { vm.itemOf(it)?.parent?.id })) })
        LazyRow(Modifier.padding(horizontal = 12.dp), horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
            item { FilterChip(selected = showDone, onClick = { vm.chooseOutlineShowDone(!showDone) }, label = { Text("Show finished") }) }
            item {
                TextButton(onClick = {
                    vm.setExpanded(expanded + roots.flatMap { it.selfAndDescendants }.filter { it.children.isNotEmpty() }.map { it.id })
                }) { Text("Expand all") }
            }
            item { TextButton(onClick = { vm.setExpanded(emptySet()) }) { Text("Collapse all") } }
        }
        LazyColumn(Modifier.fillMaxSize().imePadding(), contentPadding = androidx.compose.foundation.layout.PaddingValues(bottom = 24.dp)) {
            if (adding == AddPlace(null, null)) item(key = "new-top") { NewRow(0, draft, { draft = it }, ::addTyped) { adding = null } }
            if (rows.isEmpty() && adding == null) {
                item { Text("No tasks here yet. Tap + to add one.", color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(16.dp)) }
            }
            val spot = adding?.after?.let { after ->
                val at = rows.indexOfFirst { it.item.id == after }
                if (at < 0) null else {
                    var end = at + 1
                    while (end < rows.size && rows[end].depth > rows[at].depth) end++
                    (end - 1) to rows[at].depth
                }
            }
            rows.forEachIndexed { index, row ->
                item(key = row.item.id) {
                    OutlineRowView(
                        vm, row, showDone,
                        selected = selected == row.item.id,
                        editing = editingText == row.item.id.toString(),
                        draft = draft,
                        onDraft = { draft = it },
                        onTapTitle = {
                            if (selected == row.item.id && !row.item.isDone) {
                                finishRename()
                                adding = null
                                draft = row.item.title
                                editingText = row.item.id.toString()
                            } else {
                                finishRename()
                                selectedText = row.item.id.toString()
                            }
                        },
                        onRenamed = {
                            // Done on the keyboard: saved, and the next task is typed below it.
                            finishRename()
                            startAdding(AddPlace(after = row.item.id, parent = row.item.parent?.id))
                        },
                        onAddSubtask = {
                            vm.setExpanded(row.item.id, true)
                            startAdding(AddPlace(after = null, parent = row.item.id))
                        },
                        open = open,
                    )
                }
                if (adding?.parent == row.item.id && adding?.after == null) {
                    item(key = "new-in-${row.item.id}") { NewRow(row.depth + 1, draft, { draft = it }, ::addTyped) { adding = null } }
                }
                if (spot != null && spot.first == index) {
                    item(key = "new-after") { NewRow(spot.second, draft, { draft = it }, ::addTyped) { adding = null } }
                }
            }
        }
    }
}

/** A row: open or close its subtasks, finish it, its title (rename in place), what it says about the task, and ⋯. */
@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun OutlineRowView(
    vm: BoardViewModel,
    row: OutlineRow,
    showDone: Boolean,
    selected: Boolean,
    editing: Boolean,
    draft: String,
    onDraft: (String) -> Unit,
    onTapTitle: () -> Unit,
    onRenamed: () -> Unit,
    onAddSubtask: () -> Unit,
    open: (UUID) -> Unit,
) {
    val item = row.item
    val timing = vm.timer?.item?.id == item.id
    // Where a drop would land on this row: its top quarter before it, its bottom quarter after it, the middle inside it.
    var bounds by remember { mutableStateOf(Rect.Zero) }
    var zone by remember { mutableStateOf<OutlineMoves.Zone?>(null) }
    fun zoneOf(event: DragAndDropEvent): OutlineMoves.Zone {
        val y = event.toAndroidDragEvent().y - bounds.top
        val h = bounds.height.coerceAtLeast(1f)
        return if (y < h * 0.25f) OutlineMoves.Zone.BEFORE else if (y > h * 0.75f) OutlineMoves.Zone.AFTER else OutlineMoves.Zone.INSIDE
    }
    val target = remember(item.id) {
        object : DragAndDropTarget {
            override fun onDrop(event: DragAndDropEvent): Boolean {
                val z = zoneOf(event)
                zone = null
                val id = TaskDrag.id(event) ?: return false
                val ok = vm.drop(id, item.id, z)
                if (ok && z == OutlineMoves.Zone.INSIDE) vm.setExpanded(item.id, true)
                return ok
            }

            override fun onMoved(event: DragAndDropEvent) { zone = zoneOf(event) }
            override fun onExited(event: DragAndDropEvent) { zone = null }
            override fun onEnded(event: DragAndDropEvent) { zone = null }
        }
    }
    val accent = ViewColors.accent
    Column(
        Modifier.fillMaxWidth()
            .onGloballyPositioned { bounds = it.boundsInRoot() }
            .dragAndDropTarget(shouldStartDragAndDrop = TaskDrag::isTask, target = target)
            .background(
                when {
                    zone == OutlineMoves.Zone.INSIDE -> accent.copy(alpha = 0.16f)
                    selected -> accent.copy(alpha = 0.08f)
                    else -> androidx.compose.ui.graphics.Color.Transparent
                },
            ),
    ) {
        if (zone == OutlineMoves.Zone.BEFORE) Box(Modifier.fillMaxWidth().height(2.dp).background(accent))
        Row(
            Modifier.fillMaxWidth().heightIn(min = 44.dp).padding(start = (8 + row.depth * 20).dp, end = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                if (!row.hasChildren) "" else if (row.expanded) "▾" else "▸",
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                style = MaterialTheme.typography.titleMedium,
                textAlign = androidx.compose.ui.text.style.TextAlign.Center,
                modifier = Modifier.width(28.dp).heightIn(min = 40.dp).padding(top = 8.dp)
                    .clickable(enabled = row.hasChildren) { vm.setExpanded(item.id, !row.expanded) }
                    .semantics { contentDescription = if (row.expanded) "Hide subtasks of ${item.title}" else "Show subtasks of ${item.title}" },
            )
            Text(
                if (item.isDone) "✓" else "○",
                color = if (item.isDone) ViewColors.done else ViewColors.priority(item.priority),
                style = MaterialTheme.typography.headlineSmall,
                modifier = Modifier.width(32.dp).clickable { vm.toggleDone(item.id) }
                    .semantics { contentDescription = if (item.isDone) "Reopen ${item.title}" else "Complete ${item.title}" },
            )
            Column(Modifier.weight(1f).padding(vertical = 6.dp)) {
                if (editing) {
                    InlineField(draft, onDraft, onDone = onRenamed)
                } else {
                    Text(
                        titleWithIcon(item),
                        style = MaterialTheme.typography.bodyLarge.copy(textDecoration = if (item.isDone) TextDecoration.LineThrough else null),
                        color = if (item.isDone) MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.onSurface,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.fillMaxWidth().taskDragSource(item.id, onTapTitle),
                    )
                }
                Facts(vm, item, timing)
            }
            RowMenu(vm, item, showDone, timing, onAddSubtask, open)
        }
        if (zone == OutlineMoves.Zone.AFTER) Box(Modifier.fillMaxWidth().height(2.dp).background(accent))
        HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.5f))
    }
}

/** What a row says about its task: labels and tags, deadline, steps done, time spent, notes. */
@Composable
private fun Facts(vm: BoardViewModel, item: WorkItem, timing: Boolean) {
    val parts = mutableListOf<String>()
    item.deadline?.takeIf { !item.isDone }?.let { parts.add("📅 ${RelativeTime.format(it, vm.now)}") }
    if (item.children.isNotEmpty()) {
        val all = item.selfAndDescendants.drop(1)
        parts.add("${all.count { it.isDone }}/${all.size}")
    }
    val spent = item.timeSpent(vm.now)
    if (spent >= Duration.ofMinutes(1) || timing) parts.add("⏱ ${durationWords(spent)}")
    if (item.notes.isNotEmpty()) parts.add("📝 ${item.notes.size}")
    if (item.labels.isNotEmpty() || item.tags.isNotEmpty()) TaskTags(item, vm, maxTags = 2)
    if (parts.isNotEmpty()) {
        val overdue = item.deadline?.let { !item.isDone && it.isBefore(vm.now) } == true
        Text(parts.joinToString("  ·  "), style = MaterialTheme.typography.labelSmall, color = if (overdue) ViewColors.danger else if (timing) ViewColors.accent else MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1)
    }
}

/** ⋯ on a row: time it, add a subtask, open it, move it. */
@Composable
private fun RowMenu(vm: BoardViewModel, item: WorkItem, showDone: Boolean, timing: Boolean, onAddSubtask: () -> Unit, open: (UUID) -> Unit) {
    var menu by remember { mutableStateOf(false) }
    var groups by remember { mutableStateOf(false) }
    Box {
        TextButton(onClick = { menu = true }, modifier = Modifier.semantics { contentDescription = "Actions for ${item.title}" }) { Text("⋯") }
        DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
            DropdownMenuItem(text = { Text("Open details") }, onClick = { menu = false; open(item.id) })
            DropdownMenuItem(text = { Text("Add a subtask") }, onClick = { menu = false; onAddSubtask() })
            if (!item.isDone) DropdownMenuItem(text = { Text(if (timing) "Stop the timer" else "Start the timer") }, onClick = { menu = false; vm.toggleTimer(item.id) })
            HorizontalDivider()
            DropdownMenuItem(text = { Text("Up") }, onClick = { menu = false; vm.step(item.id, -1, showDone) })
            DropdownMenuItem(text = { Text("Down") }, onClick = { menu = false; vm.step(item.id, 1, showDone) })
            DropdownMenuItem(
                text = { Text("Into the task above") },
                enabled = vm.canIndent(item.id, showDone),
                onClick = {
                    menu = false
                    vm.scope.indent(item.id, showDone)?.moves?.firstOrNull()?.let { m -> (m as? dev.onething.core.BoardMove.Move)?.parentId?.let { vm.setExpanded(it, true) } }
                    vm.indent(item.id, showDone)
                },
            )
            DropdownMenuItem(text = { Text("Out one level") }, enabled = vm.canOutdent(item.id), onClick = { menu = false; vm.outdent(item.id) })
            if (vm.groups.size > 1) DropdownMenuItem(text = { Text("Move to group…") }, onClick = { menu = false; groups = true })
        }
        DropdownMenu(expanded = groups, onDismissRequest = { groups = false }) {
            vm.groups.filter { it.first != item.groupId || item.parent != null }.forEach { (id, name) ->
                DropdownMenuItem(text = { Text(name) }, onClick = { groups = false; vm.moveToGroupAnywhere(item.id, id) })
            }
        }
    }
}

/** Where a new task is typed: Done on the keyboard adds it (and the next goes below); empty closes it. */
@Composable
private fun NewRow(depth: Int, text: String, onText: (String) -> Unit, onDone: () -> Unit, onCancel: () -> Unit) {
    Row(Modifier.fillMaxWidth().heightIn(min = 44.dp).padding(start = (8 + depth * 20 + 28).dp, end = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        Text("○", color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.5f), style = MaterialTheme.typography.titleMedium, modifier = Modifier.width(32.dp))
        Box(Modifier.weight(1f)) { InlineField(text, onText, onDone = onDone, placeholder = "New task") }
        TextButton(onClick = onCancel) { Text("Cancel") }
    }
}

/** A title typed in place (it takes the keyboard at once). */
@Composable
private fun InlineField(text: String, onText: (String) -> Unit, onDone: () -> Unit, placeholder: String = "Title") {
    val focus = remember { FocusRequester() }
    BasicTextField(
        value = text,
        onValueChange = onText,
        singleLine = true,
        textStyle = MaterialTheme.typography.bodyLarge.copy(color = MaterialTheme.colorScheme.onSurface),
        cursorBrush = SolidColor(ViewColors.accent),
        keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
        keyboardActions = KeyboardActions(onDone = { onDone() }),
        decorationBox = { inner ->
            Box(Modifier.fillMaxWidth().background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(6.dp)).padding(horizontal = 8.dp, vertical = 6.dp)) {
                if (text.isEmpty()) Text(placeholder, style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
                inner()
            }
        },
        modifier = Modifier.fillMaxWidth().focusRequester(focus),
    )
    LaunchedEffect(Unit) { focus.requestFocus() }
    Spacer(Modifier.height(0.dp))
}
