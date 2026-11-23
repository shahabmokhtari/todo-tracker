package dev.onething.app

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.draganddrop.dragAndDropTarget
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
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
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.path
import androidx.compose.ui.layout.boundsInRoot
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import dev.onething.core.OutlineLevel
import dev.onething.core.OutlineMoves
import dev.onething.core.OutlinePartKind
import dev.onething.core.OutlinePathPart
import dev.onething.core.OutlineRow
import dev.onething.core.OutlineShow
import dev.onething.core.RelativeTime
import dev.onething.core.Stage
import dev.onething.core.WorkItem
import dev.onething.core.durationWords
import java.time.Duration
import java.util.UUID

/** Where a new task is being typed: right after a task (as its sibling), first under one, first in a group, or at the top. */
private data class AddPlace(val after: UUID?, val parent: UUID?, val group: UUID? = null)

/** What the list draws, in order: a row of the tree, or the box a new task is typed in. */
private sealed interface Entry {
    val key: Any

    data class Line(val row: OutlineRow) : Entry {
        override val key: Any get() = row.id
    }

    data class Editor(val depth: Int, override val key: Any) : Entry
}

/** A row's indent: each level of the tree, with a guide line. */
private const val INDENT = 20
private const val START = 8

/**
 * Tasks (docs/design/tasks-tree.md; the web's outline.js, the iPhone's TasksOutlineView): the tree of tasks. Under All
 * groups the groups are its top level (each stays at the top while its tasks scroll under it). Parents say how many of
 * the tasks under them are done; ⤢ zooms into a branch (the path above goes back out, and so does Back); Show narrows
 * the tree to what can be done now or what's waiting; Open to opens it to a depth. Tap a task to pick it, tap it again to
 * rename it (Done on the keyboard saves it and starts the next one below); ⋯ on a row moves it, adds a subtask or times
 * it; hold a row and drop it on the top of another (before it), its middle (inside it), its bottom (after it), or on a
 * group (first in that group).
 */
