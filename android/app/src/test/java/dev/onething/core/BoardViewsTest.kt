package dev.onething.core

import java.time.Duration
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

/** What the Board, Tasks and Done views show and do (the iPhone's ViewsModelTests.swift, the same cases). */
class BoardViewsTest {
    private val now: Instant = Instant.parse("2026-10-28T09:00:00Z")
    private val board = TaskBoard()
    private var group: UUID? = null
    private val scope get() = BoardScope(board, group)

    private fun titles(items: List<WorkItem>?) = items.orEmpty().map { it.title }

    private fun add(title: String, parentId: UUID? = null, after: UUID? = null, stage: Stage? = null): UUID =
        board.addInPlace(title, parentId, after, stage, group, UUID.randomUUID(), now = now).id

    private fun run(planned: PlannedMove?): String? {
        planned?.let { board.applyPlanned(it, now = now) }
        return planned?.message
    }

    @Test
    fun `cards move between columns`() {
        val plan = add("Write the plan")
        val venue = add("Book the venue", stage = Stage.NEXT)
        assertEquals(listOf("Write the plan"), titles(scope.columns(now)[BoardColumn.INBOX]))
        assertEquals(listOf("Book the venue"), titles(scope.columns(now)[BoardColumn.NEXT]))

        assertEquals("Moved to Next", run(scope.moveCard(plan, BoardColumn.NEXT, venue, now)))
        assertEquals(listOf("Write the plan", "Book the venue"), titles(scope.columns(now)[BoardColumn.NEXT]))

        assertEquals("Done 🎉", run(scope.moveCard(venue, BoardColumn.DONE, now = now)))
        assertTrue(board.get(venue).isDone)
        assertEquals(listOf("Book the venue"), titles(scope.columns(now)[BoardColumn.DONE]))
        assertEquals(listOf("Book the venue"), titles(scope.finishedRoots))

        run(scope.moveCard(venue, BoardColumns.Direction.LEFT, now))
        assertFalse("out of Done: open again", board.get(venue).isDone)
        assertEquals(Stage.DOING, board.get(venue).stage)
        run(scope.moveCard(plan, BoardColumns.Direction.DOWN, now))
        assertEquals(listOf("Write the plan"), titles(scope.columns(now)[BoardColumn.NEXT]))
    }

    @Test
    fun `Doing says when it holds more than three`() {
        for (title in listOf("One", "Two", "Three")) add(title, stage = Stage.DOING)
        val four = add("Four", stage = Stage.NEXT)
        assertEquals(BoardColumns.OVER_LIMIT_MESSAGE, run(scope.moveCard(four, BoardColumn.DOING, now = now)))
        assertEquals(4, scope.columns(now)[BoardColumn.DOING]?.size)
        assertEquals(BoardColumns.Fill.OVER, BoardColumns.fill(4))
        assertEquals(BoardColumns.Fill.FULL, BoardColumns.fill(3))
    }

    @Test
    fun `the outline adds indents outdents and reorders`() {
        val ids = mutableListOf<UUID>()
        for (title in listOf("Trip", "Pack", "Tickets")) ids.add(add(title, after = ids.lastOrNull(), stage = Stage.NEXT))
        assertEquals("each right after the one before", listOf("Trip", "Pack", "Tickets"), titles(board.items))

        run(scope.indent(ids[1]))
        run(scope.indent(ids[2]))
        assertEquals(listOf("Pack", "Tickets"), titles(board.get(ids[0]).children))
        assertNull(scope.indent(ids[0]))
        add("Passport", parentId = ids[0])
        assertEquals("a new subtask goes first", listOf("Passport", "Pack", "Tickets"), titles(board.get(ids[0]).children))

        run(scope.outdent(ids[2]))
        assertEquals(listOf("Trip", "Tickets"), titles(board.items))
        assertNull(scope.outdent(ids[2]))
        run(scope.step(ids[2], -1))
        assertEquals(listOf("Tickets", "Trip"), titles(board.items))

        assertNull("not into its own subtask", scope.drop(ids[0], ids[1], OutlineMoves.Zone.INSIDE))
        run(scope.drop(ids[1], ids[2], OutlineMoves.Zone.INSIDE))
        assertEquals(listOf("Pack"), titles(board.get(ids[2]).children))
    }

    /** Indent and up/down in Tasks only see the tasks on screen: not another group's, not archived ones. */
    @Test
    fun `outline moves only use the tasks on screen`() {
        val personal = board.groups[1].id
        val old = add("Work task")
        board.complete(old, now = now)
        board.archive(old, now = now)
        group = personal
        val plants = add("Water plants")
        val mum = add("Call mum")
        assertEquals("no archived task, no other group's", listOf("Water plants", "Call mum"), titles(scope.visibleRoots))

        assertNull("the first task on screen has nothing above it", scope.indent(plants))
        assertNull(scope.step(plants, -1))

        group = null
        assertEquals("archived tasks stay out of Tasks", listOf("Water plants", "Call mum"), titles(scope.visibleRoots))
        assertNull("an archived task above doesn't count", scope.indent(plants))
        run(scope.indent(mum))
        assertEquals(plants, board.get(mum).parent?.id)
    }

