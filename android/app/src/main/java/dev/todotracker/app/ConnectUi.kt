package dev.todotracker.app

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import dev.todotracker.core.RemoteState
import java.time.ZoneId

/** Opens the camera to scan the pairing QR code; what it reads is offered as a pairing link (asked first). */
@Composable
fun rememberPairingScanner(vm: BoardViewModel): () -> Unit {
    val launcher = rememberLauncherForActivityResult(ScanContract()) { result -> result.contents?.let { vm.offerPairing(it) } }
    return {
        launcher.launch(
            ScanOptions()
                .setDesiredBarcodeFormats(ScanOptions.QR_CODE)
                .setPrompt("Scan the code in Settings › Phones on your computer")
                .setBeepEnabled(false)
                .setOrientationLocked(false),
        )
    }
}

/** Settings › Connect to a computer: use the tasks on a computer (scan its code or paste its link), or stop. */
@Composable
fun ConnectToComputer(vm: BoardViewModel) {
    val scan = rememberPairingScanner(vm)
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text("Connect to a computer", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
        val computer = vm.computer
        if (vm.isRemote && computer != null) {
            Text(
                "You're using the tasks on $computer, over its Wi-Fi or Tailscale. Changes you make here are made there. Your tasks on this phone are kept as they were, for when you disconnect.",
                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Text("This phone shows there as “${vm.phoneName}”.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            vm.remoteBanner?.let { Text(it, style = MaterialTheme.typography.bodyMedium) }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedButton(onClick = { vm.poll() }) { Text("Check now") }
                OutlinedButton(onClick = { vm.disconnect() }) { Text("Disconnect") }
            }
        } else {
            Text(
                "Use the tasks on your computer from this phone, at home on the same Wi-Fi or anywhere with Tailscale. On the computer, open Todo Tracker › Settings › Phones › Connect a phone, then scan its code.",
                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            if (vm.syncOn) {
                Text(
                    "This phone syncs on its own now. Connecting turns that off (your computer syncs the tasks then); the tasks on this phone stay here.",
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            var pasted by rememberSaveable { mutableStateOf("") }
            PhoneNameField(vm)
            Button(onClick = scan) { Text("Scan the code") }
            OutlinedTextField(
                value = pasted, onValueChange = { pasted = it }, singleLine = true, modifier = Modifier.fillMaxWidth(),
                label = { Text("Or paste the link (todotracker://pair…)") },
            )
            OutlinedButton(onClick = { vm.offerPairing(pasted) }, enabled = pasted.isNotBlank()) { Text("Connect") }
        }
    }
}

/** This phone's name, as the computer will list it (Settings › Phones there, and its timeline). */
@Composable
private fun PhoneNameField(vm: BoardViewModel) {
    OutlinedTextField(
        value = vm.phoneName, onValueChange = { vm.changePhoneName(it) }, singleLine = true, enabled = !vm.pairingBusy,
        modifier = Modifier.fillMaxWidth(), label = { Text("This phone's name") },
        supportingText = { Text("Your computer lists the phone by this name.") },
    )
}

/** "Connect to <computer>?", asked before a pairing link is used (a link can come from anywhere). */
@Composable
fun PairingDialog(vm: BoardViewModel) {
    val link = vm.pairingLink ?: return
    AlertDialog(
        onDismissRequest = { vm.cancelPairing() },
        title = { Text("Connect to ${link.computer}?") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("This phone will show the tasks on ${link.computer}, and changes you make here are made there. Your tasks on this phone stay here, untouched, for when you disconnect." + if (vm.syncOn) " Sync on this phone turns off." else "")
                PhoneNameField(vm)
                vm.pairingProblem?.let { Text(it, color = MaterialTheme.colorScheme.error) }
                if (vm.pairingBusy) {
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        CircularProgressIndicator(Modifier.padding(4.dp))
                        Text("Connecting…")
                    }
                }
            }
        },
        confirmButton = { TextButton(onClick = { vm.confirmPairing() }, enabled = !vm.pairingBusy) { Text(if (vm.pairingProblem != null) "Try again" else "Connect") } },
        dismissButton = { TextButton(onClick = { vm.cancelPairing() }, enabled = !vm.pairingBusy) { Text("Cancel") } },
    )
}

/** Over the tasks: the computer can't be reached (what's shown, and that changes wait), or it disconnected this phone. */
@Composable
fun RemoteBanner(vm: BoardViewModel) {
    val scan = rememberPairingScanner(vm)
    val revoked = vm.revokedFrom
    val text = vm.remoteBanner ?: revoked?.let { RemoteState.banner(RemoteState.Revoked, it, vm.now, ZoneId.systemDefault()) } ?: return
    Surface(color = MaterialTheme.colorScheme.secondaryContainer, shape = MaterialTheme.shapes.medium, modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp)) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Text(text, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSecondaryContainer)
            if (vm.remoteBanner != null) {
                TextButton(onClick = { vm.poll() }) { Text("Try again") }
            } else {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    TextButton(onClick = scan) { Text("Reconnect") }
                    TextButton(onClick = { vm.dismissRevoked() }) { Text("OK") }
                }
            }
        }
    }
}
