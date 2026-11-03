package dev.onething.core

import java.io.File
import java.io.IOException
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.int
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Send feedback: the same body and the same reading of answers in C#, Swift and Kotlin (tests/fixtures/feedback.json). */
class FeedbackTest {
    private fun fixture() = run {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "tests/fixtures/feedback.json").isFile) dir = dir.parentFile
        Json.parseToJsonElement(File(dir ?: error("tests/fixtures not found"), "tests/fixtures/feedback.json").readText()).jsonObject
    }

    private fun JsonObject.text(key: String): String? = this[key]?.takeIf { it !is JsonNull }?.jsonPrimitive?.contentOrNull

    private fun draft(o: JsonObject) = Feedback.Draft(
        Feedback.Kind.of(o.text("kind")!!)!!, o.text("message")!!, o.text("contact"), o.text("source")!!, o.text("clientID")!!,
    )

    private fun device(o: JsonObject) = Feedback.Device(
        o.text("platform")!!, o.text("appVersion"), o.text("build"), o.text("os"), o.text("device"), o.text("language"), o.text("screen"),
    )

    private val phone = Feedback.Device("iOS", "1.0.0", null, "Android 15", "Pixel 9", "en-US", null)

    @Test
    fun theRelayKeyTextsKindsAndSourcesAreTheSharedOnes() {
        val f = fixture()
        val relay = f["relay"]!!.jsonObject
        assertEquals(relay.text("url"), Feedback.RELAY_URL)
        assertEquals(relay.text("publicKey"), Feedback.PUBLIC_KEY)
        assertEquals(relay["maxMessage"]!!.jsonPrimitive.int, Feedback.MAX_MESSAGE)
        assertEquals(relay["maxContact"]!!.jsonPrimitive.int, Feedback.MAX_CONTACT)
        val texts = f["texts"]!!.jsonObject
        assertEquals(texts.text("sent"), Feedback.SENT_TEXT)
        assertEquals(texts.text("retry"), Feedback.RETRY_TEXT)
        assertEquals(texts.text("failed"), Feedback.FAILED_TEXT)
        assertEquals(texts.text("finePrint"), Feedback.FINE_PRINT)
        assertEquals(texts.text("contactHint"), Feedback.CONTACT_HINT)
        assertEquals(f["kinds"]!!.jsonArray.map { it.jsonObject.text("id") to it.jsonObject.text("label") }, Feedback.Kind.entries.map { it.id to it.label })
        assertEquals(f["sources"]!!.jsonArray.map { it.jsonPrimitive.content }, Feedback.SOURCES)
    }

    @Test
    fun aDraftBecomesTheBodyTheSharedFixtureSays() {
        for (case in fixture()["bodies"]!!.jsonArray.map { it.jsonObject }) {
            val name = case.text("name")!!
            val max = case["maxMessage"]?.jsonPrimitive?.intOrNull ?: Feedback.MAX_MESSAGE
            val body = Feedback.body(draft(case["draft"]!!.jsonObject), device(case["device"]!!.jsonObject), max)
            val expected = case["body"]!!
            if (expected is JsonNull) {
                assertNull(name, body)
                continue
            }
            assertEquals(name, expected, body)
            // Key order matters to the relay's issue text: the same order as the fixture.
            assertEquals(name, expected.jsonObject.keys.toList(), body!!.keys.toList())
            assertEquals(name, expected.jsonObject["metadata"]!!.jsonObject.keys.toList(), body["metadata"]!!.jsonObject.keys.toList())
        }
    }

    @Test
    fun answersAreReadAsTheSharedFixtureSays() {
        for (case in fixture()["outcomes"]!!.jsonArray.map { it.jsonObject }) {
            val status = case["status"]!!.jsonPrimitive.intOrNull
            assertEquals("status $status", case.text("outcome"), Feedback.classify(status).name.lowercase())
        }
    }

    @Test
    fun theCounterCountsCodePoints() {
        assertEquals(3, Feedback.length(" 😀a\u0007b "))
        assertEquals(2000, Feedback.cleanMessage("x".repeat(2500)).length)
    }

    @Test
    fun theTitleIsTheFirstLineCutTo256CodePoints() {
        assertEquals("Bug · Android: It crashes", Feedback.title(Feedback.Kind.BUG, "Android", "It crashes\nwhen I tap Done"))
        val long = Feedback.title(Feedback.Kind.IDEA, "Android", "😀".repeat(300))
        assertEquals(256, long.codePointCount(0, long.length))
        assertTrue(long.startsWith("Idea · Android: 😀"))
    }

    @Test
    fun debugBuildsOnlyLogUnlessAskedToSend() {
        assertTrue(Feedback.sendsForReal(debugBuild = false, sendFlag = false))
        assertFalse(Feedback.sendsForReal(debugBuild = true, sendFlag = false))
        assertTrue(Feedback.sendsForReal(debugBuild = true, sendFlag = true))

        val logged = mutableListOf<String>()
        val client = FeedbackClient(transport = { _, _, _ -> error("must not send") }, sendForReal = false, log = { logged += it })
        assertEquals(Feedback.Outcome.SENT, client.send(Feedback.Draft(Feedback.Kind.BUG, "Hi", null, "button", "id-1"), phone))
        assertTrue(logged.single().contains("\"message\":\"Hi\""))
    }

    @Test
    fun aRetryAfterABusyRelaySendsTheSameClientIdWithThePublicKey() {
        val seen = mutableListOf<Triple<String, Map<String, String>, String>>()
        val answers = ArrayDeque(listOf<Int?>(null, 503, 202))
        val client = FeedbackClient(transport = { url, headers, body ->
            seen += Triple(url, headers, body)
            answers.removeFirst() ?: throw IOException("offline")
        }, url = "https://relay.test/v1/issues")
        val draft = Feedback.Draft(Feedback.Kind.IDEA, "More colors", "", "shake", "4b1a1f1e-0000-4000-8000-000000000001")

        assertEquals(Feedback.Outcome.RETRY, client.send(draft, phone))
        assertEquals(Feedback.Outcome.RETRY, client.send(draft, phone))
        assertEquals(Feedback.Outcome.SENT, client.send(draft, phone))

        assertEquals(3, seen.size)
        for ((url, headers, body) in seen) {
            assertEquals("https://relay.test/v1/issues", url)
            assertEquals("Bearer pk_gGPPMjRSsuHaXRdgdXvUevvL", headers["Authorization"])
            assertEquals("application/json", headers["Content-Type"])
            assertEquals(draft.clientId, Json.parseToJsonElement(body).jsonObject.text("clientID"))
        }
    }

    @Test
    fun aRejectedNoteFailsAndNothingToSendIsNeverPosted() {
        val client = FeedbackClient(transport = { _, _, _ -> 400 })
        assertEquals(Feedback.Outcome.FAILED, client.send(Feedback.Draft(Feedback.Kind.BUG, "Hi", null, "button", "x"), phone))
        val never = FeedbackClient(transport = { _, _, _ -> error("must not send") })
        assertEquals(Feedback.Outcome.FAILED, never.send(Feedback.Draft(Feedback.Kind.BUG, "  \u0007 ", null, "button", "x"), phone))
    }
}
