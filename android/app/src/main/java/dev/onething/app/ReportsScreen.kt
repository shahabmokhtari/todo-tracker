package dev.onething.app

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import dev.onething.core.BoardException
import dev.onething.core.PlanDays
import dev.onething.core.ReportDay
import dev.onething.core.TimeReport
import dev.onething.core.durationWords
import java.time.Duration
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.UUID

/** The ranges offered (the same as the other apps), and dates you pick. */
private val ranges = listOf("7" to "7 days", "28" to "4 weeks", "90" to "3 months", "365" to "Year", "custom" to "Dates…")

/** The colors the other apps give groups without one, and focus and other time. */
private object ReportColors {
    val fallback = listOf("#6366f1", "#22c55e", "#f59e0b", "#ec4899", "#0ea5e9", "#8b5cf6", "#14b8a6")
    val focus = Color(0xFF6366F1)
    val other = Color(0xFF22D3EE)

    fun group(vm: BoardViewModel, id: UUID): Color {
        val groups = vm.boardGroups
        val index = groups.indexOfFirst { it.id == id }.coerceAtLeast(0)
        return ViewColors.hex(groups.firstOrNull { it.id == id }?.color ?: fallback[index % fallback.size])
    }
}

private fun seconds(s: Long) = durationWords(Duration.ofSeconds(s))

/**
 * Reports (the web's reports.js, the iPhone's ReportsView): where the time went and what got done, for a range (7 days,
 * 4 weeks, 3 months, a year, or dates you pick) and the group shown: headline numbers, time per day (focus and other
 * time), per group, top tasks, when you work (weekday × hour), a calendar of active days, and the Timeline of the work on
 * each task (tap a row to open it). Worked out here from the board, the same numbers as everywhere
 * (tests/fixtures/reports.json), so it works offline too.
 */
@Composable
fun ReportsScreen(vm: BoardViewModel, wide: Boolean, open: (UUID) -> Unit) {
    val zone = ZoneId.systemDefault()
    val today = LocalDate.now(zone)
    var customFrom by rememberSaveable { mutableStateOf(today.minusDays(13).toEpochDay()) }
    var customTo by rememberSaveable { mutableStateOf(today.toEpochDay()) }
    var picking by remember { mutableStateOf<String?>(null) }
    val range = vm.reportRange
    val (from, to) = if (range == "custom") {
        val a = LocalDate.ofEpochDay(customFrom)
        val b = LocalDate.ofEpochDay(customTo)
        minOf(a, b) to maxOf(a, b)
    } else {
        today.minusDays(((range.toIntOrNull() ?: 28) - 1).toLong()) to today
    }
    // Worked out again for another range or group, a change to the board, or every half minute (a timer runs).
    val key = "$from|$to|${vm.selectedGroup}|${vm.version}|${vm.now.epochSecond / 30}"
    val result = remember(key) {
        try {
            Result.success(vm.report(from, to))
        } catch (e: BoardException) {
            Result.failure(e)
        }
    }
    Column(Modifier.fillMaxSize()) {
        ViewHeader("Reports", vm, onAdd = null)
        Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 16.dp, vertical = 8.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
            Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                ranges.forEach { (value, label) -> FilterChip(selected = range == value, onClick = { vm.chooseReportRange(value) }, label = { Text(label) }) }
            }
            if (range == "custom") {
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedButton(onClick = { picking = "from" }) { Text(LocalDate.ofEpochDay(customFrom).format(DateTimeFormatter.ofPattern("d MMM yyyy"))) }
                    Text("to", color = MaterialTheme.colorScheme.onSurfaceVariant)
                    OutlinedButton(onClick = { picking = "to" }) { Text(LocalDate.ofEpochDay(customTo).format(DateTimeFormatter.ofPattern("d MMM yyyy"))) }
                }
            }
            val span = DateTimeFormatter.ofPattern("d MMM yyyy")
            Text(if (from == to) from.format(span) else "${from.format(span)} – ${to.format(span)}", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            result.onFailure { Text(it.message ?: "This range can't be shown.", color = MaterialTheme.colorScheme.onSurfaceVariant) }
            result.onSuccess { ReportBody(vm, it, wide, open) }
            Spacer(Modifier.height(16.dp))
        }
    }
    picking?.let { which ->
        val day = LocalDate.ofEpochDay(if (which == "from") customFrom else customTo)
        DatePickerOnly(day, onPicked = { picked -> if (which == "from") customFrom = picked.toEpochDay() else customTo = picked.toEpochDay() }) { picking = null }
    }
}

