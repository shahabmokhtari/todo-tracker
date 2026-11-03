package dev.onething.app

import android.content.Context
import android.hardware.Sensor
import android.hardware.SensorEvent
import android.hardware.SensorEventListener
import android.hardware.SensorManager
import android.os.Build
import android.util.Log
import android.widget.Toast
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import dev.onething.BuildConfig
import dev.onething.core.Feedback
import dev.onething.core.FeedbackClient
import java.util.Locale
import java.util.UUID
import kotlin.math.sqrt
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * Send feedback: Bug, Idea or Other, a note, an optional email, sent to the owner's issues relay. Kept while open
 * (rotation too); a note that couldn't go out stays, and sending again uses the same clientID (never a second issue).
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun FeedbackSheet(source: String, onClose: () -> Unit) {
    val context = LocalContext.current
    val configuration = LocalConfiguration.current
    var kind by rememberSaveable { mutableStateOf(Feedback.Kind.BUG.id) }
    var message by rememberSaveable { mutableStateOf("") }
    var contact by rememberSaveable { mutableStateOf("") }
    // Made once per sheet: a retry is the same note to the relay.
    val clientId by rememberSaveable { mutableStateOf(UUID.randomUUID().toString()) }
    var problem by rememberSaveable { mutableStateOf<String?>(null) }
    var sending by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    val sheet = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val length = Feedback.length(message)
    val canSend = !sending && Feedback.cleanMessage(message).isNotEmpty()

    fun send() {
        sending = true
        problem = null
        val draft = Feedback.Draft(Feedback.Kind.of(kind) ?: Feedback.Kind.OTHER, message, contact, source, clientId)
        val device = Feedback.Device(
            platform = "Android",
            appVersion = BuildConfig.VERSION_NAME,
            build = BuildConfig.VERSION_CODE.toString(),
            os = "Android ${Build.VERSION.RELEASE}",
            device = Build.MODEL,
            language = Locale.getDefault().toLanguageTag(),
            screen = "${configuration.screenWidthDp}x${configuration.screenHeightDp}",
        )
        val client = FeedbackClient(
            sendForReal = Feedback.sendsForReal(BuildConfig.DEBUG, BuildConfig.FEEDBACK_SEND),
            log = { Log.i("One Thing", it) },
        )
        scope.launch {
            val outcome = withContext(Dispatchers.IO) { client.send(draft, device) }
            sending = false
            if (outcome == Feedback.Outcome.SENT) {
                Toast.makeText(context, Feedback.SENT_TEXT, Toast.LENGTH_LONG).show()
                onClose()
            } else {
                problem = Feedback.text(outcome)
            }
        }
    }

    ModalBottomSheet(onDismissRequest = onClose, sheetState = sheet) {
        Column(
            Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).imePadding().navigationBarsPadding().padding(horizontal = 20.dp, vertical = 8.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Text("Send feedback", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Feedback.Kind.entries.forEach { k ->
                    FilterChip(selected = kind == k.id, onClick = { kind = k.id }, label = { Text(k.label) })
                }
            }
            OutlinedTextField(
                value = message,
                onValueChange = { message = it },
                label = { Text("What’s on your mind?") },
                minLines = 4,
                isError = length > Feedback.MAX_MESSAGE,
                supportingText = if (length > Feedback.MAX_MESSAGE - Feedback.COUNTER_FROM) {
                    { Text("$length / ${Feedback.MAX_MESSAGE}") }
                } else {
                    null
                },
                modifier = Modifier.fillMaxWidth().heightIn(min = 120.dp),
            )
            OutlinedTextField(
                value = contact,
                onValueChange = { contact = it },
                label = { Text("Email (optional)") },
                placeholder = { Text(Feedback.CONTACT_HINT) },
                supportingText = { Text(Feedback.CONTACT_HINT) },
                singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Email),
                modifier = Modifier.fillMaxWidth(),
            )
            Text(Feedback.FINE_PRINT, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            problem?.let {
                Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.semantics { liveRegion = LiveRegionMode.Polite })
            }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End, verticalAlignment = Alignment.CenterVertically) {
                Button(onClick = ::send, enabled = canSend) { Text(if (sending) "Sending…" else "Send") }
            }
        }
    }
}

/**
 * A firm shake: two jolts over 2.5 g within a second. Then quiet for two seconds, so one shake opens one sheet.
 */
class ShakeDetector(private val onShake: () -> Unit) : SensorEventListener {
    private var firstJolt: Long? = null
    private var lastShake: Long? = null

    fun jolt(x: Float, y: Float, z: Float, now: Long) {
        val g = sqrt(x * x + y * y + z * z) / SensorManager.GRAVITY_EARTH
        if (g < THRESHOLD_G || lastShake?.let { now - it < QUIET_MS } == true) return
        val first = firstJolt
        if (first != null && now - first in MIN_GAP_MS..WINDOW_MS) {
            firstJolt = null
            lastShake = now
            onShake()
        } else if (first == null || now - first > WINDOW_MS) {
            firstJolt = now
        }
    }

    override fun onSensorChanged(event: SensorEvent) = jolt(event.values[0], event.values[1], event.values[2], System.currentTimeMillis())

    override fun onAccuracyChanged(sensor: Sensor?, accuracy: Int) {}

    private companion object {
        const val THRESHOLD_G = 2.5f
        const val MIN_GAP_MS = 100L
        const val WINDOW_MS = 1000L
        const val QUIET_MS = 2000L
    }
}

/** Listens for a shake only while [enabled] and the app is in front (never in the background). */
@Composable
fun ShakeToFeedback(enabled: Boolean, onShake: () -> Unit) {
    val context = LocalContext.current
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    val shaken by rememberUpdatedState(onShake)
    DisposableEffect(enabled, lifecycle) {
        val sensors = context.getSystemService(Context.SENSOR_SERVICE) as? SensorManager
        val accelerometer = sensors?.getDefaultSensor(Sensor.TYPE_ACCELEROMETER)
        if (!enabled || sensors == null || accelerometer == null) return@DisposableEffect onDispose {}
        val detector = ShakeDetector { shaken() }
        var listening = false
        fun listen(on: Boolean) {
            if (on == listening) return
            listening = on
            if (on) sensors.registerListener(detector, accelerometer, SensorManager.SENSOR_DELAY_UI) else sensors.unregisterListener(detector)
        }
        val observer = LifecycleEventObserver { _, _ -> listen(lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)) }
        lifecycle.addObserver(observer)
        onDispose {
            lifecycle.removeObserver(observer)
            listen(false)
        }
    }
}
