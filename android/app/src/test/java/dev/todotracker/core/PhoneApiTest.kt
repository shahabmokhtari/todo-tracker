package dev.todotracker.core

import java.io.IOException
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class PhoneApiTest {
    private val a = UUID.fromString("0b5e0a43-34a0-4f2c-9a3e-1a2b3c4d5e6f")
    private val b = UUID.fromString("11111111-2222-3333-4444-555555555555")
    private val at = Instant.parse("2026-03-01T09:30:00Z")

    private fun request(action: RemoteAction) = PhoneApi.request(action)

    @Test
    fun Every_change_the_phone_makes_is_the_servers_request_with_its_field_names() {
        val cases = listOf(
            RemoteAction.Capture("Call mum @tomorrow", a) to PhoneRequest("POST", "/api/capture", """{"groupId":"$a","text":"Call mum @tomorrow"}"""),
            RemoteAction.Capture("Milk", null) to PhoneRequest("POST", "/api/capture", """{"text":"Milk"}"""),
            RemoteAction.AddSubtask(a, "Step") to PhoneRequest("POST", "/api/items", """{"parentId":"$a","title":"Step"}"""),
            RemoteAction.Complete(a) to PhoneRequest("POST", "/api/items/$a/complete"),
            RemoteAction.Reopen(a) to PhoneRequest("POST", "/api/items/$a/reopen"),
            RemoteAction.Archive(a) to PhoneRequest("POST", "/api/items/$a/archive"),
            RemoteAction.Unarchive(a) to PhoneRequest("POST", "/api/items/$a/unarchive"),
            RemoteAction.Schedule(a, at) to PhoneRequest("POST", "/api/items/$a/schedule", """{"at":"2026-03-01T09:30:00.000Z","notify":true}"""),
            RemoteAction.Schedule(a, at, notify = false) to PhoneRequest("POST", "/api/items/$a/schedule", """{"at":"2026-03-01T09:30:00.000Z","notify":false}"""),
            RemoteAction.BringBack(a) to PhoneRequest("POST", "/api/items/$a/schedule", """{"clear":true}"""),
            RemoteAction.WaitFor(a, b) to PhoneRequest("POST", "/api/items/$a/after", """{"afterId":"$b"}"""),
            RemoteAction.DismissReminder(a, b) to PhoneRequest("POST", "/api/items/$a/reminders/$b/dismiss"),
            RemoteAction.AddReminder(a, at, "Bring the forms") to PhoneRequest("POST", "/api/items/$a/reminders", """{"at":"2026-03-01T09:30:00.000Z","message":"Bring the forms"}"""),
            RemoteAction.AddReminder(a, at, null) to PhoneRequest("POST", "/api/items/$a/reminders", """{"at":"2026-03-01T09:30:00.000Z"}"""),
            RemoteAction.ArrangeNow(listOf(b, a)) to PhoneRequest("POST", "/api/now/order", """{"ids":["$b","$a"]}"""),
            RemoteAction.AddNote(a, "Said \"maybe\"") to PhoneRequest("POST", "/api/items/$a/notes", """{"text":"Said \"maybe\""}"""),
            RemoteAction.AddSteps(a, listOf("One", "Two"), 90) to PhoneRequest("POST", "/api/items/$a/steps", """{"stepDelayMinutes":90,"titles":["One","Two"]}"""),
            RemoteAction.AddSteps(a, listOf("One"), null) to PhoneRequest("POST", "/api/items/$a/steps", """{"titles":["One"]}"""),
            RemoteAction.Delete(a) to PhoneRequest("DELETE", "/api/items/$a"),
            RemoteAction.MoveToGroup(a, b) to PhoneRequest("POST", "/api/items/$a/move", """{"groupId":"$b"}"""),
            RemoteAction.AddGroup("Home") to PhoneRequest("POST", "/api/groups", """{"name":"Home"}"""),
            RemoteAction.UpdateGroup(a, "Home", "#22c55e") to PhoneRequest("PATCH", "/api/groups/$a", """{"color":"#22c55e","name":"Home"}"""),
            RemoteAction.UpdateGroup(a, "Home", null) to PhoneRequest("PATCH", "/api/groups/$a", """{"name":"Home"}"""),
            RemoteAction.DeleteGroup(a, b) to PhoneRequest("DELETE", "/api/groups/$a?moveTo=$b"),
            RemoteAction.SetDailyEnergy(12) to PhoneRequest("PUT", "/api/energy", """{"perDay":12}"""),
            RemoteAction.SetDailyEnergy(null) to PhoneRequest("PUT", "/api/energy", "{}"),
            RemoteAction.Pomodoro("start", a) to PhoneRequest("POST", "/api/pomodoro/start", """{"itemId":"$a"}"""),
            RemoteAction.Pomodoro("start") to PhoneRequest("POST", "/api/pomodoro/start"),
            RemoteAction.Pomodoro("next", a) to PhoneRequest("POST", "/api/pomodoro/next", """{"itemId":"$a"}"""),
            RemoteAction.StartTimer(a) to PhoneRequest("POST", "/api/timer/start", """{"itemId":"$a"}"""),
            RemoteAction.StopTimer to PhoneRequest("POST", "/api/timer/stop"),
        )
        for ((action, expected) in cases) assertEquals(action.toString(), expected, request(action))
    }

    @Test
    fun A_tasks_changes_are_one_PATCH_with_the_servers_ways_of_clearing() {
        fun patch(c: TaskChanges) = request(RemoteAction.Update(a, c)).also {
            assertEquals("PATCH", it.method)
            assertEquals("/api/items/$a", it.path)
        }.body
        assertEquals("""{"priority":"high","title":"Call mum"}""", patch(TaskChanges(title = "Call mum", priority = Priority.HIGH)))
        assertEquals("""{"details":"Notes"}""", patch(TaskChanges(details = "Notes")))
        assertEquals("""{"deadline":"2026-03-01T09:30:00.000Z"}""", patch(TaskChanges(deadline = at)))
        assertEquals("""{"clearDeadline":true}""", patch(TaskChanges(clearDeadline = true)))
        assertEquals("""{"sequential":false}""", patch(TaskChanges(sequential = false)))
        assertEquals("""{"stepDelayMinutes":150}""", patch(TaskChanges(stepDelay = Duration.ofMinutes(150))))
        assertEquals("""{"clearStepDelay":true}""", patch(TaskChanges(clearStepDelay = true)))
        assertEquals("""{"repeat":"every weekday"}""", patch(TaskChanges(repeat = "every weekday")))
        assertEquals("""{"repeat":""}""", patch(TaskChanges(repeat = "")))
        assertEquals("""{"repeat":""}""", patch(TaskChanges(clearRepeat = true)))
        assertEquals("""{"energy":2}""", patch(TaskChanges(energy = 2)))
        assertEquals("""{"energy":0}""", patch(TaskChanges(clearEnergy = true)))
    }

    @Test
    fun Every_request_the_phone_sends_is_on_the_computers_phone_allow_list() {
        // The routes Security.PhoneRoutes lets phones reach (C#); anything else is refused there.
        val id = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"
        val allowed = listOf(
            "GET" to "/health", "GET" to "/api/export", "POST" to "/api/capture", "POST" to "/api/items",
            "PATCH" to "/api/items/$id", "DELETE" to "/api/items/$id",
            "POST" to "/api/items/$id/(complete|reopen|archive|unarchive|notes|schedule|after|steps|reminders|move|reorder)",
            "POST" to "/api/items/$id/reminders/$id/dismiss", "POST" to "/api/now/(order|first)", "PUT" to "/api/energy",
            "POST" to "/api/groups", "PATCH" to "/api/groups/$id", "DELETE" to "/api/groups/$id",
            "POST" to "/api/pomodoro/(start|pause|resume|skip|reset|next)", "POST" to "/api/timer/(start|stop)",
            "POST" to "/api/phones/claim",
        ).map { (m, p) -> m to Regex("^$p$") }
        val all = listOf(
            RemoteAction.Capture("x", a), RemoteAction.AddSubtask(a, "x"), RemoteAction.Complete(a), RemoteAction.Reopen(a),
            RemoteAction.Archive(a), RemoteAction.Unarchive(a),
            RemoteAction.Schedule(a, at), RemoteAction.BringBack(a), RemoteAction.WaitFor(a, b), RemoteAction.DismissReminder(a, b),
            RemoteAction.AddReminder(a, at, null), RemoteAction.ArrangeNow(listOf(a)), RemoteAction.AddNote(a, "x"),
            RemoteAction.Update(a, TaskChanges(title = "x")), RemoteAction.AddSteps(a, listOf("x"), null), RemoteAction.Delete(a),
            RemoteAction.MoveToGroup(a, b), RemoteAction.AddGroup("x"), RemoteAction.UpdateGroup(a, "x", null),
            RemoteAction.DeleteGroup(a, b), RemoteAction.SetDailyEnergy(3), RemoteAction.StartTimer(a), RemoteAction.StopTimer,
        ) + listOf("start", "pause", "resume", "skip", "reset", "next").map { RemoteAction.Pomodoro(it) }
        val requests = all.map(PhoneApi::request) + listOf(PhoneApi.export, PhoneApi.health, PhoneApi.claim("c", "n"))
        for (r in requests) {
            val path = r.path.substringBefore('?')
            assertTrue("${r.method} ${r.path}", allowed.any { (m, p) -> m == r.method && p.matches(path) })
        }
    }

    @Test
    fun The_claim_sends_the_code_and_the_phones_name() {
        assertEquals(PhoneRequest("POST", "/api/phones/claim", """{"code":"abc","name":"Pixel 8"}"""), PhoneApi.claim("abc", "Pixel 8"))
    }
}

