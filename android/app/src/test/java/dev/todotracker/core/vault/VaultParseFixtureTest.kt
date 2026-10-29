package dev.todotracker.core.vault

import dev.todotracker.core.BoardCodec
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test

/** tests/fixtures/vault/parse.json: files typed by hand, read and written back exactly as the C# core does. */
class VaultParseFixtureTest {
    @Test
    fun everyHandTypedFileReadsAndRewritesTheSame() {
        val cases = Fixtures.read("vault/parse.json").arr("cases")
        assertTrue(cases.size >= 12)
        for (c in cases) {
            val o = c.jsonObject
            val name = o.s("name")
            val path = o.s("path")
            val context = Fixtures.context(path)
            if (o["error"] != null) {
                try {
                    TaskMarkdown.parse(o.s("text"), Net.stem(path), context)
                    fail("$name should be refused")
                } catch (e: VaultFormatException) {
                    // Refused, as on a computer.
                }
                continue
            }
            val parsed = TaskMarkdown.parse(o.s("text"), Net.stem(path), context)
            assertEquals("$name item", o["item"], BoardCodec.encodeItem(parsed.root))
            assertEquals("$name needsWrite", o["needsWrite"]!!.jsonPrimitive.boolean, parsed.needsWrite)
            assertEquals("$name hasAppId", o["hasAppId"]!!.jsonPrimitive.boolean, parsed.hasAppId)
            assertEquals("$name rewritten", o.s("rewritten"), TaskMarkdown.render(parsed.root, context, parsed))
        }
    }

    @Suppress("unused")
    private fun unused() = JsonPrimitive(0)
}
