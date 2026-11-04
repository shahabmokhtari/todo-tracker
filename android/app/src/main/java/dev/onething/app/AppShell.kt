package dev.onething.app

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.consumeWindowInsets
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.width
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.DateRange
import androidx.compose.material.icons.filled.Email
import androidx.compose.material.icons.filled.Home
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.ListItem
import androidx.compose.material3.ListItemDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationRail
import androidx.compose.material3.NavigationRailItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarDuration
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.SnackbarResult
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.VerticalDivider
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import java.util.UUID

/** The app's views, in the other apps' order (the web's shell.js, the iPhone's tabs and the iPad's sidebar). */
enum class AppView(val title: String) {
    TODAY("Today"),
    BOARD("Board"),
    TASKS("Tasks"),
    DONE("Done"),
    PLAN("Plan"),
    REPORTS("Reports"),
    SETTINGS("Settings"),
    ;

    val icon: ImageVector
        get() = when (this) {
            TODAY -> Icons.Filled.Home
            BOARD -> ViewIcons.board
            TASKS -> ViewIcons.tasks
            DONE -> Icons.Filled.CheckCircle
            PLAN -> Icons.Filled.DateRange
            REPORTS -> ViewIcons.reports
            SETTINGS -> Icons.Filled.Settings
        }

    companion object {
        /** On a phone's bar (the rest are under More). */
        val onBar = listOf(TODAY, BOARD, TASKS, DONE)

        /** Under More on a phone. */
        val more = listOf(PLAN, REPORTS, SETTINGS)
    }
}

/** Where WindowSizeClass says a window is wide enough for a rail and the task's details beside the view (600 dp: medium). */
val WIDE_WINDOW: Dp = 600.dp