    @Test
    fun `a subtask is not a card`() {
        val trip = add("Trip")
        val pack = add("Pack", parentId = trip)
        val e = assertThrows(BoardException::class.java) { scope.moveCard(pack, BoardColumn.DONE, now = now) }
        assertEquals("Only top-level tasks are cards; move its task instead.", e.message)
        assertFalse(board.get(pack).isDone)
    }

    @Test
    fun `a task moved to another group goes there`() {
        val personal = board.groups[1]
        val trip = add("Trip")
        val pack = add("Pack", parentId = trip)
        assertEquals("Moved to ${personal.name}", run(scope.toGroup(pack, personal.id)))
        assertNull(board.get(pack).parent)
        assertEquals(personal.id, board.get(pack).groupId)
        group = personal.id
        assertEquals(listOf("Pack"), titles(scope.visibleRoots))
    }

    @Test
    fun `done and archived tasks come back`() {
        val old = add("Old")
        board.complete(old, now = now)
        board.archive(old, now = now)
        assertEquals(listOf("Old"), titles(scope.archivedRoots))
        assertTrue(scope.finishedRoots.isEmpty())
        board.unarchive(old, now = now)
        assertEquals(listOf("Old"), titles(scope.finishedRoots))
        board.reopen(old, now = now)
        assertTrue(scope.finishedRoots.isEmpty())
    }

    @Test
    fun `the search narrows the board, tasks and done`() {
        val trip = add("Trip")
        add("Passport", parentId = trip)
        add("Groceries")
        assertEquals("a task whose subtask matches", listOf("Trip"), titles(BoardScope(board, null, "passport").matchingRoots))
        assertEquals(2, BoardScope(board, null, "").matchingRoots.size)
    }

    @Test
    fun `the outline hides finished tasks and closed subtasks`() {
        val trip = add("Trip")
        val pack = add("Pack", parentId = trip)
        add("Tickets", parentId = trip)
        board.complete(pack, now = now)
        assertEquals(listOf("Trip"), OutlineRow.flatten(board.items, emptySet(), showDone = false).map { it.item.title })
        val open = OutlineRow.flatten(board.items, setOf(trip), showDone = false)
        assertEquals(listOf("Trip" to 0, "Tickets" to 1), open.map { it.item.title to it.depth })
        assertEquals("a new subtask goes first", listOf("Trip", "Tickets", "Pack"), OutlineRow.flatten(board.items, setOf(trip), showDone = true).map { it.item.title })
    }

    @Test
    fun `a card says what matters about its task`() {
        val trip = add("Trip")
        add("Pack", parentId = trip)
        board.update(trip, TaskChanges(deadline = now.minus(Duration.ofHours(2))), now = now)
        val chips = CardFacts.chips(board.get(trip), now)
        assertEquals(listOf("Overdue · 2h ago", "0/1"), chips.map { it.text })
        assertEquals(ChipTone.DANGER, chips[0].tone)
        assertEquals("in 1h 30m", RelativeTime.format(now.plus(Duration.ofMinutes(90)), now))
        assertEquals("Nov 20", RelativeTime.format(Instant.parse("2026-11-20T12:00:00Z"), now))
        assertEquals("2 h 05 min", durationWords(Duration.ofMinutes(125)))
    }

    @Test
    fun `a task's timeline has its subtasks too, newest first, with who did it`() {
        val trip = add("Trip")
        val pack = add("Pack", parentId = trip)
        board.complete(pack, Actor(ActorKind.AGENT, "copilot"), now.plusSeconds(60))
        add("Other")
        val lines = board.timeline(trip)
        assertEquals(listOf("Completed \"Pack\"", "Added \"Pack\" to \"Trip\"", "Created \"Trip\""), lines.map { it.summary })
        assertEquals("Agent: copilot", lines[0].actor.displayName)
        assertEquals(4, board.timeline().size)
    }

    @Test
    fun `done is grouped by day`() {
        val zone = ZoneId.of("UTC")
        val a = add("A")
        val b = add("B")
        val c = add("C")
        board.complete(a, now = now)
        board.complete(b, now = now.minus(Duration.ofDays(1)))
        board.complete(c, now = now.minus(Duration.ofDays(3)))
        val days = DoneDays.byDay(BoardScope(board, null).finishedRoots, now, zone) { d: LocalDate -> d.toString() }
        assertEquals(listOf("Today", "Yesterday", "2026-10-25"), days.map { it.first })
        assertEquals(1, days[0].second.size)
    }
}
