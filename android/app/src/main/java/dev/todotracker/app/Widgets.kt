package dev.todotracker.app

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.widget.Toast
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.DpSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.glance.GlanceId
import androidx.glance.GlanceModifier
import androidx.glance.GlanceTheme
import androidx.glance.LocalContext
import androidx.glance.LocalSize
import androidx.glance.action.ActionParameters
import androidx.glance.action.actionParametersOf
import androidx.glance.action.clickable
import androidx.glance.appwidget.GlanceAppWidget
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.SizeMode
import androidx.glance.appwidget.action.ActionCallback
import androidx.glance.appwidget.action.actionRunCallback
import androidx.glance.appwidget.action.actionStartActivity
import androidx.glance.appwidget.cornerRadius
import androidx.glance.appwidget.lazy.LazyColumn
import androidx.glance.appwidget.lazy.items
import androidx.glance.appwidget.provideContent
import androidx.glance.appwidget.updateAll
import androidx.glance.background
import androidx.glance.color.ColorProvider
import androidx.glance.layout.Alignment
import androidx.glance.layout.Box
import androidx.glance.layout.Column
import androidx.glance.layout.Row
import androidx.glance.layout.Spacer
import androidx.glance.layout.fillMaxHeight
import androidx.glance.layout.fillMaxSize
import androidx.glance.layout.fillMaxWidth
import androidx.glance.layout.height
import androidx.glance.layout.padding
import androidx.glance.layout.size
import androidx.glance.layout.width
import androidx.glance.text.FontWeight
import androidx.glance.text.Text
import androidx.glance.text.TextStyle
import androidx.work.ExistingWorkPolicy
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.CoroutineWorker
import androidx.work.WorkerParameters
import dev.todotracker.core.Actor
import dev.todotracker.core.BoardException
import dev.todotracker.core.TaskLinks
import dev.todotracker.core.WidgetItem
import dev.todotracker.core.WidgetSnapshot
import dev.todotracker.core.WidgetView
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import java.util.UUID
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resume
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext

/**
 * The home-screen widgets (Now and Today). They run in the app's process, so they read the board the app uses: this
 * phone's tasks folder ([BoardHub.store]). What they show
 * comes from [WidgetSnapshot] (the agenda's own rules, the same as the iPhone's and the Mac's widgets), and they're
 * updated when the board changes and again when the list next changes on its own (a snooze ends, midnight…).
 */
object Widgets {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private const val NEXT = "widget-next"

    /** What the widgets show now (null: the tasks can't be read yet). */
    fun snapshot(context: Context, now: Instant): WidgetSnapshot? {
        val zone = ZoneId.systemDefault()
        val store = BoardHub.store(context) ?: return null
        return store.read { WidgetSnapshot.build(it, now, zone) }
    }

    /** The board changed: the widgets show it again. */
    fun update(context: Context) {
        val app = context.applicationContext
        scope.launch {
            runCatching {
                NowWidget().updateAll(app)
                TodayWidget().updateAll(app)
            }
        }
    }

    /** Updates the widgets again when their list next changes on its own (null: in a day at the latest). */
    fun scheduleNext(context: Context, at: Instant?, now: Instant) {
        val delay = Duration.between(now, at ?: now.plus(WidgetSnapshot.HORIZON)).coerceAtLeast(Duration.ofSeconds(1))
        val request = OneTimeWorkRequestBuilder<WidgetWorker>().setInitialDelay(delay.toMillis(), TimeUnit.MILLISECONDS).build()
        runCatching { WorkManager.getInstance(context).enqueueUniqueWork(NEXT, ExistingWorkPolicy.REPLACE, request) }
    }

    /** Opens the app on the task (todotracker://task/<id>). */
    fun openTask(context: Context, id: UUID): Intent =
        Intent(Intent.ACTION_VIEW, Uri.parse(TaskLinks.forTask(id)), context, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)

    // One Done at a time: a second tap waits for the first, then finds the task done (or moved on) and does nothing.
    private val doing = Mutex()

    /**
     * Done on a widget: the same change as Mark done on a reminder (the person did it: it's theirs on the timeline),
     * made on this phone's tasks folder (through the running app when it's open). When it can't be made here (the task
     * has open subtasks), the app opens on the task and says why: nothing is changed behind the person's back, and
     * nothing is done twice.
     */
    suspend fun markDone(context: Context, id: UUID) {
        val app = context.applicationContext
        val problem = doing.withLock {
            suspendCancellableCoroutine { done ->
                BoardHub.change(app, { board, now ->
                    val item = board.get(id)
                    if (item.hasOpenChildren) throw BoardException("“${item.title}” has open subtasks: finish them here.")
                    if (WidgetSnapshot.canMarkDone(board, id, now)) board.complete(id, Actor.USER, now)
                }) { result -> done.resume(result.exceptionOrNull()?.let { it.message ?: "Couldn't mark it done." }) }
            }
        }
        if (problem != null) {
            withContext(Dispatchers.Main) {
                Toast.makeText(app, problem, Toast.LENGTH_LONG).show()
                app.startActivity(openTask(app, id))
            }
        }
        update(app)
    }
}

