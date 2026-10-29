package dev.todotracker.app

import android.content.Intent
import android.net.Uri
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
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
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import dev.todotracker.core.sync.PhoneMode
import dev.todotracker.core.sync.SyncSettings
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle

/** Settings: how this phone gets its tasks (one of three), and the way into the Sync screen. */
@Composable
fun SyncSection(vm: BoardViewModel, onOpen: () -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text("Sync with your other devices", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
        Text(modeLine(vm), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        if (vm.canSync) OutlinedButton(onClick = onOpen) { Text(if (vm.syncOn) "Sync…" else "Set up sync…") }
    }
}

private fun modeLine(vm: BoardViewModel): String = when (vm.mode) {
    PhoneMode.CONNECTED -> "This phone uses the tasks on ${vm.computer ?: "your computer"}, so it doesn't sync on its own (the computer does)."
    PhoneMode.SYNCING -> "This phone syncs its tasks on its own with your computers and other phones" + (vm.syncStatus.lastSync?.let { " · last synced ${syncAgo(it, vm.now)}" } ?: "") + "."
    PhoneMode.ON_THIS_PHONE -> "Your tasks are only on this phone. Sync keeps them in step with your computers and other phones, no computer running needed."
}

/** "just now", "5 minutes ago", "3 hours ago", else the date. */
fun syncAgo(at: Instant, now: Instant): String {
    val d = Duration.between(at, now)
    return when {
        d.toMinutes() < 1 -> "just now"
        d.toMinutes() < 60 -> "${d.toMinutes()} minute${if (d.toMinutes() == 1L) "" else "s"} ago"
        d.toHours() < 24 -> "${d.toHours()} hour${if (d.toHours() == 1L) "" else "s"} ago"
        else -> DateTimeFormatter.ofLocalizedDateTime(FormatStyle.MEDIUM, FormatStyle.SHORT).withZone(ZoneId.systemDefault()).format(at)
    }
}

/** Settings › Sync with your other devices: set it up (a GitHub gist), or see how it goes, sync now, forget a device, turn it off. */
@Composable
fun SyncScreen(vm: BoardViewModel, onClose: () -> Unit) {
    BackHandler(onBack = onClose)
    Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        Column(Modifier.safeDrawingPadding()) {
            Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                TextButton(onClick = onClose) { Text("Back") }
                Text("Sync", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold, modifier = Modifier.padding(start = 8.dp))
            }
            Column(Modifier.verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
                when (vm.mode) {
                    PhoneMode.CONNECTED -> Connected(vm)
                    PhoneMode.SYNCING -> Syncing(vm)
                    PhoneMode.ON_THIS_PHONE -> SetUp(vm)
                }
            }
        }
    }
}

@Composable
private fun Hint(text: String) = Text(text, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)

@Composable
private fun Heading(text: String) = Text(text, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)

@Composable
private fun Connected(vm: BoardViewModel) {
    val computer = vm.computer ?: "your computer"
    Heading("Connected to $computer")
    Hint("This phone shows the tasks on $computer, and $computer syncs them with your other devices. A phone either uses a computer's tasks or syncs its own, not both.")
    Hint("To sync this phone's own tasks instead, disconnect first. Your tasks on this phone are still here, as you left them.")
    OutlinedButton(onClick = { vm.disconnect() }) { Text("Disconnect from $computer") }
}

/** Off: what sync does, and turning it on through a gist (the token first). */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun SetUp(vm: BoardViewModel) {
    val context = LocalContext.current
    var place by rememberSaveable { mutableStateOf(SyncSettings.GIST) }
    // Not saveable: the token never goes into the saved screen state.
    var token by remember { mutableStateOf("") }
    var library by rememberSaveable { mutableStateOf(vm.syncLibrary) }
    Heading("Sync with your other devices")
    Hint("Your tasks stay on this phone and sync with your computers and other phones through a private GitHub gist, even when no computer is on. Changes made on more than one device are merged.")
    if (SyncHub.folderAllowed) {
        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            FilterChip(selected = place == SyncSettings.GIST, onClick = { place = SyncSettings.GIST }, label = { Text("A private GitHub gist") })
            FilterChip(selected = place == SyncSettings.FOLDER, onClick = { place = SyncSettings.FOLDER }, label = { Text("A folder (test builds)") })
        }
    }
    if (place == SyncSettings.GIST) {
        Text("1. Make a GitHub token", style = MaterialTheme.typography.bodyLarge, fontWeight = FontWeight.Medium)
        Hint("GitHub opens with the gist scope chosen (that's all Todo Tracker needs). Choose when it expires, then Generate token, and copy it.")
        OutlinedButton(onClick = { context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(SyncHub.NEW_TOKEN_URL))) }) { Text("Open GitHub") }
        Text("2. Paste it here", style = MaterialTheme.typography.bodyLarge, fontWeight = FontWeight.Medium)
        TokenField(token) { token = it }
    } else {
        Hint("Syncs through ${SyncHub.debugFolder(context, library.ifBlank { SyncSettings.DEFAULT_LIBRARY }).path} (copy it to and from a computer's Apps/TodoTrackerSync folder to try sync).")
    }
    LibraryField(library) { library = it }
    vm.syncMessage?.let { Text(it, color = MaterialTheme.colorScheme.error) }
    Button(onClick = {
        vm.turnOnSync(place, token, library)
        token = ""
    }, enabled = place != SyncSettings.GIST || token.isNotBlank()) { Text("Turn on sync") }
    Hint("Joining a library adds this phone's tasks to it: the first sync brings in the tasks your other devices have, then adds this phone's beside them (nothing is replaced).")
}

