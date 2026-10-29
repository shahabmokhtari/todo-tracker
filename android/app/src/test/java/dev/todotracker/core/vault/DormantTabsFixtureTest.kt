package dev.todotracker.core.vault

import dev.todotracker.core.Actor
import java.io.File
import java.nio.file.Files
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * tests/fixtures/vault/render.json "dormantTabs": a tab with no folder here (its folder is empty on another device, and
 * empty folders don't sync) keeps its place in config.json, so every device writes the same bytes.
 */
class DormantTabsFixtureTest {
    @Test
    fun aDormantTabKeepsItsPlaceInTheSettings() {
        val cases = Fixtures.read("vault/render.json").arr("dormantTabs")
        assertTrue(cases.size >= 5)
        for (c in cases) {
            val o = c.jsonObject
            val name = o.s("name")
            val dir = Files.createTempDirectory("tt-dormant").toFile()
            try {
                val root = File(dir, "vault")
                for (folder in o.arr("folders")) File(root, folder.jsonPrimitive.content).mkdirs()
                File(root, ".todo-tracker").mkdirs()
                File(root, ".todo-tracker/config.json").writeText(o.s("config"))
                val store = VaultStore.open(root, Fixtures.zone, { Fixtures.now })
                store.read { it.groups.size }
                val change = o["change"] as? JsonObject
                if (change != null) {
                    store.update { b ->
                        val group = b.groups.single { it.name == change.s("group") }
                        if (change.s("kind") == "rename") {
                            b.updateGroup(group.id, change.s("to"), group.color, Actor.USER, Fixtures.now)
                        } else {
                            b.deleteGroup(group.id, b.groups.single { it.name == change.s("moveTo") }.id, Actor.USER, Fixtures.now)
                        }
                    }
                }
                assertEquals(name, o.s("expected"), File(root, ".todo-tracker/config.json").readText())
            } finally {
                dir.deleteRecursively()
            }
        }
    }
}
