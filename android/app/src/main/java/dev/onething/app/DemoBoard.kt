package dev.onething.app

import dev.onething.core.Actor
import dev.onething.core.ActorKind
import dev.onething.core.LabelDefinition
import dev.onething.core.NewTask
import dev.onething.core.Priority
import dev.onething.core.Stage
import dev.onething.core.TaskBoard
import dev.onething.core.plan
import dev.onething.core.setStage
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.time.temporal.ChronoUnit
import java.util.UUID

/**
 * A made-up board for screenshots and trying the app (the iPhone's DemoBoard, the same story as the README's other
 * screenshots). Only a debug build opens it, started with `--ez demo true`, in a folder of its own that's cleared on
 * every start: the person's own tasks are never read or written.
 */
object DemoBoard {
    /** The intent extra that opens it (debug builds only). */
    const val EXTRA = "demo"

    fun board(now: Instant, zone: ZoneId = ZoneId.systemDefault()): TaskBoard {
        val board = TaskBoard()
        board.zone = zone
        val work = board.groups[0].id
        val home = board.groups[1].id
        val claude = Actor(ActorKind.AGENT, "claude")
        val earlier = now.minus(Duration.ofDays(3))
        fun add(title: String, group: UUID = work, stage: Stage = Stage.NEXT, priority: Priority = Priority.NORMAL, parent: UUID? = null, due: Duration? = null, tags: List<String> = emptyList()): UUID =
            board.addTask(NewTask(title, parentId = parent, groupId = if (parent == null) group else null, priority = priority, deadline = due?.let { now.plus(it) }, stage = if (parent == null) stage else null, tags = tags), now = earlier).id

        val release = add("Ship v2.4 release", priority = Priority.HIGH, tags = listOf("release"))
        val steps = board.addSteps(release, listOf("Ring 0: internal", "Ring 1: 5% of users", "Ring 2: everyone"), Duration.ofDays(1), now = earlier)
        board.addNote(steps[0].id, "Ring 0 healthy: error rate 0.02%, p95 latency flat", claude, now.minus(Duration.ofHours(5)))
        board.complete(steps[0].id, now = now.minus(Duration.ofHours(4)))
        val review = add("Reply to the security review", priority = Priority.CRITICAL, due = Duration.ofHours(3), tags = listOf("security"))
        board.addReminder(review, now, "Review closes at 5 pm", now = earlier)
        board.addNote(review, "Two findings left: token lifetime and the CSP header", now = now.minus(Duration.ofHours(2)))
        add("Fix flaky checkout test", priority = Priority.LOW, due = Duration.ofHours(-1))
        add("Review Maya's API design doc")
        add("Prep 1:1 agenda")
        add("Draft the Q3 roadmap", stage = Stage.INBOX, priority = Priority.HIGH, due = Duration.ofDays(5), tags = listOf("planning"))
        add("Try the new CI cache", stage = Stage.INBOX, priority = Priority.LOW)
        val onboarding = add("Onboarding guide for new hires", stage = Stage.DOING, tags = listOf("docs", "team"))
        val outline = add("Outline", parent = onboarding)
        add("First draft", parent = onboarding)
        add("Review with Sam", parent = onboarding)
        board.complete(outline, now = now.minus(Duration.ofHours(2)))
        val groceries = add("Groceries for the weekend", group = home)
        val dentist = add("Book the dentist", group = home, stage = Stage.INBOX, due = Duration.ofDays(2), tags = listOf("health"))
        add("Plan the summer trip", group = home, priority = Priority.HIGH)
        // Coming up: back tomorrow morning, and after another task.
        val tomorrow = now.atZone(zone).toLocalDate().plusDays(1).atTime(9, 0).atZone(zone).toInstant()
        board.scheduleNextAction(add("Call the bank about the card", group = home), tomorrow, now = earlier)
        board.scheduleNextAction(add("Send the release notes"), now.plus(Duration.ofHours(4)), now = earlier)
        for ((title, daysAgo) in listOf("Renew the passport" to 0L, "Send the invoice" to 1L, "Fix the login redirect" to 1L, "Pay the electricity bill" to 3L)) {
            val id = add(title, group = if ("bill" in title || "passport" in title) home else work)
            board.complete(id, now = now.minus(Duration.ofDays(daysAgo)).minus(Duration.ofHours(1)))
        }
        val old = add("Clean up old branches")
        board.complete(old, now = now.minus(Duration.ofDays(20)))
        board.archive(old, now = now.minus(Duration.ofDays(19)))
        board.complete(groceries, now = now.minus(Duration.ofMinutes(10)))
        board.reopen(groceries, now = now.minus(Duration.ofMinutes(5)))
        // A plan for today: the review first, then the design doc.
        val hour = now.truncatedTo(ChronoUnit.HOURS)
        board.plan(review, hour, 60, now = earlier)
        board.items.firstOrNull { it.title.startsWith("Review Maya") }?.let { board.plan(it.id, hour.plus(Duration.ofHours(2)), 45, now = earlier) }
        board.setStage(release, Stage.DOING, now = earlier)
        // Labels.
        board.labels += listOf(LabelDefinition("urgent", "#ef4444"), LabelDefinition("writing", "#8b5cf6"), LabelDefinition("errand", "#10b981"))
        for ((id, label) in listOf(review to "urgent", onboarding to "writing", groceries to "errand", dentist to "errand")) board.find(id)?.labels?.add(label)
        board.items.firstOrNull { it.title.startsWith("Draft the Q3") }?.labels?.add("writing")
        board.setEnergy(review, 3, now = earlier)
        board.setEnergy(release, 2, now = earlier)
        // Timing the release since 40 minutes ago.
        board.startTimer(release, now = now.minus(Duration.ofMinutes(40)))
        return board
    }
}
