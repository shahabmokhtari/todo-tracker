package dev.todotracker.core

import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

/** What a task's details change (same rules as TodoTracker.Core: Update, reminders, archive, groups). */
class TaskDetailsTest {
    private val t0: Instant = Instant.parse("2026-01-05T09:00:00Z")

    @Test
    fun `updating a task changes only the fields given and says who did it`() {
        val board = TaskBoard()
        val item = board.addTask(NewTask("Draft", details = "old", deadline = t0.plusSeconds(3600)), now = t0)

        board.update(item.id, TaskChanges(title = " Write the draft ", priority = Priority.HIGH), now = t0)
        assertEquals("Write the draft", item.title)
        assertEquals(Priority.HIGH, item.priority)
        assertEquals("old", item.details)
        assertEquals(t0.plusSeconds(3600), item.deadline)

        board.update(item.id, TaskChanges(details = "  ", clearDeadline = true, energy = 3, repeat = "every weekday"), actor = Actor(ActorKind.AGENT, "copilot"), now = t0)
        assertNull(item.details)
        assertNull(item.deadline)
        assertEquals(3, item.energy)
        assertEquals("every weekday", item.repeat.toString())
        assertEquals(Actor(ActorKind.AGENT, "copilot"), board.activity.last().actor)

        board.update(item.id, TaskChanges(clearRepeat = true, clearEnergy = true, deadline = t0), now = t0)
        assertNull(item.repeat)
        assertNull(item.energy)
        assertEquals(t0, item.deadline)
    }

    @Test
    fun `a bad title, repeat rule or energy is refused and changes nothing`() {
        val board = TaskBoard()
        val item = board.addTask(NewTask("Keep me"), now = t0)

        assertThrows(BoardException::class.java) { board.update(item.id, TaskChanges(title = "  ", priority = Priority.LOW), now = t0) }
        val bad = assertThrows(BoardException::class.java) { board.update(item.id, TaskChanges(title = "Changed", repeat = "every blue moon"), now = t0) }
        assertTrue(bad.message!!.contains("every day"))
        assertThrows(BoardException::class.java) { board.update(item.id, TaskChanges(title = "Changed", energy = 4), now = t0) }
        assertThrows(BoardException::class.java) { board.update(item.id, TaskChanges(stepDelay = Duration.ofSeconds(5)), now = t0) }

        assertEquals("Keep me", item.title)
        assertEquals(Priority.NORMAL, item.priority)
    }

    @Test
    fun `steps in order and the time between them are set in the details`() {
        val board = TaskBoard()
        val item = board.addTask(NewTask("Rollout"), now = t0)

        board.update(item.id, TaskChanges(sequential = true, stepDelay = Duration.ofHours(2)), now = t0)
        assertTrue(item.sequential)
        assertEquals(Duration.ofHours(2), item.stepDelay)

        board.update(item.id, TaskChanges(sequential = false, clearStepDelay = true), now = t0)
        assertFalse(item.sequential)
        assertNull(item.stepDelay)
    }

    @Test
    fun `a reminder set by hand fires once and its notification is marked delivered`() {
        val board = TaskBoard()
        val item = board.addTask(NewTask("Call the bank"), now = t0)
        val reminder = board.addReminder(item.id, t0.plusSeconds(600), null, now = t0)
        assertEquals("Time to act on: Call the bank", reminder.message)
        assertEquals(ReminderKind.MANUAL, reminder.kind)

        assertEquals(t0.plusSeconds(600), Reminders.nextAt(board, t0))
        assertTrue(Reminders.due(board, t0.plusSeconds(599)).isEmpty())

        val due = Reminders.due(board, t0.plusSeconds(600))
        assertEquals(listOf(reminder.id), due.map { it.reminder.id })
        assertEquals("Call the bank", due[0].item.title)

        board.markNotified(item.id, reminder.id, t0.plusSeconds(601))
        assertTrue(Reminders.due(board, t0.plusSeconds(700)).isEmpty())
        assertNull(Reminders.nextAt(board, t0.plusSeconds(700)))
        assertEquals("reminderFired", board.activity.last().kind)
    }

    @Test
    fun `a snooze ending is a reminder to deliver, and not for a task that's done or a snooze that was replaced`() {
        val board = TaskBoard()
        val a = board.addTask(NewTask("A"), now = t0)
        val b = board.addTask(NewTask("B"), now = t0)
        val c = board.addTask(NewTask("C"), now = t0)
        board.scheduleNextAction(a.id, t0.plusSeconds(3600), notify = true, now = t0)
        board.scheduleNextAction(b.id, t0.plusSeconds(1800), notify = true, now = t0)
        board.scheduleNextAction(c.id, t0.plusSeconds(900), notify = true, now = t0)
        board.complete(c.id, now = t0)
        // Snoozed again: only the newer time counts.
        board.scheduleNextAction(b.id, t0.plusSeconds(7200), notify = true, now = t0)

        assertEquals(t0.plusSeconds(3600), Reminders.nextAt(board, t0))
        assertEquals(listOf("A"), Reminders.due(board, t0.plusSeconds(3600)).map { it.item.title })
        assertEquals(listOf("A", "B"), Reminders.due(board, t0.plusSeconds(7200)).map { it.item.title })
    }

