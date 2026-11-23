package dev.onething.app

import dev.onething.core.NewTask
import dev.onething.core.Outline
import dev.onething.core.OutlineLevel
import dev.onething.core.OutlineShow
import dev.onething.core.TaskBoard
import java.time.Instant
import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** What the Tasks tree remembers on this phone, and what zoom, Show, Open to and Show in tree change in it. */
class OutlineMemoryTest {
    private val now: Instant = Instant.parse("2026-10-28T09:00:00Z")

    private class Store(val values: MutableMap<String, Any> = mutableMapOf()) : Remembered {
        override fun text(key: String): String? = values[key] as? String
        override fun flag(key: String): Boolean? = values[key] as? Boolean
        override fun put(key: String, value: String?) {
            if (value == null) values.remove(key) else values[key] = value
        }
    }

    private val board = TaskBoard()
    private val work = board.groups[0].id
    private val personal = board.groups[1].id
    private fun add(title: String, parent: UUID? = null, group: UUID? = null) = board.addTask(NewTask(title, parentId = parent, groupId = group), now = now).id

    private val launch = add("Launch", group = work)
    private val copy = add("Launch: copy", parent = launch)
    private val hero = add("Hero", parent = copy)
    private val faq = add("FAQ", parent = copy)
    private val trip = add("Trip", group = personal)
    private val flights = add("Flights", parent = trip)

    @Test
    fun `someone who had Show finished on starts at Everything, and the old setting is left in place`() {
        val old = Store(mutableMapOf(OutlineMemory.OLD_SHOW_DONE to true))
        assertEquals(OutlineShow.ALL, OutlineMemory.read(old).show)
        assertTrue(old.values.containsKey(OutlineMemory.OLD_SHOW_DONE))
        assertEquals(OutlineShow.TODO, OutlineMemory.read(Store(mutableMapOf(OutlineMemory.OLD_SHOW_DONE to false))).show)
        assertEquals(OutlineShow.TODO, OutlineMemory.read(Store()).show)
        assertEquals("a choice made since wins", OutlineShow.WAITING, OutlineMemory.read(Store(mutableMapOf(OutlineMemory.OLD_SHOW_DONE to true, OutlineMemory.SHOW to "waiting"))).show)
        assertEquals(OutlineShow.TODO, OutlineMemory.read(Store(mutableMapOf(OutlineMemory.SHOW to "nonsense"))).show)
    }

    @Test
    fun `open tasks, closed groups, the Show choice and the zoom are kept under the design's keys`() {
        val store = Store()
        val memory = OutlineMemory().withOpen(launch, true).withGroupOpen(personal, false).choosing(OutlineShow.NOW).zoomedTo(copy)
        memory.write(store)
        assertEquals(launch.toString(), store.text("outline.expanded"))
        assertEquals(personal.toString(), store.text("outline.closedGroups"))
        assertEquals("now", store.text("outline.show"))
        assertEquals(copy.toString(), store.text("outline.zoom"))
        assertEquals(memory, OutlineMemory.read(store))

        val out = memory.zoomedTo(null)
        out.write(store, memory)
        assertNull(store.text("outline.zoom"))
        assertNull(OutlineMemory.read(store).zoomId)
    }

    @Test
    fun `zooming in and leaving the zoom opens the way back to it`() {
        val memory = OutlineMemory().withGroupOpen(work, false).zoomedTo(hero)
        assertEquals("a task without subtasks: its parent", copy, Outline.resolveZoom(board, memory.options(null, ""), now))
        val zoomed = memory.zoomedTo(copy)
        assertEquals(listOf("Hero", "FAQ"), Outline.rows(board, zoomed.options(null, ""), now).map { it.title })
        val out = zoomed.leavingZoom(board)
        assertNull(out.zoomId)
        assertTrue("its parents open", launch in out.expanded)
        assertFalse("its group open", work in out.closedGroups)
        val rows = Outline.rows(board, out.options(null, ""), now).map { it.title }
        assertTrue(rows.containsAll(listOf("Work", "Launch", "copy")))
    }

    @Test
    fun `a Show choice narrows the tree, and picking one drops the one for the moment`() {
        board.complete(flights, now = now)
        val memory = OutlineMemory().withOpen(trip, true)
        assertEquals(listOf("Work", "Launch", "Personal", "Trip"), Outline.rows(board, memory.options(null, ""), now).map { it.title })
        val all = memory.choosing(OutlineShow.ALL)
        assertEquals(listOf("Work", "Launch", "Personal", "Trip", "Flights"), Outline.rows(board, all.options(null, ""), now).map { it.title })
        val forNow = memory.copy(showForNow = OutlineShow.ALL)
        assertEquals(OutlineShow.ALL, forNow.shown)
        val store = Store()
        forNow.write(store)
        assertEquals("the choice for the moment isn't kept", "todo", store.text("outline.show"))
        assertNull(forNow.choosing(OutlineShow.WAITING).showForNow)
    }

    @Test
    fun `Open to sets how deep the tree is open, only for what's in view`() {
        val memory = OutlineMemory().withOpen(trip, true)
        val groups = memory.openedTo(board, null, "", OutlineLevel.GROUPS, now)
        assertEquals(setOf(work, personal), groups.closedGroups)
        val everything = memory.openedTo(board, null, "", OutlineLevel.ALL, now)
        assertEquals(setOf(launch, copy, trip), everything.expanded)
        assertTrue(everything.closedGroups.isEmpty())
        val workOnly = memory.openedTo(board, work, "", OutlineLevel.SUBTASKS, now)
        assertEquals("another group's open tasks stay", setOf(trip, launch), workOnly.expanded)
    }

    @Test
    fun `Show in tree opens the way to the task, leaves a zoom that doesn't hold it, and picks its group`() {
        val zoomedElsewhere = OutlineMemory().zoomedTo(trip).withGroupOpen(work, false).choosing(OutlineShow.NOW)
        val reveal = zoomedElsewhere.showingInTree(board, faq, personal, "")!!
        assertNull("a zoom that doesn't hold it is left", reveal.memory.zoomId)
        assertTrue(reveal.memory.expanded.containsAll(listOf(launch, copy)))
        assertFalse(work in reveal.memory.closedGroups)
        assertEquals("its group, when another is picked", work, reveal.groupId)
        assertEquals("Can do now is set aside for To do, for now", OutlineShow.TODO, reveal.memory.shown)
        assertEquals("the choice kept stays", OutlineShow.NOW, reveal.memory.show)
        assertFalse(reveal.clearSearch)
        val rows = Outline.rows(board, reveal.memory.options(reveal.groupId, ""), now)
        assertTrue(rows.any { it.id == faq })

        val inside = OutlineMemory().zoomedTo(launch).showingInTree(board, faq, null, "")!!
        assertEquals("a zoom that holds it stays", launch, inside.memory.zoomId)
        assertNull("all groups stays all groups", inside.groupId)

        board.complete(hero, now = now)
        assertEquals("a finished task: Everything, for now", OutlineShow.ALL, OutlineMemory().showingInTree(board, hero, null, "")!!.memory.shown)
        assertTrue("a search that leaves it out goes", OutlineMemory().showingInTree(board, hero, null, "trip")!!.clearSearch)
        assertFalse(OutlineMemory().showingInTree(board, hero, null, "hero")!!.clearSearch)
        assertNull("a task that's gone", OutlineMemory().showingInTree(board, UUID.randomUUID(), null, ""))
    }
}