@OptIn(ExperimentalFoundationApi::class)
@Composable
fun TasksScreen(vm: BoardViewModel, open: (UUID) -> Unit) {
    @Suppress("UNUSED_VARIABLE") val redraw = vm.version
    val asked = vm.outlineOptions
    // A zoom that can't be shown any more (finished, moved, gone): out to its nearest parent that can. A search only
    // narrows for now (the zoom is kept, for when it's cleared).
    val zoom = vm.outlineZoom(asked)
    val options = asked.copy(zoomId = zoom)
    if (zoom != asked.zoomId && !asked.searching && vm.loaded) LaunchedEffect(zoom, asked.zoomId) { vm.zoomOutline(zoom) }
    val rows = vm.outlineRows(options)
    val path = vm.outlinePath(options)
    val allGroups = vm.selectedGroup == null && zoom == null
    BackHandler(enabled = zoom != null) { vm.leaveOutlineZoom() }

    var selectedText by rememberSaveable { mutableStateOf<String?>(null) }
    val selected = selectedText?.let { runCatching { UUID.fromString(it) }.getOrNull() }
    var editingText by rememberSaveable { mutableStateOf<String?>(null) }
    var adding by remember { mutableStateOf<AddPlace?>(null) }
    var draft by rememberSaveable { mutableStateOf("") }
    val list = rememberLazyListState()

    fun finishRename() {
        val id = editingText?.let { UUID.fromString(it) } ?: return
        vm.rename(id, draft)
        editingText = null
    }

    fun startAdding(place: AddPlace) {
        finishRename()
        draft = ""
        place.group?.let { vm.setGroupOpen(it, true) }
        adding = place
    }

    fun addTyped() {
        val place = adding ?: return
        if (draft.isBlank()) {
            adding = null
            return
        }
        place.parent?.takeIf { it != zoom }?.let { vm.setExpanded(it, true) }
        val topLevel = place.parent == null
        val id = vm.addTask(
            draft, place.parent, place.after, if (topLevel) Stage.NEXT else null,
            groupId = place.group ?: vm.selectedGroup, first = topLevel && place.after == null,
        ) ?: return
        draft = ""
        selectedText = id.toString()
        // The box moves below the new task, for the next one (empty: done adding).
        adding = AddPlace(after = id, parent = place.parent)
    }

    /** New task: after the task picked, else inside the zoom, else first in the group picked (all groups: the first). */
    fun newTask() {
        val picked = selected?.let { id -> rows.firstOrNull { it.id == id && !it.isGroup } }?.item
        if (picked != null) {
            startAdding(AddPlace(after = picked.id, parent = picked.parent?.id))
            return
        }
        val place = vm.outlineNewTaskPlace()
        startAdding(
            when {
                place.parentId != null -> AddPlace(after = null, parent = place.parentId)
                allGroups -> AddPlace(after = null, parent = null, group = place.groupId)
                else -> AddPlace(after = null, parent = null)
            },
        )
    }

    // What the list draws: the rows, and the box a new task is typed in where it will land.
    val entries = buildList {
        val place = adding
        val groupRowShown = place?.group != null && rows.any { it.isGroup && it.id == place.group }
        val atTop = place != null && place.after == null &&
            ((place.parent == null && !groupRowShown) || (place.parent != null && place.parent == zoom))
        if (atTop) add(Entry.Editor(0, "new-top"))
        // "After X": after X and everything shown under it.
        val spot = place?.after?.let { after ->
            val at = rows.indexOfFirst { it.id == after }
            if (at < 0) null else {
                var end = at + 1
                while (end < rows.size && rows[end].depth > rows[at].depth) end++
                (end - 1) to rows[at].depth
            }
        }
        rows.forEachIndexed { index, row ->
            add(Entry.Line(row))
            if (!atTop && place != null && place.after == null) {
                if (row.isGroup && place.group == row.id) add(Entry.Editor(1, "new-in-${row.id}"))
                if (!row.isGroup && place.parent == row.id) add(Entry.Editor(row.depth + 1, "new-in-${row.id}"))
            }
            if (spot != null && spot.first == index) add(Entry.Editor(spot.second, "new-after"))
        }
    }

    // Leaving a zoom or Show in tree: the row is picked and scrolled to.
    LaunchedEffect(vm.outlineReveal, rows) {
        val id = vm.outlineReveal ?: return@LaunchedEffect
        vm.outlineReveal = null
        val at = entries.indexOfFirst { it is Entry.Line && it.row.id == id }
        if (at >= 0) {
            selectedText = id.toString()
            list.scrollToItem((at - 3).coerceAtLeast(0))
        }
    }

    Column(Modifier.fillMaxSize()) {
        ViewHeader("Tasks", vm, onAdd = ::newTask)
        Toolbar(vm, allGroups)
        if (path.isNotEmpty()) PathBar(vm, path, open)
        LazyColumn(Modifier.fillMaxSize().imePadding(), state = list, contentPadding = PaddingValues(bottom = 24.dp)) {
            if (rows.isEmpty() && adding == null) {
                item(key = "empty") { Text(emptyText(vm, zoom != null), color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(16.dp)) }
            }
            for (entry in entries) {
                when (entry) {
                    is Entry.Editor -> item(key = entry.key) { NewRow(entry.depth, draft, { draft = it }, ::addTyped) { adding = null } }
                    is Entry.Line -> {
                        val row = entry.row
                        if (row.isGroup) {
                            stickyHeader(key = row.id) {
                                GroupRowView(vm, row, onAdd = { startAdding(AddPlace(after = null, parent = null, group = row.id)) })
                            }
                        } else {
                            val item = row.item!!
                            item(key = row.id) {
                                OutlineRowView(
                                    vm, row,
                                    guides = row.depth,
                                    selected = selected == row.id,
                                    editing = editingText == row.id.toString(),
                                    draft = draft,
                                    onDraft = { draft = it },
                                    onTapTitle = {
                                        if (selected == row.id && !item.isDone) {
                                            finishRename()
                                            adding = null
                                            // Renaming shows (and saves) the whole title.
                                            draft = item.title
                                            editingText = row.id.toString()
                                        } else {
                                            finishRename()
                                            selectedText = row.id.toString()
                                        }
                                    },
                                    onRenamed = {
                                        // Done on the keyboard: saved, and the next task is typed below it.
                                        finishRename()
                                        startAdding(AddPlace(after = row.id, parent = item.parent?.id))
                                    },
                                    onAddSubtask = {
                                        vm.setExpanded(row.id, true)
                                        startAdding(AddPlace(after = null, parent = row.id))
                                    },
                                    open = open,
                                )
                            }
                        }
                    }
                }
            }
        }
    }
}