/** Updates the widgets when their list changes on its own. */
class WidgetWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        NowWidget().updateAll(applicationContext)
        TodayWidget().updateAll(applicationContext)
        return Result.success()
    }
}

/** Done tapped on a widget. */
class WidgetDoneAction : ActionCallback {
    override suspend fun onAction(context: Context, glanceId: GlanceId, parameters: ActionParameters) {
        val id = parameters[TASK]?.let { runCatching { UUID.fromString(it) }.getOrNull() } ?: return
        Widgets.markDone(context, id)
    }

    companion object {
        val TASK = ActionParameters.Key<String>("task")
    }
}

/** The words the widgets say (the same as on the iPhone, iPad and Mac). */
internal object WidgetWords {
    const val NOW = "Now"
    const val TODAY = "Today"
    const val NOTHING = "Nothing to do right now. Enjoy it."
    const val OPEN_APP = "Open Onething once to see your tasks here."
    const val DONE = "Done"
    fun more(count: Int) = "$count more today"
}

private val accent = Color(0xFF6366F1)

/** Reads the board, and sets the next update, then draws [content] with what to show now. */
private suspend fun showing(context: Context, draw: suspend (WidgetView, Boolean) -> Unit) {
    val now = Instant.now()
    val snapshot = withContext(Dispatchers.IO) { runCatching { Widgets.snapshot(context, now) }.getOrNull() }
    Widgets.scheduleNext(context, snapshot?.nextChange(now), now)
    draw(snapshot?.viewAt(now) ?: WidgetView.EMPTY, snapshot != null)
}

/** The Now widget: the one thing to do now (and its group), Done, and when it's wide the next few and how many more today. */
class NowWidget : GlanceAppWidget() {
    override val sizeMode = SizeMode.Responsive(setOf(DpSize(110.dp, 110.dp), DpSize(250.dp, 110.dp)))

    override suspend fun provideGlance(context: Context, id: GlanceId) {
        showing(context) { view, hasSnapshot -> provideContent { GlanceTheme { NowContent(view, hasSnapshot) } } }
    }
}

/** The Today widget: the Do now list, each with Done, then what comes back later today. Resizable. */
class TodayWidget : GlanceAppWidget() {
    override val sizeMode = SizeMode.Exact

    override suspend fun provideGlance(context: Context, id: GlanceId) {
        showing(context) { view, hasSnapshot -> provideContent { GlanceTheme { TodayContent(view, hasSnapshot) } } }
    }
}

class NowWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget: GlanceAppWidget = NowWidget()
}

class TodayWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget: GlanceAppWidget = TodayWidget()
}

@Composable
private fun Surface(content: @Composable () -> Unit) {
    Box(
        GlanceModifier.fillMaxSize().background(GlanceTheme.colors.widgetBackground).cornerRadius(20.dp).padding(14.dp),
        contentAlignment = Alignment.TopStart,
    ) { content() }
}

@Composable
private fun Empty(hasSnapshot: Boolean) {
    Text(
        if (hasSnapshot) WidgetWords.NOTHING else WidgetWords.OPEN_APP,
        style = TextStyle(color = GlanceTheme.colors.onSurface, fontSize = 15.sp, fontWeight = FontWeight.Medium),
        modifier = GlanceModifier.clickable(actionStartActivity(Intent(LocalContext.current, MainActivity::class.java))),
    )
}

@Composable
private fun DoneButton(item: WidgetItem) {
    val context = LocalContext.current
    val label = if (item.openSubtasks) "Open" else WidgetWords.DONE
    val action = if (item.openSubtasks) actionStartActivity(Widgets.openTask(context, item.id))
    else actionRunCallback<WidgetDoneAction>(actionParametersOf(WidgetDoneAction.TASK to item.id.toString()))
    Box(
        GlanceModifier.background(accent).cornerRadius(14.dp).padding(horizontal = 12.dp, vertical = 6.dp).clickable(action),
    ) {
        Text(if (item.openSubtasks) label else "✓ $label", style = TextStyle(color = ColorProvider(Color.White, Color.White), fontSize = 13.sp, fontWeight = FontWeight.Bold))
    }
}

@Composable
private fun GroupLine(item: WidgetItem) {
    val group = item.group ?: return
    val dot = item.color?.let { runCatching { Color(android.graphics.Color.parseColor(it)) }.getOrNull() } ?: accent
    Row(verticalAlignment = Alignment.CenterVertically) {
        Box(GlanceModifier.size(7.dp).background(dot).cornerRadius(4.dp)) {}
        Spacer(GlanceModifier.width(4.dp))
        Text(group, maxLines = 1, style = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = 12.sp))
    }
}

