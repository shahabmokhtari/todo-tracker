package dev.onething.app

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.draganddrop.dragAndDropTarget
import androidx.compose.foundation.gestures.detectDragGesturesAfterLongPress
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.detectVerticalDragGestures
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.VerticalDivider
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draganddrop.DragAndDropEvent
import androidx.compose.ui.draganddrop.DragAndDropTarget
import androidx.compose.ui.draganddrop.toAndroidDragEvent
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.boundsInRoot
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.compose.ui.zIndex
import dev.onething.core.AgendaEntry
import dev.onething.core.PlanDays
import dev.onething.core.PlanGrid
import dev.onething.core.PlanSegment
import dev.onething.core.PlanSettings
import dev.onething.core.Planning
import dev.onething.core.WorkItem
import dev.onething.core.plannedLength
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.UUID
import kotlin.math.roundToInt

/** Planning without dragging: a task (or one to pick), when, and how long. */
private data class PlanRequest(val taskId: UUID?, val start: Instant, val minutes: Int?)

/**
 * Plan (the web's plan.js, the iPhone's PlanView): a week (or a day) as a time grid. Plan a block of work on a task:
 * "Plan…" on a task in Not planned, a tap on an empty time, or (on a tablet) drag a task onto a time. Hold a block and
 * drag it to move it, drag its bottom edge to make it longer or shorter, tap it for more. While a block is on, its task
 * is the thing to do now. Deadlines show on their day, "now" is a line, done blocks are ticked, and each day says what
 * its blocks take in energy against what there is in a day. On a phone it's a day at a time.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PlanScreen(vm: BoardViewModel, wide: Boolean, open: (UUID) -> Unit) {
    @Suppress("UNUSED_VARIABLE") val redraw = vm.version
    val zone = ZoneId.systemDefault()
    var anchorDay by rememberSaveable { mutableStateOf(LocalDate.now(zone).toEpochDay()) }
    val anchor = LocalDate.ofEpochDay(anchorDay)
    val settings = vm.planSettings()
    // A phone shows a day at a time.
    val shown = if (wide) settings else settings.copy(view = PlanSettings.DAY)
    val first = PlanGrid.firstDay(anchor, shown.isWeek, shown.weekStart)
    val days = PlanGrid.shownDays(first, shown)
    val plan = vm.planDays(first, if (shown.isWeek) 7 else 1)
    var request by remember { mutableStateOf<PlanRequest?>(null) }
    var showTasks by rememberSaveable { mutableStateOf(false) }
    var showSettings by rememberSaveable { mutableStateOf(false) }
    val planTask: (UUID) -> Unit = { id -> request = PlanRequest(id, PlanGrid.nextQuarter(Instant.now(), zone), null) }

    Column(Modifier.fillMaxSize()) {
        ViewHeader("Plan", vm, onAdd = null)
        PlanToolbar(shown, days, wide, plan.unplanned.size,
            step = { anchorDay = anchor.plusDays(it.toLong() * if (shown.isWeek) 7 else 1).toEpochDay() },
            today = { anchorDay = LocalDate.now(zone).toEpochDay() },
            save = { vm.setPlanSettings(it(settings)) },
            settings = settings,
            onSettings = { showSettings = true },
            onTasks = { showTasks = true })
        HorizontalDivider()
        Row(Modifier.fillMaxSize()) {
            if (wide) {
                PlanTaskList(vm, plan, true, open, planTask, Modifier.width(270.dp).fillMaxHeight())
                VerticalDivider()
            }
            PlanGridView(vm, plan, days, shown, wide, open, ask = { request = it }, Modifier.weight(1f))
        }
    }
    request?.let { r -> PlanDialog(vm, r, plan.unplanned + plan.later) { request = null } }
    if (showTasks) {
        ModalBottomSheet(onDismissRequest = { showTasks = false }) {
            PlanTaskList(vm, plan, false, open = { showTasks = false; open(it) }, planTask = { showTasks = false; planTask(it) }, Modifier.fillMaxWidth().height(480.dp))
        }
    }
    if (showSettings) PlanSettingsDialog(settings, onSave = { vm.setPlanSettings(it); showSettings = false }) { showSettings = false }
}

@Composable
private fun PlanToolbar(
    shown: PlanSettings,
    days: List<LocalDate>,
    wide: Boolean,
    unplanned: Int,
    step: (Int) -> Unit,
    today: () -> Unit,
    save: ((PlanSettings) -> PlanSettings) -> Unit,
    settings: PlanSettings,
    onSettings: () -> Unit,
    onTasks: () -> Unit,
) {
    val week = shown.isWeek
    val title = if (days.size == 1) {
        days[0].format(DateTimeFormatter.ofPattern(if (wide) "EEEE d MMMM" else "EEE d MMM"))
    } else {
        "${days.first().format(DateTimeFormatter.ofPattern("d MMM"))} – ${days.last().format(DateTimeFormatter.ofPattern("d MMM"))}"
    }
    Column(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            TextButton(onClick = { step(-1) }, modifier = Modifier.semantics { contentDescription = if (week) "Previous week" else "Previous day" }) { Text("‹", style = MaterialTheme.typography.titleLarge) }
            OutlinedButton(onClick = today) { Text("Today") }
            TextButton(onClick = { step(1) }, modifier = Modifier.semantics { contentDescription = if (week) "Next week" else "Next day" }) { Text("›", style = MaterialTheme.typography.titleLarge) }
            Text(title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f).padding(start = 4.dp))
            if (!wide) TextButton(onClick = onTasks, modifier = Modifier.semantics { contentDescription = "Not planned: $unplanned tasks" }) { Text("☰ $unplanned") }
        }
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.horizontalScroll(rememberScrollState())) {
            if (wide) {
                FilterChip(selected = !settings.isWeek, onClick = { save { it.copy(view = PlanSettings.DAY) } }, label = { Text("Day") })
                FilterChip(selected = settings.isWeek, onClick = { save { it.copy(view = PlanSettings.WEEK) } }, label = { Text("Week") })
            }
            FilterChip(selected = settings.isWorkHours, onClick = { save { it.copy(hours = PlanSettings.WORK) } }, label = { Text("Work hours") })
            FilterChip(selected = !settings.isWorkHours, onClick = { save { it.copy(hours = PlanSettings.FULL) } }, label = { Text("Full day") })
            TextButton(onClick = onSettings, modifier = Modifier.semantics { contentDescription = "Work hours and days" }) { Text("⚙ Work hours") }
        }
    }
}

/** The tasks to do that aren't planned yet, and Later: the ones put off, with when they're back. */
@Composable
private fun PlanTaskList(vm: BoardViewModel, plan: PlanDays, wide: Boolean, open: (UUID) -> Unit, planTask: (UUID) -> Unit, modifier: Modifier) {
    LazyColumn(modifier, contentPadding = androidx.compose.foundation.layout.PaddingValues(12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        item { Text("Not planned", style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold) }
        if (plan.unplanned.isEmpty()) {
            item { Text("Everything to do now is planned.", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant) }
        } else {
            item { Text(if (wide) "Tap Plan… on a task, or hold it and drag it onto a time." else "Tap Plan… on a task to give it a time.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
        }
        items(plan.unplanned, key = { "u" + it.item.id }) { PlanTaskRow(vm, it, null, open, planTask) }
        if (plan.later.isNotEmpty()) {
            item { Text("Later", style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold, modifier = Modifier.padding(top = 12.dp)) }
            items(plan.later, key = { "l" + it.item.id }) { entry ->
                val back = entry.wakeAt?.let { "back ${vm.planWhen(it)}" } ?: entry.waitingFor?.let { "after “${it.title}”" }
                PlanTaskRow(vm, entry, back, open, planTask)
            }
        }
    }
}

@Composable
private fun PlanTaskRow(vm: BoardViewModel, entry: AgendaEntry, back: String?, open: (UUID) -> Unit, planTask: (UUID) -> Unit) {
    val item = entry.item
    Row(Modifier.fillMaxWidth().padding(vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.width(4.dp).height(36.dp).background(accentOf(vm, item), RoundedCornerShape(2.dp)))
        Column(Modifier.weight(1f).padding(start = 8.dp).taskDragSource(item.id) { open(item.id) }) {
            Text(titleWithIcon(item) + (item.energy?.let { " " + "⚡".repeat(it) } ?: ""), style = MaterialTheme.typography.bodyMedium, maxLines = 2)
            if (entry.breadcrumb.isNotEmpty()) Text(entry.breadcrumb.joinToString(" › "), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1)
            back?.let { Text(it, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
        }
        TextButton(onClick = { planTask(item.id) }, modifier = Modifier.semantics { contentDescription = "Plan “${item.title}”…" }) { Text("Plan…") }
    }
}

/** A task's own color, else its group's, else the brand's. */
private fun accentOf(vm: BoardViewModel, item: WorkItem) =
    ViewColors.hex(item.color ?: item.root.color ?: vm.groupColors[item.groupId])

/** Days side by side, hours down; blocks where they're planned. */
@Composable
private fun PlanGridView(vm: BoardViewModel, plan: PlanDays, days: List<LocalDate>, settings: PlanSettings, wide: Boolean, open: (UUID) -> Unit, ask: (PlanRequest) -> Unit, modifier: Modifier) {
    val zone = ZoneId.systemDefault()
    val parts = plan.blocks.flatMap { item -> PlanGrid.segments(item.plannedAt!!, item.plannedLength, zone).map { item to it } }
    val (from, to) = PlanGrid.hours(settings, parts.map { it.second }, days)
    val perMinute = if (wide) 1.0f else 1.1f
    val gutter = 48.dp
    val scroll = rememberScrollState()
    val density = LocalDensity.current
    // The whole day starts at work hours rather than midnight.
    LaunchedEffect(settings.hours, settings.workStart) {
        if (!settings.isWorkHours) scroll.scrollTo(with(density) { (maxOf(0, settings.workStart - 60) * perMinute).dp.roundToPx() })
    }
    BoxWithConstraints(modifier.fillMaxHeight()) {
        val column = maxOf(60.dp, (maxWidth - gutter) / maxOf(days.size, 1))
        Column(Modifier.fillMaxSize()) {
            Row {
                Spacer(Modifier.width(gutter))
                days.forEach { day -> PlanDayHead(vm, day, plan, open, Modifier.width(column)) }
            }
            HorizontalDivider()
            Row(Modifier.verticalScroll(scroll).padding(bottom = 12.dp)) {
                HourGutter(from, to, perMinute, Modifier.width(gutter))
                days.forEachIndexed { index, day ->
                    PlanDayColumn(vm, day, index, days, from, to, perMinute, column, parts.filter { it.second.day == day }, open, ask)
                }
            }
        }
    }
}

@Composable
private fun HourGutter(from: Int, to: Int, perMinute: Float, modifier: Modifier) {
    Box(modifier.height(((to - from) * perMinute).dp)) {
        var m = (from + 59) / 60 * 60
        while (m < to) {
            val y = ((m - from) * perMinute).dp
            Text(
                PlanGrid.hhmm(m), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.offset(y = if (m == from) y else y - 7.dp).padding(start = 6.dp),
            )
            m += 60
        }
    }
}

/** A day's name and date, what its blocks take in energy, its deadlines and what comes back that day. */
@Composable
private fun PlanDayHead(vm: BoardViewModel, day: LocalDate, plan: PlanDays, open: (UUID) -> Unit, modifier: Modifier) {
    val zone = ZoneId.systemDefault()
    val today = day == LocalDate.now(zone)
    val deadlines = plan.deadlines.filter { it.deadline!!.atZone(zone).toLocalDate() == day }
    val back = plan.later.filter { e -> e.wakeAt?.atZone(zone)?.toLocalDate() == day }
    Column(
        modifier.background(if (today) ViewColors.accent.copy(alpha = 0.07f) else androidx.compose.ui.graphics.Color.Transparent).padding(horizontal = 3.dp, vertical = 6.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(3.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(4.dp)) {
            Text(day.format(DateTimeFormatter.ofPattern("EEE")), style = MaterialTheme.typography.labelMedium, color = if (today) ViewColors.accent else MaterialTheme.colorScheme.onSurfaceVariant)
            Box(Modifier.size(26.dp).background(if (today) ViewColors.accent else androidx.compose.ui.graphics.Color.Transparent, CircleShape), contentAlignment = Alignment.Center) {
                Text("${day.dayOfMonth}", style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.SemiBold, color = if (today) androidx.compose.ui.graphics.Color.White else MaterialTheme.colorScheme.onSurface)
            }
        }
        val used = plan.energy[day] ?: 0.0
        val per = vm.energyPerDay
        if (used > 0) {
            val over = used > per
            val n = Math.round(used * 10) / 10.0
            val shownText = if (n == Math.rint(n)) n.toLong().toString() else "%.1f".format(n)
            val color = if (over) ViewColors.danger else ViewColors.warn
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.semantics(mergeDescendants = true) {
                contentDescription = "Energy: $shownText of $per" + if (over) ", more than you have in a day" else ""
            }) {
                Text("⚡ $shownText/$per", style = MaterialTheme.typography.labelSmall, color = if (over) ViewColors.danger else MaterialTheme.colorScheme.onSurfaceVariant)
                Box(Modifier.padding(start = 4.dp).width(28.dp).height(4.dp).background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(2.dp))) {
                    Box(Modifier.fillMaxHeight().fillMaxWidth(minOf(1.0, used / per).toFloat()).background(color, RoundedCornerShape(2.dp)))
                }
            }
        }
        deadlines.take(2).forEach { HeadChip("⚑ ${it.title}", if (it.isDone) MaterialTheme.colorScheme.onSurfaceVariant else ViewColors.warn) { open(it.id) } }
        back.take(2).forEach { HeadChip("◷ ${it.item.title}", MaterialTheme.colorScheme.onSurfaceVariant) { open(it.item.id) } }
        if (deadlines.size + back.size > 4) Text("+${deadlines.size + back.size - 4} more", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

@Composable
private fun HeadChip(text: String, color: androidx.compose.ui.graphics.Color, onClick: () -> Unit) {
    Text(
        text, style = MaterialTheme.typography.labelSmall, color = color, maxLines = 1, overflow = TextOverflow.Ellipsis,
        modifier = Modifier.fillMaxWidth().background(color.copy(alpha = 0.14f), RoundedCornerShape(50)).clickable(onClick = onClick).padding(horizontal = 6.dp, vertical = 2.dp),
    )
}

/** One day's column: hour lines, the blocks on it, "now", and where a task is dropped or a tap plans one. */
@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun PlanDayColumn(
    vm: BoardViewModel,
    day: LocalDate,
    index: Int,
    days: List<LocalDate>,
    from: Int,
    to: Int,
    perMinute: Float,
    column: androidx.compose.ui.unit.Dp,
    parts: List<Pair<WorkItem, PlanSegment>>,
    open: (UUID) -> Unit,
    ask: (PlanRequest) -> Unit,
) {
    val zone = ZoneId.systemDefault()
    val today = day == vm.now.atZone(zone).toLocalDate()
    val density = LocalDensity.current
    val pxPerMinute = with(density) { perMinute.dp.toPx() }
    var bounds by remember { mutableStateOf(Rect.Zero) }
    var targeted by remember { mutableStateOf(false) }
    val lineColor = MaterialTheme.colorScheme.outlineVariant
    val target = remember(day) {
        object : DragAndDropTarget {
            override fun onDrop(event: DragAndDropEvent): Boolean {
                targeted = false
                val id = TaskDrag.id(event) ?: return false
                val minutes = PlanGrid.snapped(from + (event.toAndroidDragEvent().y - bounds.top) / pxPerMinute.toDouble())
                return vm.plan(id, PlanGrid.time(minutes, day, ZoneId.systemDefault()))
            }

            override fun onEntered(event: DragAndDropEvent) { targeted = true }
            override fun onExited(event: DragAndDropEvent) { targeted = false }
            override fun onEnded(event: DragAndDropEvent) { targeted = false }
        }
    }
    Box(
        Modifier.width(column).height(((to - from) * perMinute).dp)
            .onGloballyPositioned { bounds = it.boundsInRoot() }
            .background(if (targeted) ViewColors.accent.copy(alpha = 0.12f) else if (today) ViewColors.accent.copy(alpha = 0.05f) else androidx.compose.ui.graphics.Color.Transparent)
            .drawBehind {
                drawLine(lineColor, Offset(0f, 0f), Offset(0f, size.height), strokeWidth = 1f)
                var m = (from + 59) / 60 * 60
                while (m < to) {
                    val y = (m - from) * pxPerMinute
                    drawLine(lineColor.copy(alpha = 0.6f), Offset(0f, y), Offset(size.width, y), strokeWidth = 1f)
                    m += 60
                }
            }
            .dragAndDropTarget(shouldStartDragAndDrop = TaskDrag::isTask, target = target)
            // A tap on an empty time: plan a task there.
            .pointerInput(day, from) {
                detectTapGestures { at ->
                    val minutes = PlanGrid.snapped(from + at.y / pxPerMinute.toDouble() - 7.5)
                    ask(PlanRequest(null, PlanGrid.time(minutes, day, ZoneId.systemDefault()), null))
                }
            }
            .semantics { contentDescription = "Plan a task on ${day.format(DateTimeFormatter.ofPattern("EEEE d MMMM"))}" },
    ) {
        val (lanes, count) = PlanGrid.lanes(parts.map { it.second })
        val laneWidth = (column - 6.dp) / count
        parts.forEachIndexed { i, (item, segment) ->
            PlanBlock(vm, item, segment, from, perMinute, column, index, days, open, ask,
                Modifier.offset(x = 3.dp + laneWidth * lanes[i]).width(maxOf(20.dp, laneWidth - 2.dp)))
        }
        if (today) {
            val minutes = PlanGrid.minutes(vm.now, zone)
            if (minutes in from..to) {
                Canvas(Modifier.fillMaxWidth().height(8.dp).offset(y = ((minutes - from) * perMinute).dp - 4.dp)) {
                    drawCircle(ViewColors.danger, radius = 4.dp.toPx(), center = Offset(4.dp.toPx(), size.height / 2))
                    drawLine(ViewColors.danger, Offset(0f, size.height / 2), Offset(size.width, size.height / 2), strokeWidth = 1.5.dp.toPx())
                }
            }
        }
    }
}

/** A block of planned work: tap it for what to do; hold and drag it to move it; drag its bottom edge to change how long. */
@Composable
private fun PlanBlock(
    vm: BoardViewModel,
    item: WorkItem,
    segment: PlanSegment,
    from: Int,
    perMinute: Float,
    column: androidx.compose.ui.unit.Dp,
    dayIndex: Int,
    days: List<LocalDate>,
    open: (UUID) -> Unit,
    ask: (PlanRequest) -> Unit,
    modifier: Modifier,
) {
    val zone = ZoneId.systemDefault()
    val density = LocalDensity.current
    val pxPerMinute = with(density) { perMinute.dp.toPx() }
    val columnPx = with(density) { column.toPx() }
    val start = item.plannedAt!!
    val movable = !item.isDone
    var dragX by remember { mutableFloatStateOf(0f) }
    var dragY by remember { mutableFloatStateOf(0f) }
    var dragging by remember { mutableStateOf(false) }
    var stretch by remember { mutableFloatStateOf(0f) }
    var menu by remember { mutableStateOf(false) }
    val length = item.plannedLength
    val minutes = if (stretch != 0f) PlanGrid.resized(length, stretch / pxPerMinute.toDouble()) else length
    val top = ((segment.from - from) * perMinute).dp
    val height = maxOf(18.dp, ((segment.to - segment.from + minutes - length) * perMinute).dp)
    val accent = accentOf(vm, item)
    val end = start.plusSeconds(minutes * 60L)
    val time = "${PlanGrid.hhmm(PlanGrid.minutes(start, zone))}–${PlanGrid.hhmm(PlanGrid.minutes(end, zone))}"

    fun move(byMinutes: Double = 0.0, byDays: Int = 0) {
        if (movable) vm.plan(item.id, PlanGrid.moved(start, byMinutes, byDays, zone))
    }

    fun resize(by: Double) {
        if (!movable) return
        val next = PlanGrid.resized(length, by)
        if (next != length) vm.plan(item.id, start, next)
    }

    Box(
        modifier
            .offset { IntOffset(dragX.roundToInt(), top.roundToPx() + dragY.roundToInt()) }
            .height(height)
            .zIndex(if (dragging || stretch != 0f) 10f else 0f)
            .alpha(if (item.isDone) 0.7f else 1f)
            .background(accent.copy(alpha = if (item.isDone) 0.12f else 0.28f), RoundedCornerShape(6.dp))
            .border(if (dragging) 2.dp else 0.5.dp, if (dragging) ViewColors.accent else accent.copy(alpha = 0.35f), RoundedCornerShape(6.dp))
            .drawBehind { drawRect(accent, size = Size(3.dp.toPx(), size.height)) }
            .pointerInput(item.id, movable) {
                if (!movable) return@pointerInput
                // Held first, so a plain swipe still scrolls the plan.
                detectDragGesturesAfterLongPress(
                    onDragStart = { dragging = true },
                    onDrag = { change, amount ->
                        change.consume()
                        dragX += amount.x
                        dragY += amount.y
                    },
                    onDragEnd = {
                        val target = (dayIndex + (dragX / columnPx).roundToInt()).coerceIn(0, days.size - 1)
                        val shift = (days[target].toEpochDay() - days[dayIndex].toEpochDay()).toInt()
                        val moved = PlanGrid.moved(start, dragY / pxPerMinute.toDouble(), shift, zone)
                        dragging = false
                        dragX = 0f
                        dragY = 0f
                        if (moved != start) vm.plan(item.id, moved)
                    },
                    onDragCancel = {
                        dragging = false
                        dragX = 0f
                        dragY = 0f
                    },
                )
            }
            .clickable { menu = true }
            .semantics {
                contentDescription = "${item.title}, $time" + if (item.isDone) ", done" else ""
            }
            .padding(start = 6.dp, end = 4.dp, top = 2.dp),
    ) {
        Column {
            if (height >= 34.dp) Text(time, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1)
            Text(
                (if (item.isDone) "✓ " else "") + titleWithIcon(item) + (item.energy?.let { " " + "⚡".repeat(it) } ?: ""),
                style = MaterialTheme.typography.labelMedium.copy(textDecoration = if (item.isDone) TextDecoration.LineThrough else null),
                fontWeight = FontWeight.Medium,
                maxLines = maxOf(1, ((height.value - if (height >= 34.dp) 20 else 6) / 15).toInt()),
                overflow = TextOverflow.Ellipsis,
            )
        }
        if (movable && segment.isLast && height >= 40.dp) {
            // The bottom edge: drag it to make the block longer or shorter.
            Box(
                Modifier.align(Alignment.BottomCenter).fillMaxWidth(0.6f).height(12.dp)
                    .pointerInput(item.id, length) {
                        detectVerticalDragGestures(
                            onVerticalDrag = { change, amount ->
                                change.consume()
                                stretch += amount
                            },
                            onDragEnd = {
                                val next = PlanGrid.resized(length, stretch / pxPerMinute.toDouble())
                                stretch = 0f
                                if (next != length) vm.plan(item.id, start, next)
                            },
                            onDragCancel = { stretch = 0f },
                        )
                    },
                contentAlignment = Alignment.Center,
            ) {
                Box(Modifier.width(28.dp).height(4.dp).background(MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.6f), RoundedCornerShape(50)))
            }
        }
        DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
            DropdownMenuItem(text = { Text("Open") }, onClick = { menu = false; open(item.id) })
            if (movable) {
                DropdownMenuItem(text = { Text("Change…") }, onClick = { menu = false; ask(PlanRequest(item.id, start, length)) })
                HorizontalDivider()
                DropdownMenuItem(text = { Text("15 minutes earlier") }, onClick = { menu = false; move(byMinutes = -15.0) })
                DropdownMenuItem(text = { Text("15 minutes later") }, onClick = { menu = false; move(byMinutes = 15.0) })
                DropdownMenuItem(text = { Text("Day before") }, onClick = { menu = false; move(byDays = -1) })
                DropdownMenuItem(text = { Text("Next day") }, onClick = { menu = false; move(byDays = 1) })
                DropdownMenuItem(text = { Text("Longer (+15 min)") }, onClick = { menu = false; resize(15.0) })
                DropdownMenuItem(text = { Text("Shorter (−15 min)") }, onClick = { menu = false; resize(-15.0) })
                HorizontalDivider()
                DropdownMenuItem(text = { Text("Take off the plan", color = ViewColors.danger) }, onClick = { menu = false; vm.unplan(item.id) })
            }
        }
    }
}

/** Plan a task at a time for a while, without dragging (or change a block, or take it off the plan). */
@Composable
private fun PlanDialog(vm: BoardViewModel, request: PlanRequest, choices: List<AgendaEntry>, onDismiss: () -> Unit) {
    var taskId by remember { mutableStateOf(request.taskId ?: choices.firstOrNull()?.item?.id) }
    var start by remember { mutableStateOf(request.start) }
    var minutes by remember { mutableStateOf(request.minutes ?: taskId?.let { vm.itemOf(it)?.plannedLength } ?: Planning.DEFAULT_MINUTES) }
    var picking by remember { mutableStateOf(false) }
    var choosingTask by remember { mutableStateOf(false) }
    var choosingLength by remember { mutableStateOf(false) }
    val lengths = (listOf(15, 30, 45, 60, 90, 120, 180, 240, 360, 480, 720) + minutes).distinct().sorted()
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(if (request.taskId == null) "Plan a task" else "Plan") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                val id = taskId
                if (request.taskId == null) {
                    if (choices.isEmpty()) {
                        Text("Everything to do now is planned.", color = MaterialTheme.colorScheme.onSurfaceVariant)
                    } else {
                        Box {
                            OutlinedButton(onClick = { choosingTask = true }, modifier = Modifier.fillMaxWidth()) {
                                Text(id?.let { vm.titleOf(it) } ?: "Pick a task", maxLines = 1, overflow = TextOverflow.Ellipsis)
                            }
                            DropdownMenu(expanded = choosingTask, onDismissRequest = { choosingTask = false }) {
                                choices.forEach { e -> DropdownMenuItem(text = { Text(e.item.title) }, onClick = { choosingTask = false; taskId = e.item.id }) }
                            }
                        }
                    }
                } else if (id != null) {
                    Text(vm.titleOf(id).orEmpty(), style = MaterialTheme.typography.titleMedium)
                }
                Text("Starts", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                OutlinedButton(onClick = { picking = true }, modifier = Modifier.fillMaxWidth()) { Text(vm.planWhen(start)) }
                Text("How long", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Box {
                    OutlinedButton(onClick = { choosingLength = true }, modifier = Modifier.fillMaxWidth()) { Text(PlanGrid.length(minutes)) }
                    DropdownMenu(expanded = choosingLength, onDismissRequest = { choosingLength = false }) {
                        lengths.forEach { m -> DropdownMenuItem(text = { Text(PlanGrid.length(m)) }, onClick = { choosingLength = false; minutes = m }) }
                    }
                }
                if (id != null && vm.itemOf(id)?.plannedAt != null) {
                    TextButton(onClick = { vm.unplan(id); onDismiss() }) { Text("Take off the plan", color = ViewColors.danger) }
                }
            }
        },
        confirmButton = { TextButton(onClick = { val id = taskId; if (id != null && vm.plan(id, start, minutes)) onDismiss() }, enabled = taskId != null) { Text("Plan") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
    if (picking) DateTimePicker("Starts", start, future = false, onPicked = { start = it }, onDismiss = { picking = false })
}

/** Work hours and days, and the day a week starts on (each group keeps its own). */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun PlanSettingsDialog(initial: PlanSettings, onSave: (PlanSettings) -> Unit, onDismiss: () -> Unit) {
    var s by remember { mutableStateOf(initial) }
    val names = listOf("Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun")
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Work hours and days") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp), modifier = Modifier.verticalScroll(rememberScrollState())) {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text("From", modifier = Modifier.width(44.dp))
                    TimeChoice(s.workStart, (0 until 24 * 60 step 30).toList()) { s = s.copy(workStart = it) }
                    Text("to")
                    TimeChoice(s.workEnd, (60..24 * 60 step 30).toList()) { s = s.copy(workEnd = it) }
                }
                Text("Week starts on", style = MaterialTheme.typography.labelMedium)
                SingleChoiceSegmentedButtonRow {
                    SegmentedButton(selected = s.weekStart == 1, onClick = { s = s.copy(weekStart = 1) }, shape = SegmentedButtonDefaults.itemShape(0, 2)) { Text("Monday") }
                    SegmentedButton(selected = s.weekStart == 7, onClick = { s = s.copy(weekStart = 7) }, shape = SegmentedButtonDefaults.itemShape(1, 2)) { Text("Sunday") }
                }
                Text("Work days", style = MaterialTheme.typography.labelMedium)
                @OptIn(androidx.compose.foundation.layout.ExperimentalLayoutApi::class)
                androidx.compose.foundation.layout.FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    (1..7).forEach { d ->
                        val on = d in s.workDays
                        FilterChip(selected = on, onClick = { s = s.copy(workDays = if (on) s.workDays - d else (s.workDays + d).sorted()) }, label = { Text(names[d - 1]) })
                    }
                }
                s.problem?.let { Text(it, color = ViewColors.danger, style = MaterialTheme.typography.bodySmall) }
            }
        },
        confirmButton = { TextButton(onClick = { onSave(s) }, enabled = s.problem == null) { Text("Save") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

@Composable
private fun TimeChoice(value: Int, choices: List<Int>, onPick: (Int) -> Unit) {
    var open by remember { mutableStateOf(false) }
    Box {
        OutlinedButton(onClick = { open = true }) { Text(PlanGrid.hhmm(value)) }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            choices.forEach { m -> DropdownMenuItem(text = { Text(PlanGrid.hhmm(m)) }, onClick = { open = false; onPick(m) }) }
        }
    }
}