/** A computer that answers as told, and remembers what it was sent. */
private class FakeComputer(private val answer: (String, PhoneRequest, Map<String, String>) -> PhoneResponse) : PhoneTransport {
    val sent = mutableListOf<Pair<String, PhoneRequest>>()
    val headers = mutableListOf<Map<String, String>>()

    override fun exchange(host: String, request: PhoneRequest, headers: Map<String, String>): PhoneResponse {
        sent += host to request
        this.headers += headers
        return answer(host, request, headers)
    }
}

class PhoneClientTest {
    private val hosts = listOf("192.168.1.20:5318", "100.101.102.103:5318")

    @Test
    fun The_addresses_are_tried_in_turn_and_the_one_that_answered_goes_first_next_time() {
        val computer = FakeComputer { host, _, _ -> if (host.startsWith("192")) throw IOException("no route") else PhoneResponse(200, "{}") }
        val client = PhoneClient(hosts, computer, "ttp_x")
        client.send(PhoneApi.health)
        assertEquals(hosts, computer.sent.map { it.first })
        assertEquals("100.101.102.103:5318", client.current)
        computer.sent.clear()
        client.send(PhoneApi.health)
        assertEquals(listOf("100.101.102.103:5318"), computer.sent.map { it.first })
        assertEquals(listOf("100.101.102.103:5318", "192.168.1.20:5318"), PhoneClient(hosts, computer, preferred = "100.101.102.103:5318").order())
        // An address that isn't one of the computer's is never tried first.
        assertEquals(hosts, PhoneClient(hosts, computer, preferred = "10.0.0.9:5318").order())
    }