@Composable
private fun TokenField(value: String, onChange: (String) -> Unit) {
    OutlinedTextField(
        value = value, onValueChange = onChange, singleLine = true, modifier = Modifier.fillMaxWidth(),
        label = { Text("GitHub token") }, visualTransformation = PasswordVisualTransformation(),
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
        supportingText = { Text("Kept encrypted on this phone, and sent only to GitHub.") },
    )
}

@Composable
private fun LibraryField(value: String, onChange: (String) -> Unit) {
    OutlinedTextField(
        value = value, onValueChange = onChange, singleLine = true, modifier = Modifier.fillMaxWidth(),
        label = { Text("Library") },
        supportingText = { Text("Devices with the same library name sync together. Your computers use the name of their tasks folder (“${SyncSettings.DEFAULT_LIBRARY}” unless you changed it).") },
    )
}

/** On: how it goes, Sync now, the other devices, what changed on both, the library, and turning it off. */
@Composable
private fun Syncing(vm: BoardViewModel) {
    val status = vm.syncStatus
    var forgetting by rememberSaveable { mutableStateOf<String?>(null) }
    var turningOff by rememberSaveable { mutableStateOf(false) }
    Heading(if (vm.syncPlace == SyncSettings.FOLDER) "Syncing through a folder" else "Syncing through a private GitHub gist")
    Hint("Library “${vm.syncLibrary}”. It syncs when you open the app, a few seconds after a change, every minute while it's open, and now and then when it's closed.")
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        Button(onClick = { vm.syncNow() }, enabled = !vm.syncBusy) { Text("Sync now") }
        if (vm.syncBusy) {
            CircularProgressIndicator(Modifier.padding(4.dp))
            Text("Syncing…")
        } else {
            Text(status.lastSync?.let { "Last synced ${syncAgo(it, vm.now)}" } ?: "Not synced yet", style = MaterialTheme.typography.bodyMedium)
        }
    }
    if (!vm.syncJoined && !vm.syncBusy) Hint("This phone's tasks go out after its first complete sync.")
    vm.syncNotice?.let { Text(it, style = MaterialTheme.typography.bodyMedium) }
    status.lastError?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodyMedium) }
    vm.syncMessage?.takeIf { it != status.lastError }?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodyMedium) }

    if (vm.syncNeedsToken || status.lastError?.contains("sign-in") == true || status.lastError?.contains("Sign in") == true) {
        var token by remember { mutableStateOf("") }
        val context = LocalContext.current
        Text("Sign in to GitHub again", style = MaterialTheme.typography.bodyLarge, fontWeight = FontWeight.Medium)
        OutlinedButton(onClick = { context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(SyncHub.NEW_TOKEN_URL))) }) { Text("Make a new token") }
        TokenField(token) { token = it }
        OutlinedButton(onClick = {
            vm.turnOnSync(SyncSettings.GIST, token, vm.syncLibrary)
            token = ""
        }, enabled = token.isNotBlank()) { Text("Use this token") }
    }

    HorizontalDivider()
    Heading("Your other devices")
    if (status.devices.isEmpty()) Hint("None yet. On a computer, open Todo Tracker › Sync and choose GitHub gist with the same library name.")
    status.devices.forEach { d ->
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(d.name.ifBlank { "A device" }, style = MaterialTheme.typography.bodyLarge)
                Hint("Last synced ${syncAgo(d.at, vm.now)}")
            }
            TextButton(onClick = { forgetting = d.device }) { Text("Forget") }
        }
    }

    if (status.conflicts.isNotEmpty()) {
        HorizontalDivider()
        Heading("Changed on both")
        Hint("These were changed on this phone and on another device at the same place. Both versions are kept in the task; open it on a computer to choose one (Keep mine or Keep theirs).")
        status.conflicts.forEach { c ->
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(c.path.substringAfterLast('/').removeSuffix(".md"), style = MaterialTheme.typography.bodyLarge)
                    Hint("With ${c.peerName.ifBlank { "another device" }}, ${syncAgo(c.at, vm.now)}")
                }
                TextButton(onClick = { vm.dismissConflict(c.key) }) { Text("Got it") }
            }
        }
    }

    HorizontalDivider()
    var library by rememberSaveable(vm.syncLibrary) { mutableStateOf(vm.syncLibrary) }
    LibraryField(library) { library = it }
    OutlinedButton(onClick = { vm.changeSyncLibrary(library) }, enabled = library.isNotBlank() && library.trim() != vm.syncLibrary) { Text("Use this library") }
    Hint("Using another library adds this phone's tasks to it too (they stay in this one as well).")

    HorizontalDivider()
    OutlinedButton(onClick = { turningOff = true }) { Text("Turn sync off") }
    Hint("Your tasks stay on this phone; they just stop syncing.")

    forgetting?.let { id ->
        val name = status.devices.firstOrNull { it.device == id }?.name ?: "this device"
        AlertDialog(
            onDismissRequest = { forgetting = null },
            title = { Text("Forget $name?") },
            text = { Text("Its changes are no longer merged here, and it's taken off the list. If it syncs again, it comes back. Nothing is deleted from your tasks.") },
            confirmButton = { TextButton(onClick = { vm.forgetDevice(id); forgetting = null }) { Text("Forget") } },
            dismissButton = { TextButton(onClick = { forgetting = null }) { Text("Cancel") } },
        )
    }
    if (turningOff) {
        AlertDialog(
            onDismissRequest = { turningOff = false },
            title = { Text("Turn sync off?") },
            text = { Text("Your tasks stay on this phone as they are. Changes here no longer reach your other devices, and theirs no longer come here. The GitHub token is forgotten on this phone.") },
            confirmButton = { TextButton(onClick = { vm.turnOffSync(); turningOff = false }) { Text("Turn off") } },
            dismissButton = { TextButton(onClick = { turningOff = false }) { Text("Cancel") } },
        )
    }
}
