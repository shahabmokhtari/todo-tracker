package dev.onething.app

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.draganddrop.dragAndDropTarget
import androidx.compose.foundation.gestures.snapping.rememberSnapFlingBehavior
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
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draganddrop.DragAndDropEvent
import androidx.compose.ui.draganddrop.DragAndDropTarget
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.unit.dp
import dev.onething.core.BoardColumn
import dev.onething.core.BoardColumns
import dev.onething.core.CardFacts
import dev.onething.core.WorkItem
import java.util.UUID
import kotlinx.coroutines.launch

/**
 * The Board (the web's board.js, the iPhone's BoardView): the group's top-level tasks as cards in a small, fixed flow,
 * Inbox → Next → Doing → Done. Hold a card and drag it to another column (or onto a column's name above), or use its
 * Move menu; Doing is kept to three on purpose. Starting a timer moves a card to Doing; Done finishes it, and out of
 * Done opens it again. As many columns side by side as fit; on a phone, one at a time (swipe, or tap a column's name).
 */
@Composable
fun BoardScreen(vm: BoardViewModel, open: (UUID) -> Unit, onAdd: () -> Unit) {
    @Suppress("UNUSED_VARIABLE") val redraw = vm.version
    val columns = vm.scope.columns(vm.now)
    val all = BoardColumn.entries
    Column(Modifier.fillMaxSize()) {
        ViewHeader("Board", vm, onAdd)
        Text(
            "Hold a card to drag it, or use ⇄ on it. Starting a timer moves a card to Doing.",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp),
        )
        BoxWithConstraints(Modifier.fillMaxSize().imePadding()) {
            val visible = (maxWidth.value / 250f).toInt().coerceIn(1, all.size)
            val columnWidth = (maxWidth - 32.dp - 12.dp * (visible - 1)) / visible
            val list = rememberLazyListState()
            val scope = rememberCoroutineScope()
            Column(Modifier.fillMaxSize()) {
                if (visible < all.size) {
                    // Not all columns fit: each is a tap away (and a drop target: a card dropped on a name goes there).
                    val first = list.firstVisibleItemIndex
                    Row(Modifier.padding(horizontal = 16.dp, vertical = 4.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                        all.forEachIndexed { index, column ->
                            ColumnTab(column, columns[column].orEmpty().size, onScreen = index >= first && index < first + visible, vm = vm) {
                                scope.launch { list.animateScrollToItem(minOf(index, all.size - visible)) }
                            }
                        }
                    }
                }
                LazyRow(
                    state = list,
                    flingBehavior = rememberSnapFlingBehavior(list),
                    contentPadding = PaddingValues(horizontal = 16.dp),
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                    modifier = Modifier.fillMaxSize().padding(top = 4.dp, bottom = 12.dp),
                ) {
                    items(all, key = { it.wire }) { column ->
                        BoardColumnView(vm, column, columns[column].orEmpty(), open, Modifier.width(columnWidth).fillMaxHeight())
                    }
                }
            }
        }
    }
}

/** A column's name and count above the board on a phone: a tap shows it, a card dropped on it moves there. */
@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun ColumnTab(column: BoardColumn, count: Int, onScreen: Boolean, vm: BoardViewModel, onClick: () -> Unit) {
    var over by remember { mutableStateOf(false) }
    val target = remember(column) {
        object : DragAndDropTarget {
            override fun onDrop(event: DragAndDropEvent): Boolean {
                over = false
                val id = TaskDrag.id(event) ?: return false
                return vm.moveCard(id, column)
            }

            override fun onEntered(event: DragAndDropEvent) { over = true }
            override fun onExited(event: DragAndDropEvent) { over = false }
            override fun onEnded(event: DragAndDropEvent) { over = false }
        }
    }
    val tint = if (onScreen || over) ViewColors.accent else MaterialTheme.colorScheme.onSurfaceVariant
    Surface(
        onClick = onClick,
        shape = RoundedCornerShape(50),
        color = if (over) ViewColors.accent.copy(alpha = 0.3f) else if (onScreen) ViewColors.accent.copy(alpha = 0.14f) else MaterialTheme.colorScheme.surfaceVariant,
        modifier = Modifier.dragAndDropTarget(shouldStartDragAndDrop = TaskDrag::isTask, target = target).semantics { contentDescription = "Show ${column.displayName}" },
    ) {
        Text("${column.displayName} $count", style = MaterialTheme.typography.labelLarge, fontWeight = FontWeight.SemiBold, color = tint, modifier = Modifier.padding(horizontal = 10.dp, vertical = 6.dp))
    }
}

