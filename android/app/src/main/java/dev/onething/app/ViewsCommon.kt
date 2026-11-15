package dev.onething.app

import android.content.ClipData
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.draganddrop.dragAndDropSource
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
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
import androidx.compose.ui.draganddrop.DragAndDropTransferData
import androidx.compose.ui.draganddrop.toAndroidDragEvent
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.path
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import dev.onething.core.Chip
import dev.onething.core.ChipTone
import dev.onething.core.Priority
import dev.onething.core.WorkItem
import java.util.UUID

// What the Board, Tasks, Done, Plan and Reports views share: their header (the group shown, search, a new task), the
// line that says what just happened, colors, chips, and dragging a task.

/** The brand's accent, and the colors the other apps use for warnings, danger and done. */
object ViewColors {
    val accent = Color(0xFF6366F1)
    val danger = Color(0xFFEF4444)
    val warn = Color(0xFFCA8A04)
    val done = Color(0xFF22C55E)
    val info = Color(0xFF0EA5E9)

    fun hex(text: String?, fallback: Color = accent): Color = parseColor(text) ?: fallback

    fun priority(p: Priority): Color = when (p) {
        Priority.CRITICAL -> danger
        Priority.HIGH -> Color(0xFFF59E0B)
        Priority.NORMAL -> accent
        Priority.LOW -> Color(0xFF94A3B8)
    }
}

/** The view header: its name, the group shown (a menu), search and a new task. */
@Composable
fun ViewHeader(title: String, vm: BoardViewModel, onAdd: (() -> Unit)?, actions: @Composable () -> Unit = {}) {
    var searching by rememberSaveable { mutableStateOf(false) }
    Column(Modifier.fillMaxWidth().padding(start = 16.dp, end = 4.dp, top = 8.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(title, style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Bold, modifier = Modifier.weight(1f), maxLines = 1)
            actions()
            IconButton(onClick = { searching = !searching; if (!searching) vm.search("") }) { Icon(Icons.Filled.Search, contentDescription = "Search") }
            if (onAdd != null) IconButton(onClick = onAdd) { Icon(Icons.Filled.Add, contentDescription = "New task") }
        }
        GroupMenu(vm)
        if (searching) {
            val focus = remember { FocusRequester() }
            OutlinedTextField(
                value = vm.query,
                onValueChange = { vm.search(it) },
                placeholder = { Text("Words, #tags, is:done, is:doing…") },
                singleLine = true,
                trailingIcon = { IconButton(onClick = { searching = false; vm.search("") }) { Icon(Icons.Filled.Close, contentDescription = "Close the search") } },
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
                modifier = Modifier.fillMaxWidth().padding(end = 12.dp, bottom = 4.dp).focusRequester(focus).semantics { contentDescription = "Search box" },
            )
            LaunchedEffect(Unit) { if (vm.query.isEmpty()) focus.requestFocus() }
        } else if (vm.query.isNotEmpty()) {
            SearchFilterBar(vm)
        }
        vm.status?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(end = 12.dp, bottom = 4.dp)) }
    }
}

