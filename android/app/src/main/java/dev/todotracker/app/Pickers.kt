package dev.todotracker.app

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DatePicker
import androidx.compose.material3.DatePickerDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SelectableDates
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TimePicker
import androidx.compose.material3.rememberDatePickerState
import androidx.compose.material3.rememberTimePickerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import java.time.Instant
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.LocalTime
import java.time.ZoneId
import java.time.ZoneOffset

/**
 * A day, then a time. With [future] (snoozes, reminders), days before today can't be picked and a time that's already
 * past today can't be saved (it says so); a deadline can be any day.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DateTimePicker(title: String, start: Instant, future: Boolean, onPicked: (Instant) -> Unit, onDismiss: () -> Unit) {
    val zone = ZoneId.systemDefault()
    val local = start.atZone(zone)
    val today = LocalDate.now(zone)
    var day by remember { mutableStateOf<LocalDate?>(null) }
    // The calendar works in UTC days.
    val dates = rememberDatePickerState(
        initialSelectedDateMillis = local.toLocalDate().atStartOfDay(ZoneOffset.UTC).toInstant().toEpochMilli(),
        selectableDates = object : SelectableDates {
            override fun isSelectableDate(utcTimeMillis: Long): Boolean =
                !future || !Instant.ofEpochMilli(utcTimeMillis).atZone(ZoneOffset.UTC).toLocalDate().isBefore(today)

            override fun isSelectableYear(year: Int): Boolean = !future || year >= today.year
        },
    )
    val picked = day
    if (picked == null) {
        DatePickerDialog(
            onDismissRequest = onDismiss,
            confirmButton = {
                TextButton(
                    onClick = { dates.selectedDateMillis?.let { day = Instant.ofEpochMilli(it).atZone(ZoneOffset.UTC).toLocalDate() } },
                    enabled = dates.selectedDateMillis != null,
                ) { Text("Next") }
            },
            dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
        ) {
            DatePicker(state = dates, title = { Text(title, modifier = Modifier.padding(start = 24.dp, top = 16.dp)) })
        }
        return
    }
    val context = LocalContext.current
    val times = rememberTimePickerState(local.hour, local.minute, android.text.format.DateFormat.is24HourFormat(context))
    val at = LocalDateTime.of(picked, LocalTime.of(times.hour, times.minute)).atZone(zone).toInstant()
    val past = future && !at.isAfter(Instant.now())
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = {
            Column {
                TimePicker(state = times)
                // Said before Save, not after: a past time would bring the task straight back.
                if (past) Text("That time has passed. Pick a later one.", color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
            }
        },
        confirmButton = { TextButton(onClick = { onPicked(at); onDismiss() }, enabled = !past) { Text("Save") } },
        dismissButton = { TextButton(onClick = { day = null }) { Text("Back") } },
    )
}