/** One column: its name and count (Doing as 2/3, with a word when it holds more), its cards, and a box to add one. */
@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun BoardColumnView(vm: BoardViewModel, column: BoardColumn, cards: List<WorkItem>, open: (UUID) -> Unit, modifier: Modifier) {
    val fill = if (column == BoardColumn.DOING) BoardColumns.fill(cards.size) else BoardColumns.Fill.OK
    var targeted by remember { mutableStateOf(false) }
    var dropBefore by remember { mutableStateOf<UUID?>(null) }
    // Dropped on the column (not on a card): its place in the shared order, or last within the column.
    val target = remember(column) {
        object : DragAndDropTarget {
            override fun onDrop(event: DragAndDropEvent): Boolean {
                targeted = false
                val id = TaskDrag.id(event) ?: return false
                return vm.moveCard(id, column)
            }

            override fun onEntered(event: DragAndDropEvent) { targeted = true }
            override fun onExited(event: DragAndDropEvent) { targeted = false }
            override fun onEnded(event: DragAndDropEvent) {
                targeted = false
                dropBefore = null
            }
        }
    }
    val border = when {
        targeted -> BorderStroke(2.dp, ViewColors.accent)
        fill == BoardColumns.Fill.OVER -> BorderStroke(1.dp, ViewColors.danger.copy(alpha = 0.5f))
        else -> BorderStroke(1.dp, MaterialTheme.colorScheme.outlineVariant)
    }
    Column(
        modifier
            .background(MaterialTheme.colorScheme.surfaceContainerLow, RoundedCornerShape(16.dp))
            .border(border, RoundedCornerShape(16.dp))
            .dragAndDropTarget(shouldStartDragAndDrop = TaskDrag::isTask, target = target)
            .padding(10.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(horizontal = 4.dp).semantics(mergeDescendants = true) {}) {
            Text(column.displayName, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f))
            val countColor = when (fill) {
                BoardColumns.Fill.OVER -> ViewColors.danger
                BoardColumns.Fill.FULL -> ViewColors.warn
                BoardColumns.Fill.OK -> MaterialTheme.colorScheme.onSurfaceVariant
            }
            Text(
                if (column == BoardColumn.DOING) "${cards.size}/${BoardColumns.WIP_LIMIT}" else "${cards.size}",
                style = MaterialTheme.typography.labelMedium,
                fontWeight = FontWeight.Bold,
                color = countColor,
                modifier = Modifier
                    .background(if (fill == BoardColumns.Fill.OVER) ViewColors.danger.copy(alpha = 0.14f) else MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(50))
                    .padding(horizontal = 8.dp, vertical = 2.dp),
            )
        }
        Text(
            if (column == BoardColumn.DOING) "Keep Doing to ${BoardColumns.WIP_LIMIT}: finishing beats starting" else column.hint,
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(horizontal = 4.dp),
        )
        if (fill == BoardColumns.Fill.OVER) {
            Text(BoardColumns.OVER_LIMIT_MESSAGE, style = MaterialTheme.typography.bodySmall, color = ViewColors.danger, modifier = Modifier.padding(horizontal = 4.dp))
        }
        LazyColumn(Modifier.weight(1f).fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            if (cards.isEmpty()) {
                item {
                    Text(
                        column.emptyText,
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.7f),
                        modifier = Modifier.fillMaxWidth().heightIn(min = 60.dp).padding(8.dp),
                    )
                }
            }
            items(cards, key = { it.id }) { card ->
                // Dropped on a card: it goes before that one.
                val cardTarget = remember(card.id, column) {
                    object : DragAndDropTarget {
                        override fun onDrop(event: DragAndDropEvent): Boolean {
                            dropBefore = null
                            targeted = false
                            val id = TaskDrag.id(event) ?: return false
                            return vm.moveCard(id, column, if (column == BoardColumn.DONE) null else card.id)
                        }

                        override fun onEntered(event: DragAndDropEvent) { dropBefore = card.id }
                        override fun onExited(event: DragAndDropEvent) { if (dropBefore == card.id) dropBefore = null }
                    }
                }
                Column(Modifier.dragAndDropTarget(shouldStartDragAndDrop = TaskDrag::isTask, target = cardTarget)) {
                    if (dropBefore == card.id) Box(Modifier.fillMaxWidth().height(3.dp).background(ViewColors.accent, RoundedCornerShape(50)))
                    BoardCard(vm, card, column, open)
                }
            }
        }
        if (column != BoardColumn.DONE) AddToColumn(vm, column)
    }
}