    @Test
    fun `only a finished top-level task is archived, and it leaves every list until it's brought back`() {
        val board = TaskBoard()
        val item = board.addTask(NewTask("Ship it"), now = t0)
        val sub = board.addTask(NewTask("Tag the release", parentId = item.id), now = t0)
        board.addNote(item.id, "Shipped on time", now = t0)

        assertThrows(BoardException::class.java) { board.archive(item.id, now = t0) }
        board.complete(item.id, now = t0)
        assertThrows(BoardException::class.java) { board.archive(sub.id, now = t0) }

        board.archive(item.id, now = t0.plusSeconds(60))
        assertEquals(t0.plusSeconds(60), item.archivedAt)
        assertTrue(item.isArchived && sub.isArchived)
        assertTrue(Agenda.doneToday(board, t0.plusSeconds(120), ZoneId.of("UTC")).isEmpty())
        assertTrue(Agenda.build(board, t0.plusSeconds(120)).recentNotes.isEmpty())
        assertEquals("archived", board.activity.last().kind)
        assertNotNull(BoardCodec.decode(BoardCodec.encode(board)).get(item.id).archivedAt)

        board.unarchive(item.id, now = t0.plusSeconds(180))
        assertNull(item.archivedAt)
        assertEquals(listOf("Ship it"), Agenda.doneToday(board, t0.plusSeconds(240), ZoneId.of("UTC")).map { it.title })
    }

    @Test
    fun `reopening an archived task brings it back from the archive too`() {
        val board = TaskBoard()
        val item = board.addTask(NewTask("Ship it"), now = t0)
        board.complete(item.id, now = t0)
        board.archive(item.id, now = t0)

        board.reopen(item.id, now = t0.plusSeconds(60))

        assertNull(item.archivedAt)
        assertEquals(listOf(item.id), Agenda.build(board, t0.plusSeconds(60)).now.map { it.item.id })
    }

    @Test
    fun `a group is renamed and recolored, and deleting it moves its tasks so none are lost`() {
        val board = TaskBoard()
        val work = board.groups[0]
        val personal = board.groups[1]
        val task = board.addTask(NewTask("Report", groupId = work.id), now = t0)
        val sub = board.addTask(NewTask("Charts", parentId = task.id), now = t0)

        board.updateGroup(work.id, " Job ", "#ff0000", now = t0)
        assertEquals("Job", work.name)
        assertEquals("#ff0000", work.color)
        assertThrows(BoardException::class.java) { board.updateGroup(work.id, "personal", null, now = t0) }
        assertThrows(BoardException::class.java) { board.updateGroup(work.id, "Job", "red", now = t0) }

        assertThrows(BoardException::class.java) { board.deleteGroup(work.id, work.id, now = t0) }
        board.deleteGroup(work.id, personal.id, now = t0)
        assertEquals(listOf(personal.id), board.groups.map { it.id })
        assertEquals(personal.id, task.groupId)
        assertEquals(personal.id, sub.groupId)
        assertThrows(BoardException::class.java) { board.deleteGroup(personal.id, personal.id, now = t0) }
    }

    @Test
    fun `a task moves to another group with its subtasks, a subtask follows its parent`() {
        val board = TaskBoard()
        val (work, personal) = board.groups
        val task = board.addTask(NewTask("Report", groupId = work.id), now = t0)
        val sub = board.addTask(NewTask("Charts", parentId = task.id), now = t0)

        board.moveToGroup(task.id, personal.id, now = t0)
        assertEquals(personal.id, sub.groupId)
        assertEquals("moved", board.activity.last().kind)
        assertThrows(BoardException::class.java) { board.moveToGroup(sub.id, work.id, now = t0) }
    }

    @Test
    fun `the focus timer's lengths are set within limits`() {
        val board = TaskBoard()
        board.setFocusLengths(50, 10, 30)
        assertEquals(PomodoroSettings(50, 10, 30, 4), board.pomodoro.settings)
        assertThrows(BoardException::class.java) { board.setFocusLengths(0, 5, 15) }
        assertThrows(BoardException::class.java) { board.setFocusLengths(25, 5, 999) }
        assertEquals(PomodoroSettings(50, 10, 30, 4), BoardCodec.decode(BoardCodec.encode(board)).pomodoro.settings)
    }