/** A day on the calendar (for the report's dates). */
@OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
@Composable
private fun DatePickerOnly(day: LocalDate, onPicked: (LocalDate) -> Unit, onDismiss: () -> Unit) {
    val state = androidx.compose.material3.rememberDatePickerState(initialSelectedDateMillis = day.atStartOfDay(java.time.ZoneOffset.UTC).toInstant().toEpochMilli())
    androidx.compose.material3.DatePickerDialog(
        onDismissRequest = onDismiss,
        confirmButton = {
            androidx.compose.material3.TextButton(onClick = {
                state.selectedDateMillis?.let { onPicked(Instant.ofEpochMilli(it).atZone(java.time.ZoneOffset.UTC).toLocalDate()) }
                onDismiss()
            }) { Text("OK") }
        },
        dismissButton = { androidx.compose.material3.TextButton(onClick = onDismiss) { Text("Cancel") } },
    ) { androidx.compose.material3.DatePicker(state = state) }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun ReportBody(vm: BoardViewModel, r: TimeReport, wide: Boolean, open: (UUID) -> Unit) {
    val focus = r.days.sumOf { it.focusSeconds }
    val average = if (r.activeDays > 0) r.trackedSeconds / r.activeDays else 0
    BoxWithConstraints(Modifier.fillMaxWidth()) {
        val kpiWidth = if (maxWidth >= 920.dp) (maxWidth - 40.dp) / 4 else (maxWidth - 14.dp) / 2
        val cardWidth = if (maxWidth >= 840.dp) (maxWidth - 18.dp) / 2 else maxWidth
        Column(verticalArrangement = Arrangement.spacedBy(16.dp)) {
            FlowRow(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Kpi("Time tracked", seconds(r.trackedSeconds), if (average > 0) "${seconds(average)} per active day" else null, "⏱", Modifier.width(kpiWidth))
                Kpi("Focus sessions", "${r.focusSessions}", if (focus > 0) "${seconds(focus)} of focus" else null, "◎", Modifier.width(kpiWidth))
                Kpi("Tasks done", "${r.completed}", if (r.created > 0) "${r.created} new" else null, "✓", Modifier.width(kpiWidth))
                Kpi("Longest streak", "${r.longestStreak} day${if (r.longestStreak == 1) "" else "s"}", "${r.activeDays} active day${if (r.activeDays == 1) "" else "s"}", "🔥", Modifier.width(kpiWidth))
            }
            if (r.trackedSeconds == 0L && r.completed == 0) {
                Column(Modifier.fillMaxWidth().padding(32.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                    Text("▁▃▅", style = MaterialTheme.typography.headlineMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    Text(
                        "Nothing tracked here yet. Start a timer on a task (▶) or a focus session, and your reports fill in.",
                        textAlign = TextAlign.Center, color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            } else {
                FlowRow(horizontalArrangement = Arrangement.spacedBy(16.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
                    ReportCard("Time per day", "focus and other time · hours", Modifier.width(cardWidth)) { PerDayChart(r) }
                    ReportCard("By group", null, Modifier.width(cardWidth)) { GroupDonut(vm, r) }
                    ReportCard("Where the time went", "top tasks", Modifier.width(cardWidth)) { TopTasks(vm, r, open) }
                    ReportCard("When you work", "weekday × hour", Modifier.width(cardWidth)) { HourHeatmap(r.hourGrid) }
                    if (r.days.size >= 90) ReportCard("Active days", "time tracked or tasks done", Modifier.width(cardWidth)) { ActiveDays(r.days) }
                }
                ReportCard("Timeline", "bars: work · line: from created to done or due · ◆ due · ● done", Modifier.fillMaxWidth()) {
                    if (r.timeline.isEmpty()) {
                        Text("No task was worked on or finished in this range.", color = MaterialTheme.colorScheme.onSurfaceVariant)
                    } else {
                        TimelineChart(vm, r, compactLabels = !wide, open = open)
                    }
                }
            }
        }
    }
}

@Composable
private fun Kpi(title: String, value: String, sub: String?, icon: String, modifier: Modifier) {
    Column(
        modifier.heightIn(min = 112.dp).background(MaterialTheme.colorScheme.surfaceContainerLow, RoundedCornerShape(12.dp)).padding(12.dp)
            .semantics(mergeDescendants = true) {},
        verticalArrangement = Arrangement.spacedBy(2.dp),
    ) {
        Text(icon, color = ViewColors.accent)
        Text(value, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold, maxLines = 1)
        Text(title, style = MaterialTheme.typography.bodyMedium)
        sub?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
    }
}

@Composable
private fun ReportCard(title: String, subtitle: String?, modifier: Modifier, content: @Composable () -> Unit) {
    Column(modifier.background(MaterialTheme.colorScheme.surfaceContainerLow, RoundedCornerShape(14.dp)).padding(14.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Column {
            Text(title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
            subtitle?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
        }
        content()
    }
}

/** Hours a day (a week a bar for long ranges), focus and other time stacked. */
@Composable
private fun PerDayChart(r: TimeReport) {
    val perBar = if (r.days.size > 120) 7 else 1
    val bars = r.days.chunked(perBar).map { slice ->
        val f = slice.sumOf { it.focusSeconds }
        val all = slice.sumOf { it.trackedSeconds }
        Triple(slice.first().date, f / 3600.0, (all - f) / 3600.0)
    }
    val most = maxOf(0.5, bars.maxOfOrNull { it.second + it.third } ?: 0.5)
    val label = MaterialTheme.colorScheme.onSurfaceVariant
    val grid = MaterialTheme.colorScheme.outlineVariant
    Column {
        Canvas(Modifier.fillMaxWidth().height(180.dp).semantics { contentDescription = "Hours tracked per day" }) {
            val w = size.width / bars.size
            for (k in 1..3) {
                val y = size.height * (1 - k / 3f)
                drawLine(grid, Offset(0f, y), Offset(size.width, y), strokeWidth = 1f)
            }
            bars.forEachIndexed { i, (_, f, o) ->
                val x = i * w + w * 0.15f
                val bw = maxOf(1f, w * 0.7f)
                val fh = (f / most * size.height).toFloat()
                val oh = (o / most * size.height).toFloat()
                if (fh > 0) drawRect(ReportColors.focus, Offset(x, size.height - fh), Size(bw, fh))
                if (oh > 0) drawRect(ReportColors.other, Offset(x, size.height - fh - oh), Size(bw, oh))
            }
        }
        Row(Modifier.fillMaxWidth()) {
            val fmt = DateTimeFormatter.ofPattern("d MMM")
            Text(bars.first().first.format(fmt), style = MaterialTheme.typography.labelSmall, color = label, modifier = Modifier.weight(1f))
            Text("max ${"%.1f".format(most)} h", style = MaterialTheme.typography.labelSmall, color = label, modifier = Modifier.weight(1f), textAlign = TextAlign.Center)
            Text(bars.last().first.format(fmt), style = MaterialTheme.typography.labelSmall, color = label, modifier = Modifier.weight(1f), textAlign = TextAlign.End)
        }
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp), modifier = Modifier.padding(top = 6.dp)) {
            Legend(ReportColors.focus, "Focus")
            Legend(ReportColors.other, "Timer & logged")
        }
    }
}

@Composable
private fun Legend(color: Color, text: String) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(9.dp).background(color, CircleShape))
        Text(text, style = MaterialTheme.typography.labelSmall, modifier = Modifier.padding(start = 4.dp))
    }
}

@Composable
private fun GroupDonut(vm: BoardViewModel, r: TimeReport) {
    val total = maxOf(1L, r.trackedSeconds)
    val colors = r.groups.map { ReportColors.group(vm, it.groupId) }
    val track = MaterialTheme.colorScheme.surfaceVariant
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(16.dp)) {
        Box(Modifier.size(140.dp), contentAlignment = Alignment.Center) {
            Canvas(Modifier.fillMaxSize()) {
                val stroke = size.minDimension * 0.19f
                val inset = stroke / 2
                val arcSize = Size(size.width - stroke, size.height - stroke)
                if (r.groups.isEmpty()) drawArc(track, 0f, 360f, false, Offset(inset, inset), arcSize, style = Stroke(stroke))
                var angle = -90f
                r.groups.forEachIndexed { i, g ->
                    val sweep = 360f * g.trackedSeconds / total
                    drawArc(colors[i], angle + 0.75f, maxOf(0.5f, sweep - 1.5f), false, Offset(inset, inset), arcSize, style = Stroke(stroke))
                    angle += sweep
                }
            }
            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                Text(seconds(r.trackedSeconds), style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold)
                Text("tracked", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        Column(verticalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.weight(1f)) {
            r.groups.forEachIndexed { i, g ->
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Box(Modifier.size(9.dp).background(colors[i], CircleShape))
                    Text(g.name, style = MaterialTheme.typography.bodyMedium, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f).padding(start = 6.dp))
                    Text("${seconds(g.trackedSeconds)} · ${Math.round(g.trackedSeconds * 100.0 / total)}%", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
        }
    }
}

@Composable
private fun TopTasks(vm: BoardViewModel, r: TimeReport, open: (UUID) -> Unit) {
    val top = maxOf(1L, r.tasks.firstOrNull()?.trackedSeconds ?: 1L)
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        if (r.tasks.isEmpty()) Text("No time tracked in this range.", color = MaterialTheme.colorScheme.onSurfaceVariant)
        r.tasks.forEach { t ->
            Column(Modifier.fillMaxWidth().clickable { open(t.id) }.semantics(mergeDescendants = true) {}) {
                Row {
                    Text(t.title + if (t.done) " ✓" else "", style = MaterialTheme.typography.bodyMedium, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
                    Text(seconds(t.trackedSeconds), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                Box(Modifier.fillMaxWidth(maxOf(0.02f, (t.trackedSeconds.toFloat() / top))).height(6.dp).padding(top = 2.dp).background(ReportColors.group(vm, t.groupId), RoundedCornerShape(50)))
            }
        }
    }
}

/** Weekday × hour: when the work happens (Monday first). */
@Composable
private fun HourHeatmap(grid: List<List<Long>>) {
    val names = listOf("Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun")
    // The grid's weekdays start on Sunday (0); shown Monday first.
    val rows = (0 until 7).map { grid[(it + 1) % 7] }
    val most = maxOf(1L, rows.flatten().maxOrNull() ?: 1L)
    val label = MaterialTheme.colorScheme.onSurfaceVariant
    Column {
        rows.forEachIndexed { i, row ->
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(names[i], style = MaterialTheme.typography.labelSmall, color = label, modifier = Modifier.width(34.dp))
                Canvas(Modifier.weight(1f).height(18.dp)) {
                    val w = size.width / 24
                    row.forEachIndexed { h, s ->
                        val alpha = if (s == 0L) 0.06f else 0.2f + 0.8f * s / most
                        drawRoundRect(ViewColors.accent.copy(alpha = alpha), Offset(h * w + w * 0.05f, 1f), Size(w * 0.9f, size.height - 2f), CornerRadius(3f))
                    }
                }
            }
        }
        Row(Modifier.padding(start = 34.dp)) {
            listOf("00:00", "06:00", "12:00", "18:00", "24:00").forEachIndexed { i, t ->
                Text(t, style = MaterialTheme.typography.labelSmall, color = label, modifier = Modifier.weight(1f), textAlign = if (i == 4) TextAlign.End else TextAlign.Start)
            }
        }
    }
}

/** A calendar of the days with time tracked or tasks done (weeks across, weekdays down). */
@Composable
private fun ActiveDays(days: List<ReportDay>) {
    val first = days.first().date.let { it.minusDays((it.dayOfWeek.value - 1).toLong()) }
    val values = days.map { it.trackedSeconds + it.completed * 1800L }
    val most = maxOf(1L, values.maxOrNull() ?: 1L)
    val weeks = ((days.last().date.toEpochDay() - first.toEpochDay()) / 7 + 1).toInt()
    Canvas(Modifier.fillMaxWidth().height(110.dp).semantics { contentDescription = "Active days" }) {
        val w = size.width / weeks
        val h = size.height / 7
        days.forEachIndexed { i, d ->
            val week = ((d.date.toEpochDay() - first.toEpochDay()) / 7).toInt()
            val row = d.date.dayOfWeek.value - 1
            val alpha = if (values[i] == 0L) 0.08f else 0.25f + 0.75f * values[i] / most
            drawRoundRect(ViewColors.done.copy(alpha = alpha), Offset(week * w + w * 0.08f, row * h + h * 0.08f), Size(w * 0.84f, h * 0.84f), CornerRadius(2f))
        }
    }
}

/** The Timeline (Gantt): a row per task, its work as bars, a line from created to done (or due), ◆ due, ● done. */
@Composable
private fun TimelineChart(vm: BoardViewModel, r: TimeReport, compactLabels: Boolean, open: (UUID) -> Unit) {
    val zone = ZoneId.systemDefault()
    val rangeStart = PlanDays.midnight(r.from, zone)
    val rangeEnd = PlanDays.midnight(r.to.plusDays(1), zone)
    val total = (rangeEnd.toEpochMilli() - rangeStart.toEpochMilli()).toFloat()
    fun clamp(at: Instant) = minOf(maxOf(at, rangeStart), rangeEnd)
    fun x(at: Instant, width: Float) = (clamp(at).toEpochMilli() - rangeStart.toEpochMilli()) / total * width
    val rowHeight = 28.dp
    val labelWidth = if (compactLabels) 110.dp else 190.dp
    val lineColor = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.45f)
    val gridColor = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.5f)
    // A bar is at least this long, so a short stretch of work still shows.
    val minBar = if (r.days.size > 14) 3 * 3600_000L else 1200_000L
    Column {
        Row {
            Column(Modifier.width(labelWidth)) {
                r.timeline.forEach { row ->
                    Box(Modifier.height(rowHeight).fillMaxWidth().clickable { open(row.id) }, contentAlignment = Alignment.CenterEnd) {
                        Text(row.title, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis, textAlign = TextAlign.End, modifier = Modifier.padding(end = 8.dp))
                    }
                }
            }
            val colors = r.timeline.map { ReportColors.group(vm, it.groupId) }
            Canvas(
                Modifier.weight(1f).height(rowHeight * r.timeline.size)
                    .pointerInput(r.timeline) {
                        detectTapGestures { at ->
                            val i = (at.y / rowHeight.toPx()).toInt()
                            r.timeline.getOrNull(i)?.let { open(it.id) }
                        }
                    }
                    .semantics { contentDescription = "Timeline of ${r.timeline.size} tasks" },
            ) {
                val rh = rowHeight.toPx()
                for (k in 0..4) {
                    val gx = size.width * k / 4
                    drawLine(gridColor, Offset(gx, 0f), Offset(gx, size.height), strokeWidth = 1f)
                }
                r.timeline.forEachIndexed { i, row ->
                    val cy = i * rh + rh / 2
                    val lifeEnd = clamp(row.completedAt ?: row.deadline ?: rangeEnd)
                    val startX = x(row.createdAt, size.width)
                    drawLine(lineColor, Offset(startX, cy), Offset(maxOf(startX, x(lifeEnd, size.width)), cy), strokeWidth = 1.5.dp.toPx())
                    for (w in row.work) {
                        val end = maxOf(w.end.toEpochMilli(), w.start.toEpochMilli() + minBar)
                        val x0 = x(w.start, size.width)
                        val x1 = x(Instant.ofEpochMilli(end), size.width)
                        drawRoundRect(colors[i], Offset(x0, cy - 6.dp.toPx()), Size(maxOf(2f, x1 - x0), 12.dp.toPx()), CornerRadius(3.dp.toPx()))
                    }
                    row.deadline?.takeIf { !it.isBefore(rangeStart) && !it.isAfter(rangeEnd) }?.let { due ->
                        val dx = x(due, size.width)
                        val s = 5.dp.toPx()
                        val diamond = Path().apply {
                            moveTo(dx, cy - s); lineTo(dx + s, cy); lineTo(dx, cy + s); lineTo(dx - s, cy); close()
                        }
                        drawPath(diamond, ViewColors.warn)
                    }
                    row.completedAt?.takeIf { !it.isBefore(rangeStart) && !it.isAfter(rangeEnd) }?.let { done ->
                        drawCircle(ViewColors.done, radius = 4.5.dp.toPx(), center = Offset(x(done, size.width), cy))
                    }
                }
            }
        }
        // Dates along the bottom, under the lines they belong to.
        BoxWithConstraints(Modifier.padding(start = labelWidth, top = 4.dp).fillMaxWidth().height(16.dp)) {
            val fmt = DateTimeFormatter.ofPattern("d MMM")
            val span = rangeEnd.toEpochMilli() - rangeStart.toEpochMilli() - 1
            val width = maxWidth
            (0..4).forEach { k ->
                val at = rangeStart.plusMillis(span * k / 4).atZone(zone).toLocalDate()
                val x = width * (k / 4f)
                val label = 44.dp
                Text(
                    at.format(fmt), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1,
                    textAlign = TextAlign.Center,
                    modifier = Modifier.width(label).offset(x = (x - label / 2).coerceIn(0.dp, width - label)),
                )
            }
        }
    }
}