private fun emptyText(vm: BoardViewModel, zoomed: Boolean): String = when {
    vm.query.isNotBlank() -> "Nothing here matches the search."
    vm.outline.shown == OutlineShow.NOW -> "Nothing can be done right now."
    vm.outline.shown == OutlineShow.WAITING -> "Nothing is waiting."
    zoomed -> "Nothing left under this task. Back goes out again."
    else -> "No tasks here yet. Tap + to add one."
}

/** Show (To do, Can do now, Waiting, Everything) and Open to (Groups, Tasks, Subtasks, All). */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun Toolbar(vm: BoardViewModel, allGroups: Boolean) {
    var choosing by remember { mutableStateOf(false) }
    val shown = vm.outline.shown
    FlowRow(Modifier.fillMaxWidth().padding(horizontal = 12.dp), verticalArrangement = Arrangement.Center) {
        Box {
            TextButton(onClick = { choosing = true }, modifier = Modifier.semantics { contentDescription = "Show: ${shown.label}" }) {
                Text("Show: ", color = MaterialTheme.colorScheme.onSurfaceVariant)
                Text("${shown.label} ▾")
            }
            DropdownMenu(expanded = choosing, onDismissRequest = { choosing = false }) {
                OutlineShow.entries.forEach { choice ->
                    DropdownMenuItem(
                        text = { Text(if (choice == shown) "✓ ${choice.label}" else choice.label) },
                        onClick = { choosing = false; vm.chooseOutlineShow(choice) },
                    )
                }
            }
        }
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("Open to", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(start = 4.dp, end = 2.dp))
            OutlineLevel.entries.filter { it != OutlineLevel.GROUPS || allGroups }.forEach { level ->
                TextButton(
                    onClick = { vm.openOutlineTo(level) },
                    contentPadding = PaddingValues(horizontal = 8.dp),
                    modifier = Modifier.semantics { contentDescription = "Open the tree to ${level.label.lowercase()}" },
                ) { Text(level.label) }
            }
        }
    }
}

/** The path above a zoomed tree: All groups › group › each parent › the zoomed task (its details), with its progress. */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun PathBar(vm: BoardViewModel, parts: List<OutlinePathPart>, open: (UUID) -> Unit) {
    FlowRow(Modifier.fillMaxWidth().padding(horizontal = 12.dp).semantics { contentDescription = "Where you are" }, verticalArrangement = Arrangement.Center) {
        parts.forEachIndexed { i, part ->
            val last = i == parts.lastIndex
            Row(verticalAlignment = Alignment.CenterVertically) {
                if (i > 0) Text("›", color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(horizontal = 2.dp))
                val label = when {
                    part.kind == OutlinePartKind.ALL -> "All groups: leave the zoom"
                    last -> "${part.title}: open its details"
                    else -> part.title
                }
                Text(
                    part.title,
                    style = MaterialTheme.typography.labelLarge,
                    fontWeight = if (last) FontWeight.Bold else FontWeight.Medium,
                    color = if (last) MaterialTheme.colorScheme.onSurface else ViewColors.accent,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.clickable {
                        val id = part.id
                        when {
                            part.kind == OutlinePartKind.ALL -> {
                                vm.leaveOutlineZoom()
                                if (vm.selectedGroup != null) vm.select(null)
                            }
                            part.kind == OutlinePartKind.GROUP && id != null -> {
                                vm.leaveOutlineZoom()
                                if (vm.selectedGroup != id) vm.select(id)
                            }
                            last && id != null -> open(id)
                            id != null -> vm.zoomOutline(id)
                        }
                    }.padding(vertical = 10.dp, horizontal = 4.dp).semantics { contentDescription = label },
                )
                if (last && part.total > 0) Progress(part.done, part.total)
            }
        }
    }
}

