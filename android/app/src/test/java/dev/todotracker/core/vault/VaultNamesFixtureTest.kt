package dev.todotracker.core.vault

import java.time.Instant
import java.time.ZoneId
import java.util.UUID
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Test

/** tests/fixtures/vault/names.json: file names, derived ids, JSON/YAML escaping, local times, syncable paths. */
class VaultNamesFixtureTest {
    private val names = Fixtures.read("vault/names.json")

    @Test
    fun titlesBecomeTheSameFileAndFolderNames() {
        for (c in names.arr("titles")) {
            val o = c.jsonObject
            assertEquals(o.s("title"), o.s("file"), VaultFiles.safeName(o.s("title")))
            assertEquals(o.s("title"), o.s("folder"), VaultFiles.safeFolderName(o.s("title")))
        }
    }

    @Test
    fun idsDerivedFromTextAreTheSame() {
        for (c in names.arr("stableIds")) {
            val o = c.jsonObject
            assertEquals(o.s("key"), o.s("id"), Net.stableGuid(o.s("key")).toString())
        }
        for (c in names.arr("groupIds")) {
            val o = c.jsonObject
            assertEquals(o.s("folder"), o.s("key"), "group|" + Net.upper(o.s("folder")))
            assertEquals(o.s("folder"), o.s("id"), Net.stableGuid("group|" + Net.upper(o.s("folder"))).toString())
        }
        for (c in names.arr("blockIds")) {
            val o = c.jsonObject
            assertEquals(o.s("blockId"), VaultText.blockId(Net.parseGuid(o.s("id"))!!))
        }
    }

    @Test
    fun stringsAreEscapedTheSameWay() {
        for (c in names.arr("strings")) {
            val o = c.jsonObject
            val text = o.s("text")
            assertEquals(text, o.s("hidden"), VaultText.hidden(text))
            assertEquals(text, o.s("yaml"), VaultText.yamlScalar(text))
            assertEquals(text, o.s("json"), NetJson.string(text, JsonEscaping.DEFAULT))
        }
    }

    @Test
    fun localTimesLandOnTheSameInstants() {
        val local = names.obj("localTimes")
        val zone = ZoneId.of(local.s("zone"))
        for (c in local.arr("typed")) {
            val o = c.jsonObject
            assertEquals(o.s("text"), o.s("utc"), VaultText.utc(VaultText.parseTime(o.s("text"), zone, o.s("defaultTime"))!!))
        }
        for (c in local.arr("shown")) {
            val o = c.jsonObject
            val at = Instant.parse(o.s("utc"))
            assertEquals(o.s("utc"), o.s("localDateTime"), VaultText.localDateTime(at, zone))
            assertEquals(o.s("utc"), o.s("localDate"), VaultText.localDate(at, zone))
            assertEquals(o.s("utc"), o.s("localMinute"), VaultText.localMinute(at, zone))
        }
    }

    @Test
    fun theSamePathsSync() {
        for (c in names.arr("syncablePaths")) {
            val o = c.jsonObject
            assertEquals(o.s("path"), o["syncable"]!!.jsonPrimitive.content.toBoolean(), VaultFiles.isSyncablePath(o.s("path")))
        }
    }

    @Test
    fun guidsReadLikeDotNet() {
        val id = UUID.fromString("7b0c2f9e-0000-4000-8000-000000000001")
        assertEquals(id, Net.parseGuid("7b0c2f9e000040008000000000000001"))
        assertEquals(id, Net.parseGuid(" {7B0C2F9E-0000-4000-8000-000000000001} "))
        assertEquals(null, Net.parseGuid("1-1-1-1-1"))
        assertEquals(null, Net.parseGuid("202601051200"))
    }
}