    @Test
    fun A_host_that_answers_at_all_is_the_computer_so_its_answer_counts() {
        val computer = FakeComputer { _, _, _ -> PhoneResponse(500, """{"title":"Broken"}""") }
        val error = assertThrows(PhoneException.Refused::class.java) { PhoneClient(hosts, computer, "ttp_x").send(PhoneApi.health) }
        assertEquals(500, error.status)
        assertEquals("Broken", error.message)
        assertEquals(1, computer.sent.size)
    }

    @Test
    fun No_address_answering_is_unreachable() {
        val computer = FakeComputer { _, _, _ -> throw IOException("timed out") }
        assertThrows(PhoneException.Unreachable::class.java) { PhoneClient(hosts, computer, "ttp_x").send(PhoneApi.health) }
        assertEquals(2, computer.sent.size)
    }

    @Test
    fun The_token_goes_with_every_request_and_a_401_means_disconnected() {
        val computer = FakeComputer { _, _, _ -> PhoneResponse(401, """{"detail":"This phone isn’t connected to Todo Tracker."}""") }
        val error = assertThrows(PhoneException.Revoked::class.java) { PhoneClient(hosts, computer, "ttp_secret").perform(RemoteAction.StopTimer) }
        assertEquals("This phone isn’t connected to Todo Tracker.", error.message)
        assertEquals("Bearer ttp_secret", computer.headers.single()["Authorization"])
    }

    @Test
    fun The_board_is_read_again_only_when_it_changed() {
        val computer = FakeComputer { _, _, h -> if (h["If-None-Match"] == "\"abc\"") PhoneResponse(304, "") else PhoneResponse(200, "{\"x\":1}", "\"abc\"") }
        val client = PhoneClient(hosts, computer, "ttp_x")
        val first = client.export(null)!!
        assertEquals("{\"x\":1}", first.text)
        assertEquals("\"abc\"", first.etag)
        assertNull(client.export(first.etag))
        assertEquals(PhoneApi.export, computer.sent.last().second)
    }

    @Test
    fun Claiming_trades_the_code_for_the_token_and_says_why_when_it_doesnt_work() {
        val ok = FakeComputer { _, _, _ -> PhoneResponse(200, """{"token":"ttp_abc","computer":"Studio"}""") }
        assertEquals(Claimed("ttp_abc", "Studio"), PhoneClient(hosts, ok).claim("code", "Pixel"))
        assertNull(ok.headers.single()["Authorization"])

        val used = FakeComputer { _, _, _ -> PhoneResponse(403, """{"type":"x","title":"Forbidden","status":403,"detail":"That code didn’t work."}""") }
        assertEquals("That code didn’t work.", assertThrows(PhoneException.Refused::class.java) { PhoneClient(hosts, used).claim("code", "Pixel") }.message)
        assertEquals(1, used.sent.size)

        val busy = FakeComputer { _, _, _ -> PhoneResponse(429, "") }
        assertEquals(429, assertThrows(PhoneException.Refused::class.java) { PhoneClient(hosts, busy).claim("code", "Pixel") }.status)

        val odd = FakeComputer { _, _, _ -> PhoneResponse(200, """{"token":"not-a-phone-token"}""") }
        assertThrows(PhoneException.Refused::class.java) { PhoneClient(hosts, odd).claim("code", "Pixel") }
    }
}