/** A group (under All groups): open or close it, how much is left, its progress, and + to add a task to it. A drop lands first in it. */
@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun GroupRowView(vm: BoardViewModel, row: OutlineRow, onAdd: () -> Unit) {
    val group = row.group ?: return
    var over by remember { mutableStateOf(false) }
    val target = remember(group.id) {
        object : DragAndDropTarget {
            override fun onDrop(event: DragAndDropEvent): Boolean {
                over = false
                val id = TaskDrag.id(event) ?: return false
                val ok = vm.dropOnGroup(id, group.id)
                if (ok) vm.setGroupOpen(group.id, true)
                return ok
            }

            override fun onEntered(event: DragAndDropEvent) { over = true }
            override fun onExited(event: DragAndDropEvent) { over = false }
            override fun onEnded(event: DragAndDropEvent) { over = false }
        }
    }
    val accent = ViewColors.accent
    val toggle = { vm.setGroupOpen(group.id, !row.expanded) }
    Column(
        Modifier.fillMaxWidth()
            .background(MaterialTheme.colorScheme.background)
            .dragAndDropTarget(shouldStartDragAndDrop = TaskDrag::isTask, target = target)
            .background(if (over) accent.copy(alpha = 0.16f) else Color.Transparent),
    ) {
        Row(
            Modifier.fillMaxWidth().heightIn(min = 48.dp).padding(start = START.dp, end = 4.dp)
                .semantics { contentDescription = "${group.name}, ${leftText(row)}" },
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                if (row.expanded) "▾" else "▸",
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                style = MaterialTheme.typography.titleMedium,
                textAlign = androidx.compose.ui.text.style.TextAlign.Center,
                modifier = Modifier.width(28.dp).heightIn(min = 40.dp).padding(top = 8.dp).clickable(onClick = toggle)
                    .semantics { contentDescription = if (row.expanded) "Hide the tasks of ${group.name}" else "Show the tasks of ${group.name}" },
            )
            Box(Modifier.padding(start = 4.dp, end = 10.dp).size(10.dp).background(ViewColors.hex(group.color), CircleShape))
            Text(
                group.name,
                style = MaterialTheme.typography.titleSmall,
                fontWeight = FontWeight.SemiBold,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f).clickable(onClick = toggle).padding(vertical = 12.dp),
            )
            Text(leftText(row), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(horizontal = 8.dp))
            if (row.total > 0) Progress(row.done, row.total)
            TextButton(onClick = onAdd, modifier = Modifier.semantics { contentDescription = "Add a task to ${group.name}" }) { Text("+", style = MaterialTheme.typography.titleMedium) }
        }
        HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.5f))
    }
}

private fun leftText(row: OutlineRow): String = when {
    row.left > 0 -> "${row.left} to do"
    row.total > 0 -> "All done"
    else -> "No tasks yet"
}