@Composable
private fun NowContent(view: WidgetView, hasSnapshot: Boolean) {
    val context = LocalContext.current
    val wide = LocalSize.current.width >= 250.dp
    Surface {
        val focus = view.focus
        if (focus == null) {
            Empty(hasSnapshot)
            return@Surface
        }
        Row(GlanceModifier.fillMaxSize()) {
            Column(GlanceModifier.defaultWeight().fillMaxHeight().clickable(actionStartActivity(Widgets.openTask(context, focus.id)))) {
                Text(WidgetWords.NOW.uppercase(), style = TextStyle(color = ColorProvider(accent, Color(0xFF8B8DF8)), fontSize = 11.sp, fontWeight = FontWeight.Bold))
                Spacer(GlanceModifier.height(4.dp))
                Text(focus.title, maxLines = 3, style = TextStyle(color = GlanceTheme.colors.onSurface, fontSize = 16.sp, fontWeight = FontWeight.Bold))
                Spacer(GlanceModifier.height(4.dp))
                GroupLine(focus)
                Spacer(GlanceModifier.defaultWeight())
                Row(verticalAlignment = Alignment.CenterVertically) {
                    DoneButton(focus)
                    val more = view.moreAfter(1)
                    if (!wide && more > 0) {
                        Spacer(GlanceModifier.width(6.dp))
                        Text("+$more", style = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = 12.sp))
                    }
                }
            }
            if (wide) {
                Spacer(GlanceModifier.width(12.dp))
                Column(GlanceModifier.defaultWeight().fillMaxHeight()) {
                    Text("NEXT", style = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = 11.sp, fontWeight = FontWeight.Bold))
                    view.next.forEach { item ->
                        Spacer(GlanceModifier.height(6.dp))
                        Text(item.title, maxLines = 1, style = TextStyle(color = GlanceTheme.colors.onSurface, fontSize = 13.sp),
                            modifier = GlanceModifier.clickable(actionStartActivity(Widgets.openTask(context, item.id))))
                    }
                    if (view.next.isEmpty()) Text("That's all for now.", style = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = 13.sp))
                    Spacer(GlanceModifier.defaultWeight())
                    if (view.more > 0) Text(WidgetWords.more(view.more), style = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = 12.sp))
                }
            }
        }
    }
}

@Composable
private fun TodayContent(view: WidgetView, hasSnapshot: Boolean) {
    val context = LocalContext.current
    val time = DateTimeFormatter.ofLocalizedTime(FormatStyle.SHORT).withZone(ZoneId.systemDefault())
    Surface {
        Column(GlanceModifier.fillMaxSize()) {
            Row(GlanceModifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Text(WidgetWords.TODAY, style = TextStyle(color = GlanceTheme.colors.onSurface, fontSize = 17.sp, fontWeight = FontWeight.Bold), modifier = GlanceModifier.defaultWeight())
                if (view.nowCount > 0) Text("${view.nowCount} to do", style = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = 12.sp))
            }
            Spacer(GlanceModifier.height(8.dp))
            if (view.focus == null && view.later.isEmpty()) {
                Empty(hasSnapshot)
                return@Column
            }
            // Resizable: the list scrolls when there's more than fits.
            LazyColumn(GlanceModifier.defaultWeight()) {
                items(view.today, itemId = { it.id.mostSignificantBits xor it.id.leastSignificantBits }) { item ->
                    val first = item.id == view.focus?.id
                    Row(
                        GlanceModifier.fillMaxWidth().padding(vertical = 4.dp).let { if (first) it.background(ColorProvider(Color(0x1F6366F1), Color(0x338B8DF8))).cornerRadius(10.dp).padding(6.dp) else it },
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Column(GlanceModifier.defaultWeight().clickable(actionStartActivity(Widgets.openTask(context, item.id)))) {
                            Text(item.title, maxLines = 1, style = TextStyle(color = GlanceTheme.colors.onSurface, fontSize = 14.sp, fontWeight = if (first) FontWeight.Bold else FontWeight.Normal))
                            GroupLine(item)
                        }
                        DoneButton(item)
                    }
                }
                if (view.later.isNotEmpty()) {
                    item { Text("LATER TODAY", style = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = 11.sp, fontWeight = FontWeight.Bold), modifier = GlanceModifier.padding(top = 8.dp, bottom = 2.dp)) }
                    items(view.later, itemId = { it.first.id.mostSignificantBits xor it.first.id.leastSignificantBits xor 1L }) { (item, at) ->
                        Text("${time.format(at)}  ${item.title}", maxLines = 1, style = TextStyle(color = GlanceTheme.colors.onSurface, fontSize = 13.sp),
                            modifier = GlanceModifier.padding(vertical = 2.dp).clickable(actionStartActivity(Widgets.openTask(context, item.id))))
                    }
                }
                val more = view.moreAfter(view.today.size) - view.later.size
                if (more > 0) item { Text(WidgetWords.more(more), style = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = 12.sp), modifier = GlanceModifier.padding(top = 6.dp)) }
            }
        }
    }
}
