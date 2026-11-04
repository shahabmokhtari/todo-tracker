package dev.onething.app

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import dev.onething.core.DoneDays
import dev.onething.core.WorkItem
import dev.onething.core.durationWords
import java.time.Duration
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.UUID

/**
 * Done (the web's done.js, the iPhone's DoneView): what got finished, day by day, with the time it took; Reopen or
 * Archive each. The Archive tab shows what was put away, to bring back.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DoneScreen(vm: BoardViewModel, open: (UUID) -> Unit) {
    @Suppress("UNUSED_VARIABLE") val redraw = vm.version
    var archive by rememberSaveable { mutableStateOf(false) }
    var filter by rememberSaveable { mutableStateOf("") }
    val scope = vm.scope
    val finished = scope.finishedRoots
    val archived = scope.archivedRoots
    val all = if (archive) archived else finished
    val list = if (filter.isBlank()) all else all.filter { it.title.contains(filter.trim(), ignoreCase = true) }
    val spent = list.fold(Duration.ZERO) { sum, item -> sum.plus(item.timeSpent(vm.now)) }
    val zone = ZoneId.systemDefault()
    val dateFormat = DateTimeFormatter.ofPattern("EEEE d MMMM")
    Column(Modifier.fillMaxSize()) {
        ViewHeader("Done", vm, onAdd = null)
        LazyColumn(Modifier.fillMaxSize().imePadding(), contentPadding = androidx.compose.foundation.layout.PaddingValues(start = 16.dp, end = 16.dp, bottom = 24.dp)) {
            item {
                SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth().padding(top = 4.dp)) {
                    SegmentedButton(selected = !archive, onClick = { archive = false }, shape = SegmentedButtonDefaults.itemShape(0, 2)) { Text("Done (${finished.size})") }
                    SegmentedButton(selected = archive, onClick = { archive = true }, shape = SegmentedButtonDefaults.itemShape(1, 2)) { Text("Archive (${archived.size})") }
                }
            }
            item {
                OutlinedTextField(
                    value = filter, onValueChange = { filter = it }, singleLine = true,
                    placeholder = { Text(if (archive) "Find an archived task" else "Find a finished task") },
                    modifier = Modifier.fillMaxWidth().padding(vertical = 8.dp),
                )
            }
            if (list.isNotEmpty()) {
                item {
                    Text(
                        "${list.size} task${if (list.size == 1) "" else "s"}${if (spent >= Duration.ofMinutes(1)) " · ${durationWords(spent)} spent" else ""}",
                        style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            } else {
                item {
                    Column(Modifier.fillMaxWidth().padding(vertical = 40.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                        Text(if (archive) "🗄" else "✓", style = MaterialTheme.typography.displaySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        Text(
                            if (filter.isNotBlank()) "Nothing matches" else if (archive) "Nothing archived" else "Nothing finished yet",
                            style = MaterialTheme.typography.titleMedium,
                        )
                        if (filter.isBlank()) {
                            Text(
                                if (archive) "Archive finished tasks to keep Done short." else "It will show up here, day by day.",
                                style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    }
                }
            }
            DoneDays.byDay(list, vm.now, zone) { it.format(dateFormat) }.forEach { (label, items) ->
                item(key = "day-$label") {
                    Text("$label · ${items.size}", style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 16.dp, bottom = 4.dp))
                }
                items(items, key = { it.id }) { item -> DoneRow(vm, item, archive, open) }
            }
        }
    }
}

@Composable
private fun DoneRow(vm: BoardViewModel, item: WorkItem, archive: Boolean, open: (UUID) -> Unit) {
    val context = LocalContext.current
    Column {
        Row(Modifier.fillMaxWidth().clickable { open(item.id) }.padding(vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(if (archive) "🗄" else "✓", color = if (archive) MaterialTheme.colorScheme.onSurfaceVariant else ViewColors.done, style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(end = 10.dp))
            Column(Modifier.weight(1f)) {
                Text(titleWithIcon(item), style = MaterialTheme.typography.bodyLarge, maxLines = 2)
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    Box(Modifier.size(7.dp).background(ViewColors.hex(vm.groupColors[item.groupId]), CircleShape))
                    val parts = listOfNotNull(
                        vm.groupName(item.groupId),
                        item.selfAndDescendants.drop(1).takeIf { it.isNotEmpty() }?.let { s -> "${s.count { it.isDone }}/${s.size} steps" },
                        item.timeSpent(vm.now).takeIf { it >= Duration.ofMinutes(1) }?.let { "⏱ ${durationWords(it)}" },
                        item.completedAt?.let { android.text.format.DateFormat.getTimeFormat(context).format(java.util.Date(it.toEpochMilli())) },
                    )
                    Text(parts.joinToString(" · "), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1)
                }
            }
            if (archive) {
                TextButton(onClick = { vm.unarchive(item.id) }, modifier = Modifier.semantics { contentDescription = "Unarchive ${item.title}" }) { Text("Unarchive", fontWeight = FontWeight.Medium) }
            } else {
                TextButton(onClick = { vm.reopen(item.id) }, modifier = Modifier.semantics { contentDescription = "Reopen ${item.title}" }) { Text("Reopen") }
                TextButton(onClick = { vm.archive(item.id) }, modifier = Modifier.semantics { contentDescription = "Archive ${item.title}" }) { Text("Archive") }
            }
        }
        HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.5f))
    }
}