/** "Add to Next…": a task in this column, in the group shown. */
@Composable
private fun AddToColumn(vm: BoardViewModel, column: BoardColumn) {
    var text by rememberSaveable(column) { mutableStateOf("") }
    fun add() {
        if (vm.addTask(text, stage = column.stage) != null) text = ""
    }
    OutlinedTextField(
        value = text,
        onValueChange = { text = it },
        placeholder = { Text("Add to ${column.displayName}…") },
        singleLine = true,
        keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
        keyboardActions = KeyboardActions(onDone = { add() }),
        textStyle = MaterialTheme.typography.bodyMedium,
        modifier = Modifier.fillMaxWidth().semantics { contentDescription = "Add a task to ${column.displayName}" },
    )
}

/** A card: its title, group color, priority, labels, tags, deadline, progress and time; a timer, done (or archive) and a Move menu; a tap opens it. */
@Composable
private fun BoardCard(vm: BoardViewModel, card: WorkItem, column: BoardColumn, open: (UUID) -> Unit) {
    val timing = vm.timer?.item?.root?.id == card.id
    Card(
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
        border = BorderStroke(1.dp, if (timing) ViewColors.danger.copy(alpha = 0.6f) else MaterialTheme.colorScheme.outlineVariant),
        modifier = Modifier.fillMaxWidth().alpha(if (card.isDone) 0.75f else 1f)
            .semantics { contentDescription = card.title + if (card.isDone) ", done" else "" }
            .taskDragSource(card.id) { open(card.id) },
    ) {
        val stripe = ViewColors.hex(vm.groupColors[card.groupId])
        // The group's color down the side.
        Row(Modifier.drawBehind { drawRect(stripe, size = androidx.compose.ui.geometry.Size(4.dp.toPx(), size.height)) }) {
            Column(Modifier.padding(start = 14.dp, end = 4.dp, top = 8.dp, bottom = 2.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                Row(verticalAlignment = Alignment.Top) {
                    Box(Modifier.padding(top = 6.dp, end = 8.dp).size(8.dp).background(ViewColors.priority(card.priority), CircleShape))
                    Text(
                        titleWithIcon(card),
                        style = MaterialTheme.typography.bodyMedium.copy(textDecoration = if (card.isDone) TextDecoration.LineThrough else null),
                        fontWeight = FontWeight.SemiBold,
                        color = if (card.isDone) MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.onSurface,
                    )
                }
                TaskTags(card, vm)
                val facts = CardFacts.chips(card, vm.now)
                if (facts.isNotEmpty()) {
                    @OptIn(androidx.compose.foundation.layout.ExperimentalLayoutApi::class)
                    androidx.compose.foundation.layout.FlowRow(horizontalArrangement = Arrangement.spacedBy(4.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                        facts.forEach { ChipView(it) }
                    }
                }
                Row(verticalAlignment = Alignment.CenterVertically) {
                    if (!card.isDone) {
                        TextButton(onClick = { vm.toggleTimer(card.id) }, modifier = Modifier.semantics { contentDescription = if (timing) "Stop timing ${card.title}" else "Start timing ${card.title}" }) {
                            Text(if (timing) "■" else "▶", color = if (timing) ViewColors.danger else MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                    if (column == BoardColumn.DONE) {
                        TextButton(onClick = { vm.archive(card.id) }) { Text("Archive") }
                    } else {
                        TextButton(onClick = { vm.complete(card.id) }, modifier = Modifier.semantics { contentDescription = "Mark ${card.title} done" }) { Text("✓") }
                    }
                    MoveMenu(vm, card, column)
                    Spacer(Modifier.weight(1f))
                }
            }
        }
    }
}

/** ⇄: to another column, or up and down within this one. */
@Composable
private fun MoveMenu(vm: BoardViewModel, card: WorkItem, column: BoardColumn) {
    var open by remember { mutableStateOf(false) }
    Box {
        TextButton(onClick = { open = true }, modifier = Modifier.semantics { contentDescription = "Move ${card.title}" }) { Text("⇄") }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            BoardColumn.entries.filter { it != column }.forEach { to ->
                DropdownMenuItem(text = { Text("To ${to.displayName}") }, onClick = { open = false; vm.moveCard(card.id, to) })
            }
            if (column != BoardColumn.DONE) {
                HorizontalDivider()
                DropdownMenuItem(text = { Text("Up") }, onClick = { open = false; vm.moveCard(card.id, BoardColumns.Direction.UP) })
                DropdownMenuItem(text = { Text("Down") }, onClick = { open = false; vm.moveCard(card.id, BoardColumns.Direction.DOWN) })
            }
        }
    }
}