class RemoteStateTest {
    private val t0 = Instant.parse("2026-01-05T09:00:00Z")
    private val zone = ZoneId.of("UTC")

    @Test
    fun Reading_connects_failing_to_reach_keeps_the_last_read_and_a_401_disconnects() {
        val connected = RemoteState.read(t0)
        assertEquals(RemoteState.Connected(t0), connected)
        assertTrue(connected.canChange)

        val away = RemoteState.failed(connected, PhoneException.Unreachable("timed out"), t0)
        assertEquals(RemoteState.Unreachable(t0), away)
        assertTrue(!away.canChange)
        assertEquals(RemoteState.Connected(t0.plusSeconds(30)), RemoteState.read(t0.plusSeconds(30)))

        assertEquals(RemoteState.Revoked, RemoteState.failed(connected, PhoneException.Revoked("gone"), t0))
        assertEquals(RemoteState.Revoked, RemoteState.failed(away, PhoneException.Revoked("gone"), t0))
        assertEquals(RemoteState.Revoked, RemoteState.failed(RemoteState.Revoked, PhoneException.Unreachable("x"), t0))

        // One change the computer refused (a 404, a 409) leaves the connection as it was; phones switched off doesn't.
        assertEquals(connected, RemoteState.failed(connected, PhoneException.Refused(409, "In order"), t0))
        assertEquals(RemoteState.Unreachable(t0, "Turn it on."), RemoteState.failed(connected, PhoneException.Refused(403, "Turn it on."), t0))
        assertEquals(RemoteState.Unreachable(null), RemoteState.failed(RemoteState.Connecting, PhoneException.Unreachable("x"), null))
    }

    @Test
    fun The_banner_says_calmly_what_is_shown_and_what_to_do() {
        assertNull(RemoteState.banner(RemoteState.Connected(t0), "Studio", t0, zone))
        assertNull(RemoteState.banner(RemoteState.Connecting, "Studio", t0, zone))
        assertEquals(
            "Can't reach Studio. You're seeing your tasks from ${Snooze.describe(t0, t0.plusSeconds(600), zone)}; changes wait until you're back on its Wi-Fi or Tailscale.",
            RemoteState.banner(RemoteState.Unreachable(t0), "Studio", t0.plusSeconds(600), zone),
        )
        assertTrue(RemoteState.banner(RemoteState.Revoked, "Studio", t0, zone)!!.startsWith("This phone was disconnected on Studio."))
    }

    @Test
    fun The_computers_reminders_are_told_once_each_when_they_come_due_whatever_the_computer_marked() {
        val board = TaskBoard()
        val task = board.addTask(NewTask("Call the bank"), now = t0)
        val early = board.addReminder(task.id, t0.plusSeconds(60), "First", now = t0)
        val late = board.addReminder(task.id, t0.plusSeconds(600), "Second", now = t0)
        val gone = board.addTask(NewTask("Done already"), now = t0)
        board.addReminder(gone.id, t0.plusSeconds(60), null, now = t0)
        board.complete(gone.id, now = t0)
        // The computer told it (and marked it); the phone tells it too.
        board.markNotified(task.id, early.id, t0.plusSeconds(60))

        assertEquals(t0.plusSeconds(60), RemoteReminders.nextAt(board, t0))
        assertEquals(listOf(early.id), RemoteReminders.due(board, t0, t0.plusSeconds(61)).map { it.reminder.id })
        // Looked at 61 s: only what comes due after that is told next.
        assertEquals(emptyList<UUID>(), RemoteReminders.due(board, t0.plusSeconds(61), t0.plusSeconds(120)).map { it.reminder.id })
        assertEquals(listOf(late.id), RemoteReminders.due(board, t0.plusSeconds(61), t0.plusSeconds(600)).map { it.reminder.id })

        board.dismissReminder(task.id, late.id, now = t0)
        assertEquals(emptyList<UUID>(), RemoteReminders.due(board, t0.plusSeconds(61), t0.plusSeconds(600)).map { it.reminder.id })
        assertNull(RemoteReminders.nextAt(board, t0.plusSeconds(61)))
    }
}