/** How far along: a thin bar and done/total (leaves under it). */
@Composable
private fun Progress(done: Int, total: Int) {
    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.semantics(mergeDescendants = true) { contentDescription = "$done of $total done" }) {
        Box(Modifier.width(36.dp).height(3.dp).background(MaterialTheme.colorScheme.outlineVariant, RoundedCornerShape(2.dp))) {
            if (done > 0) Box(Modifier.fillMaxHeight().fillMaxWidth(done.toFloat() / total).background(if (done == total) ViewColors.done else ViewColors.accent, RoundedCornerShape(2.dp)))
        }
        Text("$done/$total", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(start = 6.dp))
    }
}

/** ⤢: into a branch. */
private val zoomIcon: ImageVector by lazy {
    ImageVector.Builder("Zoom", 24.dp, 24.dp, 24f, 24f).apply {
        path(stroke = SolidColor(Color.Black), strokeLineWidth = 2f, strokeLineCap = StrokeCap.Round) {
            moveTo(14f, 4f); lineTo(20f, 4f); lineTo(20f, 10f)
            moveTo(20f, 4f); lineTo(13.5f, 10.5f)
            moveTo(10f, 20f); lineTo(4f, 20f); lineTo(4f, 14f)
            moveTo(4f, 20f); lineTo(10.5f, 13.5f)
        }
    }.build()
}

/**
 * A task's row: open or close its subtasks, finish it, its title (two lines, without its parent's name; rename in
 * place shows the whole title), what it says about the task, its progress, ⤢ (zoom in) and ⋯. Guide lines mark each
 * level of indent.
 */
@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun OutlineRowView(
    vm: BoardViewModel,
    row: OutlineRow,
    guides: Int,
    selected: Boolean,
    editing: Boolean,
    draft: String,
    onDraft: (String) -> Unit,
    onTapTitle: () -> Unit,
    onRenamed: () -> Unit,
    onAddSubtask: () -> Unit,
    open: (UUID) -> Unit,
) {
    val item = row.item ?: return
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
    val guide = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.5f)
    Column(
        Modifier.fillMaxWidth()
            .onGloballyPositioned { bounds = it.boundsInRoot() }
            .dragAndDropTarget(shouldStartDragAndDrop = TaskDrag::isTask, target = target)
            .background(
                when {
                    zone == OutlineMoves.Zone.INSIDE -> accent.copy(alpha = 0.16f)
                    selected -> accent.copy(alpha = 0.08f)
                    else -> Color.Transparent
                },
            )
            .drawBehind {
                // A thin line down each level of indent, under its parent's ▸.
                for (level in 0 until guides) {
                    val x = (START + level * INDENT + 14).dp.toPx()
                    drawLine(guide, Offset(x, 0f), Offset(x, size.height), strokeWidth = 1.dp.toPx())
                }
            },
    ) {
        if (zone == OutlineMoves.Zone.BEFORE) Box(Modifier.fillMaxWidth().height(2.dp).background(accent))
        Row(
            Modifier.fillMaxWidth().heightIn(min = 44.dp).padding(start = (START + row.depth * INDENT).dp, end = 0.dp),
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
                    val icon = item.icon?.takeIf { it.isNotBlank() }?.let { "$it " }.orEmpty()
                    Text(
                        icon + row.title + if (item.repeat != null) " 🔁" else "",
                        style = MaterialTheme.typography.bodyLarge.copy(textDecoration = if (item.isDone) TextDecoration.LineThrough else null),
                        color = if (item.isDone) MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.onSurface,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.fillMaxWidth().taskDragSource(item.id, onTapTitle)
                            .semantics { if (row.title != row.fullTitle) contentDescription = row.fullTitle },
                    )
                }
                Facts(vm, row, item, timing)
            }
            if (item.children.isNotEmpty()) {
                Box(
                    Modifier.size(40.dp).clickable { vm.zoomOutline(item.id) }.semantics { contentDescription = "Zoom into ${item.title}" },
                    contentAlignment = Alignment.Center,
                ) { Icon(zoomIcon, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.size(16.dp)) }
            }
            RowMenu(vm, row, item, timing, onAddSubtask, open)
        }
        if (zone == OutlineMoves.Zone.AFTER) Box(Modifier.fillMaxWidth().height(2.dp).background(accent))
        HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.35f))
    }
}