    @Test
    fun `workstreams sum up tasks with subtasks and recent notes are newest first`() {
        val board = TaskBoard()
        val release = board.addTask(NewTask("Release"), now = t0)
        val a = board.addTask(NewTask("Build", parentId = release.id), now = t0)
        board.addTask(NewTask("Test", parentId = release.id), now = t0)
        val c = board.addTask(NewTask("Announce", parentId = release.id), now = t0)
        board.complete(a.id, now = t0)
        board.scheduleNextAction(c.id, t0.plusSeconds(3600), now = t0)
        board.addTask(NewTask("Single"), now = t0)
        for (i in 1..7) board.addNote(release.id, "Note $i", now = t0.plusSeconds(i.toLong()))

        val dash = Agenda.build(board, t0.plusSeconds(60))
        val stream = dash.overview.single { it.item === release }
        assertEquals(1, stream.doneLeaves)
        assertEquals(3, stream.totalLeaves)
        assertEquals(1, stream.actionableCount)
        assertEquals(1, stream.waitingCount)
        assertEquals(t0.plusSeconds(3600), stream.nextWakeAt)
        assertEquals(listOf("Release"), Agenda.workstreams(dash).map { it.item.title })
        assertEquals(listOf("Note 7", "Note 6", "Note 5", "Note 4", "Note 3"), dash.recentNotes.map { it.note.text })
    }

    @Test
    fun `search finds words in the title or details, and done or archived ones only when asked`() {
        val board = TaskBoard()
        val (work, personal) = board.groups
        val bank = board.addTask(NewTask("Call the bank", groupId = personal.id, details = "About the mortgage"), now = t0)
        val report = board.addTask(NewTask("Quarterly report", groupId = work.id), now = t0)
        val old = board.addTask(NewTask("Old bank form", groupId = personal.id), now = t0)
        board.complete(old.id, now = t0)
        board.archive(old.id, now = t0)

        assertEquals(listOf(bank), TaskSearch.parse("mortgage").apply(board))
        assertEquals(listOf(bank), TaskSearch.parse("BANK").apply(board))
        assertEquals(listOf(report), TaskSearch.parse("group:work").apply(board))
        assertEquals(listOf(old), TaskSearch.parse("bank is:archived").apply(board))
        assertTrue(TaskSearch.parse("bank is:done").apply(board).isEmpty())
        assertEquals(listOf(bank), TaskSearch.parse("\"call the\"").apply(board))
        assertTrue(TaskSearch.parse("  ").isEmpty)
    }

    @Test
    fun `undo of the last step of a repeating checklist puts the checklist back where it was`() {
        val now = Instant.parse("2026-01-05T10:00:00Z")
        val board = TaskBoard().apply { zone = ZoneId.of("UTC") }
        val review = board.addTask(NewTask("Weekly review", deadline = Instant.parse("2026-01-05T17:00:00Z")), now = now)
        review.repeat = Recurrence.parse("every week")
        val steps = board.addSteps(review.id, listOf("Inbox zero", "Plan the week"), null, now = now)
        board.complete(steps[0].id, now = now)
        board.complete(steps[1].id, now = now.plusSeconds(60))
        assertTrue(steps.none { it.isDone })

        // The Undo on "Done: Plan the week" reopens the step it was about.
        board.reopen(steps[1].id, now = now.plusSeconds(90))

        assertEquals(Instant.parse("2026-01-05T17:00:00Z"), review.deadline)
        assertNull(review.nextActionAt)
        assertEquals(now, steps[0].completedAt)
        assertFalse(steps[1].isDone)
        assertEquals("every week on Monday", review.repeat.toString())
        assertEquals(listOf(steps[1].id), Agenda.build(board, now.plusSeconds(90)).now.map { it.item.id })
    }

    @Test
    fun `undo of a repeating task puts back its done subtasks and the reminders moving on put away`() {
        val now = Instant.parse("2026-01-05T10:00:00Z")
        val board = TaskBoard().apply { zone = ZoneId.of("UTC") }
        val chores = board.addTask(NewTask("Chores", deadline = Instant.parse("2026-01-05T17:00:00Z")), now = now)
        chores.repeat = Recurrence.parse("every day")
        val dishes = board.addTask(NewTask("Dishes", parentId = chores.id), now = now)
        val laundry = board.addTask(NewTask("Laundry", parentId = chores.id), now = now)
        board.complete(dishes.id, now = now)
        val manual = board.addReminder(chores.id, Instant.parse("2026-01-05T16:00:00Z"), "Before dinner", now = now)

        board.complete(chores.id, now = now.plusSeconds(60))
        assertNull(dishes.completedAt)
        assertNotNull(manual.dismissedAt)

        board.reopen(chores.id, now = now.plusSeconds(120))
        assertEquals(now, dishes.completedAt)
        assertNull(laundry.completedAt)
        assertNull(manual.dismissedAt)
        assertEquals(Instant.parse("2026-01-05T17:00:00Z"), chores.deadline)
    }
}