/**
 * The app: on a phone, a bar along the bottom (Today, Board, Tasks, Done, More › Plan, Reports, Settings) and the
 * task's details over the view; on a tablet, a foldable opened up or a phone sideways, a rail of every view and the
 * task's details beside it (like the iPad's sidebar). Over everything: the break, Send feedback, and Undo.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AppShell(vm: BoardViewModel) {
    var viewName by rememberSaveable { mutableStateOf(AppView.TODAY.name) }
    val view = AppView.valueOf(viewName)
    val go: (AppView) -> Unit = {
        // What was said about the last view's change doesn't follow you to the next one.
        if (it.name != viewName) vm.status = null
        viewName = it.name
    }
    // Send feedback is open (from where: "button" or "shake").
    var feedback by rememberSaveable { mutableStateOf<String?>(null) }
    var more by rememberSaveable { mutableStateOf(false) }
    var adding by rememberSaveable { mutableStateOf(false) }
    var todayBusy by remember { mutableStateOf(false) }
    val snackbar = remember { SnackbarHostState() }
    // A snooze, a reminder or a focus session is when notifications start to matter (they tell you with the app closed).
    val context = androidx.compose.ui.platform.LocalContext.current
    val askNotifications = androidx.activity.compose.rememberLauncherForActivityResult(androidx.activity.result.contract.ActivityResultContracts.RequestPermission()) {}
    var askExact by rememberSaveable { mutableStateOf(false) }
    val askToNotify: () -> Unit = {
        if (android.os.Build.VERSION.SDK_INT >= 33 &&
            androidx.core.content.ContextCompat.checkSelfPermission(context, android.Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED
        ) {
            askNotifications.launch(android.Manifest.permission.POST_NOTIFICATIONS)
        }
        // On time to the minute needs the person's OK on recent Android: offered once, with why.
        val prefs = context.getSharedPreferences("focus-alarm", android.content.Context.MODE_PRIVATE)
        if (!FocusAlarm.exactAllowed(context) && !prefs.getBoolean("asked-exact", false)) {
            prefs.edit().putBoolean("asked-exact", true).apply()
            askExact = true
        }
    }
    val undoable = vm.undoable
    LaunchedEffect(undoable) {
        if (undoable != null) {
            val result = snackbar.showSnackbar(undoable.message, actionLabel = "Undo", duration = SnackbarDuration.Short)
            if (result == SnackbarResult.ActionPerformed) undoable.undo()
            vm.undoable = null
        }
    }
    // Another view: Back goes to Today first (an open task, a sheet or Settings answer Back before this).
    BackHandler(enabled = view != AppView.TODAY) { go(AppView.TODAY) }
    val open: (UUID) -> Unit = { vm.openTaskId = it }

    CompositionLocalProvider(LocalAskToNotify provides askToNotify) {
        BoxWithConstraints(Modifier.fillMaxSize()) {
            val windowWidth = maxWidth
            val wide = windowWidth >= WIDE_WINDOW
            val content: @Composable () -> Unit = {
                Box(Modifier.fillMaxSize()) {
                    when (view) {
                        AppView.TODAY -> TodayScreen(vm, onSettings = { go(AppView.SETTINGS) }, onFeedback = { feedback = "button" }, onBusy = { todayBusy = it })
                        AppView.BOARD -> BoardScreen(vm, open, onAdd = { adding = true })
                        AppView.TASKS -> TasksScreen(vm, open)
                        AppView.DONE -> DoneScreen(vm, open)
                        AppView.PLAN -> PlanScreen(vm, wide, open)
                        AppView.REPORTS -> ReportsScreen(vm, wide, open)
                        AppView.SETTINGS -> SettingsScreen(vm, onClose = { go(AppView.TODAY) }, onFeedback = { feedback = "button" })
                    }
                    SnackbarHost(snackbar, Modifier.align(Alignment.BottomCenter).padding(bottom = if (view == AppView.TODAY) 88.dp else 16.dp))
                }
            }
            if (wide) {
                Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
                    Row(Modifier.fillMaxSize().safeDrawingPadding()) {
                        NavigationRail(windowInsets = WindowInsets(0)) {
                            AppView.entries.filter { it != AppView.SETTINGS }.forEach { item ->
                                NavigationRailItem(selected = view == item, onClick = { go(item) }, icon = { Icon(item.icon, contentDescription = null) }, label = { Text(item.title) })
                            }
                            Spacer(Modifier.weight(1f))
                            NavigationRailItem(
                                selected = view == AppView.SETTINGS, onClick = { go(AppView.SETTINGS) },
                                icon = { Icon(AppView.SETTINGS.icon, contentDescription = null) }, label = { Text(AppView.SETTINGS.title) },
                            )
                        }
                        VerticalDivider()
                        Box(Modifier.weight(1f).fillMaxHeight()) { content() }
                        // The task's details beside the view: as wide as the window allows.
                        vm.openTaskId?.let { id ->
                            VerticalDivider()
                            Box(Modifier.width((windowWidth * 0.4f).coerceIn(320.dp, 520.dp)).fillMaxHeight()) {
                                TaskScreen(id, vm, onClose = { vm.openTaskId = null })
                            }
                        }
                    }
                }
            } else {
                Scaffold(
                    bottomBar = {
                        NavigationBar {
                            AppView.onBar.forEach { item ->
                                NavigationBarItem(selected = view == item, onClick = { go(item) }, icon = { Icon(item.icon, contentDescription = null) }, label = { Text(item.title) })
                            }
                            NavigationBarItem(
                                selected = view in AppView.more, onClick = { more = true },
                                icon = { Icon(Icons.Filled.MoreVert, contentDescription = null) },
                                label = { Text(if (view in AppView.more) view.title else "More") },
                            )
                        }
                    },
                    containerColor = MaterialTheme.colorScheme.background,
                ) { padding ->
                    Box(Modifier.padding(padding).consumeWindowInsets(padding)) { content() }
                }
                // On a phone the task's details cover the view (Close or Back goes back to it).
                vm.openTaskId?.let { id -> TaskScreen(id, vm, onClose = { vm.openTaskId = null }) }
            }
        }
        if (more) {
            ModalBottomSheet(onDismissRequest = { more = false }) {
                Column(Modifier.navigationBarsPadding().padding(bottom = 16.dp)) {
                    AppView.more.forEach { item ->
                        ListItem(
                            headlineContent = { Text(item.title) },
                            leadingContent = { Icon(item.icon, contentDescription = null) },
                            colors = ListItemDefaults.colors(containerColor = androidx.compose.ui.graphics.Color.Transparent),
                            modifier = Modifier.clickableRow { more = false; go(item) },
                        )
                    }
                    ListItem(
                        headlineContent = { Text("Send feedback…") },
                        leadingContent = { Icon(Icons.Filled.Email, contentDescription = null) },
                        colors = ListItemDefaults.colors(containerColor = androidx.compose.ui.graphics.Color.Transparent),
                        modifier = Modifier.clickableRow { more = false; feedback = "button" },
                    )
                }
            }
        }
        if (adding) QuickAddDialog(vm) { adding = false }
        if (askExact) {
            AlertDialog(
                onDismissRequest = { askExact = false },
                title = { Text("Ring on time?") },
                text = { Text("So reminders, and the end of a focus session or a break, are told right when they happen (not a few minutes later), allow One Thing to set alarms.") },
                confirmButton = {
                    TextButton(onClick = {
                        askExact = false
                        if (android.os.Build.VERSION.SDK_INT >= 31) {
                            context.startActivity(android.content.Intent(android.provider.Settings.ACTION_REQUEST_SCHEDULE_EXACT_ALARM, android.net.Uri.parse("package:${context.packageName}")))
                        }
                    }) { Text("Allow") }
                },
                dismissButton = { TextButton(onClick = { askExact = false }) { Text("Not now") } },
            )
        }
        BreakScreen(vm)
        feedback?.let { source -> FeedbackSheet(source, onClose = { feedback = null }) }
        // A shake opens Send feedback, only with nothing over the view: no other sheet or dialog, no task, never on
        // the full-screen break.
        val quiet = feedback == null && !more && !adding && !todayBusy && !askExact && vm.openTaskId == null && vm.breakPrompt == null
        ShakeToFeedback(enabled = vm.shakeToFeedback && quiet) { feedback = "shake" }
    }
}

private fun Modifier.clickableRow(onClick: () -> Unit): Modifier = clickable(onClick = onClick)