/** What a row says about its task: its progress (leaves done of all), labels and tags, deadline, time spent, notes. */
@Composable
private fun Facts(vm: BoardViewModel, row: OutlineRow, item: WorkItem, timing: Boolean) {
    val parts = mutableListOf<String>()
    item.deadline?.takeIf { !item.isDone }?.let { parts.add("📅 ${RelativeTime.format(it, vm.now)}") }
    val spent = item.timeSpent(vm.now)
    if (spent >= Duration.ofMinutes(1) || timing) parts.add("⏱ ${durationWords(spent)}")
    if (item.notes.isNotEmpty()) parts.add("📝 ${item.notes.size}")
    if (item.labels.isNotEmpty() || item.tags.isNotEmpty()) TaskTags(item, vm, maxTags = 2)
    if (row.total > 0 || parts.isNotEmpty()) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            if (row.total > 0) Progress(row.done, row.total)
            if (row.total > 0 && parts.isNotEmpty()) Spacer(Modifier.width(10.dp))
            if (parts.isNotEmpty()) {
                val overdue = item.deadline?.let { !item.isDone && it.isBefore(vm.now) } == true
                Text(parts.joinToString("  ·  "), style = MaterialTheme.typography.labelSmall, color = if (overdue) ViewColors.danger else if (timing) ViewColors.accent else MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1)
            }
        }
    }
}

/** ⋯ on a row: time it, add a subtask, open it, zoom into it, move it. */
@Composable
private fun RowMenu(vm: BoardViewModel, row: OutlineRow, item: WorkItem, timing: Boolean, onAddSubtask: () -> Unit, open: (UUID) -> Unit) {
    var menu by remember { mutableStateOf(false) }
    var groups by remember { mutableStateOf(false) }
    Box {
        TextButton(onClick = { menu = true }, contentPadding = PaddingValues(horizontal = 8.dp), modifier = Modifier.semantics { contentDescription = "Actions for ${item.title}" }) { Text("⋯") }
        DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
            DropdownMenuItem(text = { Text("Open details") }, onClick = { menu = false; open(item.id) })
            DropdownMenuItem(text = { Text("Add a subtask") }, onClick = { menu = false; onAddSubtask() })
            if (item.children.isNotEmpty()) DropdownMenuItem(text = { Text("Zoom in: show only this") }, onClick = { menu = false; vm.zoomOutline(item.id) })
            if (!item.isDone) DropdownMenuItem(text = { Text(if (timing) "Stop the timer" else "Start the timer") }, onClick = { menu = false; vm.toggleTimer(item.id) })
            HorizontalDivider()
            DropdownMenuItem(text = { Text("Up") }, onClick = { menu = false; vm.step(item.id, -1) })
            DropdownMenuItem(text = { Text("Down") }, onClick = { menu = false; vm.step(item.id, 1) })
            DropdownMenuItem(
                text = { Text("Into the task above") },
                enabled = vm.canIndent(item.id),
                onClick = {
                    menu = false
                    vm.scope.indent(item.id, vm.outlineKeeps())?.moves?.firstOrNull()?.let { m -> (m as? dev.onething.core.BoardMove.Move)?.parentId?.let { vm.setExpanded(it, true) } }
                    vm.indent(item.id)
                },
            )
            // At the top of a zoom it would leave the view: drag or Move to group still can.
            DropdownMenuItem(text = { Text("Out one level") }, enabled = row.canOutdent && vm.canOutdent(item.id), onClick = { menu = false; vm.outdent(item.id) })
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
    Row(Modifier.fillMaxWidth().heightIn(min = 44.dp).padding(start = (START + depth * INDENT + 28).dp, end = 8.dp), verticalAlignment = Alignment.CenterVertically) {
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