/** "Showing what matches …": above a view the search narrows, with a way out. */
@Composable
fun SearchFilterBar(vm: BoardViewModel) {
    Row(
        Modifier.fillMaxWidth().padding(end = 12.dp, bottom = 4.dp).background(ViewColors.accent.copy(alpha = 0.08f), RoundedCornerShape(10.dp)).padding(start = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text("Showing what matches “${vm.query}”", style = MaterialTheme.typography.bodyMedium, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
        TextButton(onClick = { vm.search("") }) { Text("Show all") }
    }
}

/** The group shown (all of them, or one): every view shows only its tasks. */
@Composable
fun GroupMenu(vm: BoardViewModel) {
    var open by remember { mutableStateOf(false) }
    val selected = vm.selectedGroup
    Box {
        TextButton(onClick = { open = true }, contentPadding = androidx.compose.foundation.layout.PaddingValues(horizontal = 0.dp)) {
            if (selected != null) Box(Modifier.size(9.dp).background(ViewColors.hex(vm.groupColors[selected]), CircleShape))
            Text(
                (selected?.let { vm.groupName(it) } ?: "All groups") + " ▾",
                style = MaterialTheme.typography.labelLarge,
                modifier = Modifier.padding(start = if (selected != null) 6.dp else 0.dp),
            )
        }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            DropdownMenuItem(text = { Text("All groups") }, onClick = { open = false; vm.select(null) })
            vm.groups.forEach { (id, name) ->
                DropdownMenuItem(
                    text = { Text(name) },
                    leadingIcon = { Box(Modifier.size(10.dp).background(ViewColors.hex(vm.groupColors[id]), CircleShape)) },
                    onClick = { open = false; vm.select(id) },
                )
            }
        }
    }
}

/** A new task, typed with quick capture ("Call mum @tomorrow !high"): in the group shown. */
@Composable
fun QuickAddDialog(vm: BoardViewModel, onDismiss: () -> Unit) {
    var text by rememberSaveable { mutableStateOf("") }
    val focus = remember { FocusRequester() }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("New task") },
        text = {
            OutlinedTextField(
                value = text, onValueChange = { text = it }, singleLine = true,
                placeholder = { Text("Call mum @tomorrow !high due:3d") },
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
                keyboardActions = KeyboardActions(onDone = { if (vm.capture(text)) onDismiss() }),
                modifier = Modifier.focusRequester(focus),
            )
            LaunchedEffect(Unit) { focus.requestFocus() }
        },
        confirmButton = { TextButton(onClick = { if (vm.capture(text)) onDismiss() }, enabled = text.isNotBlank()) { Text("Add") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

/** A fact on a card ("Due in 2h", "2/5", "⏱ 25 min"). */
@Composable
fun ChipView(chip: Chip) {
    val color = when (chip.tone) {
        ChipTone.DANGER -> ViewColors.danger
        ChipTone.WARN -> ViewColors.warn
        ChipTone.INFO -> ViewColors.info
        ChipTone.STEP -> ViewColors.done
        ChipTone.MUTED -> MaterialTheme.colorScheme.onSurfaceVariant
    }
    Text(
        chip.text,
        style = MaterialTheme.typography.labelSmall,
        color = color,
        maxLines = 1,
        modifier = Modifier.background(color.copy(alpha = 0.12f), RoundedCornerShape(50)).padding(horizontal = 7.dp, vertical = 2.dp),
    )
}

/** A label ("urgent") in its color, or a #tag. */
@Composable
fun SmallTag(text: String, color: Color) {
    Text(
        text,
        style = MaterialTheme.typography.labelSmall,
        color = color,
        maxLines = 1,
        modifier = Modifier.background(color.copy(alpha = 0.14f), RoundedCornerShape(6.dp)).padding(horizontal = 6.dp, vertical = 1.dp),
    )
}

/** Labels (in their colors) and the first tags. */
@OptIn(androidx.compose.foundation.layout.ExperimentalLayoutApi::class)
@Composable
fun TaskTags(item: WorkItem, vm: BoardViewModel, maxTags: Int = 3) {
    if (item.labels.isEmpty() && item.tags.isEmpty()) return
    androidx.compose.foundation.layout.FlowRow(horizontalArrangement = Arrangement.spacedBy(4.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        item.labels.forEach { SmallTag(it, ViewColors.hex(vm.labelColor(it), MaterialTheme.colorScheme.onSurfaceVariant)) }
        item.tags.take(maxTags).forEach { SmallTag("#$it", MaterialTheme.colorScheme.onSurfaceVariant) }
    }
}

/** A task being dragged (on the Board, in Tasks, onto the Plan): its id, inside this app. */
object TaskDrag {
    private const val PREFIX = "onething-task:"

    fun data(id: UUID) = DragAndDropTransferData(ClipData.newPlainText("Task", PREFIX + id), localState = id)

    /** The task an event carries (null: something else). */
    fun id(event: DragAndDropEvent): UUID? {
        val drag = event.toAndroidDragEvent()
        (drag.localState as? UUID)?.let { return it }
        val text = drag.clipData?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.text?.toString() ?: return null
        return if (text.startsWith(PREFIX)) runCatching { UUID.fromString(text.removePrefix(PREFIX)) }.getOrNull() else null
    }

    /** Whether a drag is a task's (targets only light up for them). */
    fun isTask(event: DragAndDropEvent): Boolean = id(event) != null || event.toAndroidDragEvent().clipDescription?.label == "Task"
}

/** A tap opens it; holding it picks it up to drag (the system draws it under your finger). */
@OptIn(ExperimentalFoundationApi::class)
fun Modifier.taskDragSource(id: UUID, onTap: () -> Unit): Modifier = dragAndDropSource {
    detectTapGestures(
        onTap = { onTap() },
        onLongPress = { startTransfer(TaskDrag.data(id)) },
    )
}

/** The icons of the views that Material's core set doesn't have: the Board (columns) and Reports (bars). */
object ViewIcons {
    val board: ImageVector by lazy {
        ImageVector.Builder("Board", 24.dp, 24.dp, 24f, 24f).apply {
            path(fill = SolidColor(Color.Black)) {
                // Three columns of different heights.
                moveTo(3f, 4f); lineTo(8f, 4f); lineTo(8f, 20f); lineTo(3f, 20f); close()
                moveTo(9.5f, 4f); lineTo(14.5f, 4f); lineTo(14.5f, 15f); lineTo(9.5f, 15f); close()
                moveTo(16f, 4f); lineTo(21f, 4f); lineTo(21f, 11f); lineTo(16f, 11f); close()
            }
        }.build()
    }

    val reports: ImageVector by lazy {
        ImageVector.Builder("Reports", 24.dp, 24.dp, 24f, 24f).apply {
            path(fill = SolidColor(Color.Black)) {
                moveTo(4f, 13f); lineTo(8f, 13f); lineTo(8f, 20f); lineTo(4f, 20f); close()
                moveTo(10f, 8f); lineTo(14f, 8f); lineTo(14f, 20f); lineTo(10f, 20f); close()
                moveTo(16f, 4f); lineTo(20f, 4f); lineTo(20f, 20f); lineTo(16f, 20f); close()
            }
        }.build()
    }

    val tasks: ImageVector by lazy {
        ImageVector.Builder("Tasks", 24.dp, 24.dp, 24f, 24f).apply {
            path(fill = SolidColor(Color.Black)) {
                // An outline: a line, then two indented ones, then another.
                moveTo(3f, 4f); lineTo(21f, 4f); lineTo(21f, 6.5f); lineTo(3f, 6.5f); close()
                moveTo(8f, 9f); lineTo(21f, 9f); lineTo(21f, 11.5f); lineTo(8f, 11.5f); close()
                moveTo(8f, 14f); lineTo(21f, 14f); lineTo(21f, 16.5f); lineTo(8f, 16.5f); close()
                moveTo(3f, 19f); lineTo(21f, 19f); lineTo(21f, 21.5f); lineTo(3f, 21.5f); close()
            }
        }.build()
    }
}
