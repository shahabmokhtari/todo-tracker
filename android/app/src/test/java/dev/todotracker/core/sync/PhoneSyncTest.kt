package dev.todotracker.core.sync

import dev.todotracker.core.Actor
import dev.todotracker.core.BoardCodec
import dev.todotracker.core.LocalBoard
import dev.todotracker.core.NewTask
import dev.todotracker.core.TaskBoard
import dev.todotracker.core.vault.VaultStore
import java.io.File
import java.nio.file.Files
import java.time.Duration
import java.time.Instant
import java.time.ZoneOffset
import java.util.Base64
import org.junit.After
import org.junit.Ignore
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

/** The app's side of phase 2: the phone's tasks folder (moved from board.json) syncs on its own with the other devices. */
class PhoneSyncTest {
    private val dir: File = Files.createTempDirectory("tt-phone-sync").toFile()
    private var now = Instant.parse("2026-01-05T14:00:00Z")
    private val shared = File(dir, "shared")

    @After
    fun cleanUp() {
        dir.deleteRecursively()
    }

    private class MapValues : KeyValues {
        val map = HashMap<String, String>()

        override fun get(key: String) = map[key]

        override fun set(key: String, value: String?) {
            if (value == null) map.remove(key) else map[key] = value
        }
    }

    /** Stands in for the Keystore: a reversible seal the test can check isn't the plain text. */
    private class FakeBox(var works: Boolean = true) : SecretBox {
        override fun seal(plain: String) = "sealed:" + Base64.getEncoder().encodeToString(plain.reversed().toByteArray())

        override fun open(sealed: String): String? =
            if (!works || !sealed.startsWith("sealed:")) null else String(Base64.getDecoder().decode(sealed.removePrefix("sealed:"))).reversed()
    }

    /** A computer: its tasks folder and the C#-equivalent engine, through the same shared folder. */
    private inner class Laptop {
        val store = VaultStore.open(File(dir, "laptop/vault"), ZoneOffset.UTC, { now })
        val engine = SyncEngine(store, FolderRemote(shared), SyncState(File(dir, "laptop/sync"), File(dir, "laptop/device"), "Laptop")) { now }

        fun sync() = engine.sync().also { tick() }

        fun titles() = store.read { b -> b.items.map { it.title }.sorted() }
    }

    /** The phone as the app sets it up: files dir with (maybe) an old board.json, the one store, and PhoneSync. */
    private inner class Phone(val files: File = File(dir, "phone")) {
        val values = MapValues()
        val box = FakeBox()
        val store: VaultStore
        val sync: PhoneSync

        init {
            files.mkdirs()
            store = LocalBoard.open(files, ZoneOffset.UTC) { now }.store!!
            sync = PhoneSync(store, SyncSettings(values, box), File(files, "sync"), { "Pixel" }, { FolderRemote(shared) }, { LocalBoard.legacyFile(files).takeIf { it.exists() } }) { now }
        }

        fun sync() = sync.sync().also { tick() }

        fun titles() = store.read { b -> b.items.map { it.title }.sorted() }
    }

    private fun tick() {
        now = now.plus(Duration.ofSeconds(1))
    }

    private fun writeLegacy(files: File, vararg titles: String): TaskBoard {
        files.mkdirs()
        val board = TaskBoard()
        for (t in titles) board.addTask(NewTask(t, groupId = board.groups[0].id), Actor.USER, now)
        LocalBoard.legacyFile(files).writeText(BoardCodec.encode(board))
        return board
    }

    // ---- Moving board.json into the tasks folder ----

    @Test
    fun anOldBoardMovesIntoTheTasksFolderOnceAndIsKeptAside() {
        val files = File(dir, "phone")
        writeLegacy(files, "Call the dentist", "Water the plants")
        val first = LocalBoard.open(files, ZoneOffset.UTC) { now }
        assertEquals(2, first.migrated)
        assertNull(first.problem)
        assertEquals(listOf("Call the dentist", "Water the plants"), first.store!!.read { b -> b.items.map { it.title }.sorted() })
        assertFalse(File(files, "board.json").exists())
        assertTrue(File(files, "board.json.migrated").exists())
        assertTrue(File(files, "vault/Work/Call the dentist.md").exists())

        // The next start just opens the folder: nothing comes in twice, the kept copy stays as it is.
        val kept = File(files, "board.json.migrated").readText()
        val second = LocalBoard.open(files, ZoneOffset.UTC) { now }
        assertNull(second.migrated)
        assertEquals(2, second.store!!.read { it.items.size })
        assertEquals(kept, File(files, "board.json.migrated").readText())
    }

    @Test
    fun aBoardLeftBehindByAnInterruptedMoveAddsOnlyWhatIsMissing() {
        val files = File(dir, "phone")
        val board = writeLegacy(files, "Call the dentist")
        LocalBoard.open(files, ZoneOffset.UTC) { now }
        // As if the app stopped before setting the file aside, and the old app added one more meanwhile.
        board.addTask(NewTask("Buy stamps", groupId = board.groups[0].id), Actor.USER, now)
        File(files, "board.json").writeText(BoardCodec.encode(board))
        val again = LocalBoard.open(files, ZoneOffset.UTC) { now }
        assertEquals(1, again.migrated)
        assertEquals(listOf("Buy stamps", "Call the dentist"), again.store!!.read { b -> b.items.map { it.title }.sorted() })
        assertTrue(File(files, "board.json.migrated 2").exists())
    }

    @Test
    fun aBoardFromANewerAppIsNeitherMovedNorChanged() {
        val files = File(dir, "phone").also { it.mkdirs() }
        val text = """{"schemaVersion":99,"groups":[],"items":[]}"""
        File(files, "board.json").writeText(text)
        val opened = LocalBoard.open(files, ZoneOffset.UTC) { now }
        assertNull(opened.store)
        assertTrue(opened.problem!!.contains("newer"))
        assertEquals(text, File(files, "board.json").readText())
        assertFalse(File(files, "vault").exists())
    }

    @Test
    fun aTasksFolderThatCantBeOpenedTurnsChangesOffWithoutTouchingTheOldBoard() {
        val files = File(dir, "phone")
        writeLegacy(files, "Call the dentist")
        val text = File(files, "board.json").readText()
        // Something in the way where the folder goes.
        File(files, "vault").writeText("not a folder")
        val opened = LocalBoard.open(files, ZoneOffset.UTC) { now }
        assertNull(opened.store)
        assertTrue(opened.problem!!.startsWith("Couldn't open your tasks"))
        assertEquals(text, File(files, "board.json").readText())
        assertFalse(File(files, "board.json.migrated").exists())
    }

    @Test
    fun aHalfBuiltFolderFromAnInterruptedMoveIsNotTakenForTheTasks() {
        val files = File(dir, "phone")
        writeLegacy(files, "Call the dentist", "Water the plants")
        // A move that stopped half way leaves its work in a temporary folder, never in vault/.
        File(files, "vault.moving/Work").mkdirs()
        File(files, "vault.moving/Work/Call the dentist.md").writeText("half")
        val opened = LocalBoard.open(files, ZoneOffset.UTC) { now }
        assertEquals(2, opened.migrated)
        assertEquals(listOf("Call the dentist", "Water the plants"), opened.store!!.read { b -> b.items.map { it.title }.sorted() })
        assertFalse(File(files, "vault.moving").exists())
    }

    @Test
    fun aBackupLeavesOutWhatMakesAPhoneItselfButKeepsTheTasks() {
        val rules = listOf(File("src/main/res/xml/backup_rules.xml"), File("src/main/res/xml/data_extraction_rules.xml")).map { it.readText() }
        for (r in rules) {
            assertTrue(r, r.contains("""domain="sharedpref" path="sync.xml""""))
            assertTrue(r, r.contains("""domain="sharedpref" path="phone.xml""""))
            assertTrue(r, r.contains("""domain="file" path="sync/""""))
            assertFalse(r, r.contains("""path="vault"""))
        }
        val manifest = File("src/main/AndroidManifest.xml").readText()
        assertTrue(manifest.contains("android:dataExtractionRules=\"@xml/data_extraction_rules\""))
        assertTrue(manifest.contains("android:fullBackupContent=\"@xml/backup_rules\""))
    }

    // ---- Joining and syncing ----

    @Test
    fun joiningALibraryThatHasTasksAddsThePhonesBesideThemWithoutCopies() {
        val laptop = Laptop()
        laptop.store.update { it.addTask(NewTask("Plan the trip", groupId = it.groups[0].id), Actor.USER, now) }
        laptop.sync()

        writeLegacy(File(dir, "phone"), "Call the dentist")
        val phone = Phone()
        phone.sync.turnOn(SyncSettings.FOLDER, null, "Todo Tracker", connected = false)
        val joined = phone.sync()
        assertTrue(joined.joined)
        assertTrue(joined.changed)
        assertTrue(phone.sync.settings.joined)
        assertEquals(listOf("Call the dentist", "Plan the trip"), phone.titles())

        laptop.sync()
        assertEquals(listOf("Call the dentist", "Plan the trip"), laptop.titles())
        // The same tabs on both, once each (the phone's Work met the laptop's Work).
        assertEquals(laptop.store.read { b -> b.groups.map { it.name } }, phone.store.read { b -> b.groups.map { it.name } })
        assertEquals(listOf("Work", "Personal"), phone.store.read { b -> b.groups.map { it.name } })

        // Syncing again changes nothing on either.
        assertFalse(phone.sync().changed)
        assertEquals(0, laptop.sync().changes)
        assertEquals(2, phone.store.read { it.items.size })
        assertTrue(phone.sync.status().conflicts.isEmpty())
    }

    @Test
    fun aChangeOnThePhoneReachesTheOtherDeviceAndBack() {
        val laptop = Laptop()
        laptop.sync()
        val phone = Phone()
        phone.sync.turnOn(SyncSettings.FOLDER, null, "Todo Tracker", connected = false)
        phone.sync()

        val id = phone.store.update { it.addTask(NewTask("Renew passport", groupId = it.groups[0].id), Actor.USER, now).id }
        phone.sync()
        laptop.sync()
        assertEquals(listOf("Renew passport"), laptop.titles())
        // The phone sees that both now agree (it syncs every minute): the next change merges against that.
        phone.sync()

        laptop.store.update { it.complete(id, Actor.USER, now) }
        laptop.sync()
        assertTrue(phone.sync().changed)
        assertTrue(phone.store.read { it.get(id).isDone })
        assertEquals(listOf("Laptop"), phone.sync.status().devices.map { it.name })
    }

    @Test
    fun theFirstSyncWaitsForEverythingBeforePublishing() {
        val laptop = Laptop()
        laptop.store.update { it.addTask(NewTask("On the laptop", groupId = it.groups[0].id), Actor.USER, now) }
        laptop.sync()
        File(shared, "blobs").walkTopDown().filter { it.isFile }.first { it.readText().contains("# On the laptop") }.delete()

        val phone = Phone()
        phone.sync.turnOn(SyncSettings.FOLDER, null, "Todo Tracker", connected = false)
        val first = phone.sync()
        assertFalse(first.joined)
        assertFalse(phone.sync.settings.joined)
        assertEquals(PhoneSync.WAITING, phone.sync.settings.notice)
        assertEquals(listOf(laptop.engine.state.deviceId + ".json"), File(shared, "devices").list()!!.toList())

        // The laptop writes it again; the next sync joins.
        laptop.store.update { it.addTask(NewTask("Another", groupId = it.groups[0].id), Actor.USER, now) }
        laptop.sync()
        assertTrue(phone.sync().joined)
        assertNull(phone.sync.settings.notice)
        assertEquals(2, File(shared, "devices").list()!!.size)
    }

    @Test
    fun aForgottenDeviceIsNoLongerListed() {
        val laptop = Laptop()
        laptop.sync()
        val phone = Phone()
        phone.sync.turnOn(SyncSettings.FOLDER, null, "Todo Tracker", connected = false)
        phone.sync()
        assertEquals(1, phone.sync.status().devices.size)
        phone.sync.forget(laptop.engine.state.deviceId)
        assertTrue(phone.sync.status().devices.isEmpty())
    }

    // ---- Modes ----

    @Test
    fun syncCantBeTurnedOnWhileConnectedToAComputer() {
        val phone = Phone()
        val e = assertThrows(IllegalStateException::class.java) { phone.sync.turnOn(SyncSettings.FOLDER, null, "Todo Tracker", connected = true) }
        assertTrue(e.message!!.contains("Disconnect"))
        assertFalse(phone.sync.on)
    }

    @Test
    fun turningSyncOffKeepsTheTasksAndTurningItOnAgainPullsFirst() {
        val laptop = Laptop()
        laptop.sync()
        val phone = Phone()
        phone.sync.turnOn(SyncSettings.FOLDER, null, "Todo Tracker", connected = false)
        phone.store.update { it.addTask(NewTask("Mine", groupId = it.groups[0].id), Actor.USER, now) }
        phone.sync()
        phone.sync.turnOff()
        assertFalse(phone.sync.on)
        assertEquals(listOf("Mine"), phone.titles())

        // Meanwhile the laptop adds one; on again, it joins again (pulls first) and nothing is doubled.
        laptop.sync()
        laptop.store.update { it.addTask(NewTask("Theirs", groupId = it.groups[0].id), Actor.USER, now) }
        laptop.sync()
        phone.sync.turnOn(SyncSettings.FOLDER, null, "Todo Tracker", connected = false)
        assertFalse(phone.sync.settings.joined)
        assertTrue(phone.sync().joined)
        assertEquals(listOf("Mine", "Theirs"), phone.titles())
        laptop.sync()
        assertEquals(listOf("Mine", "Theirs"), laptop.titles())
    }

    @Test
    fun anotherLibraryIsJoinedAfresh() {
        val phone = Phone()
        phone.sync.turnOn(SyncSettings.GIST, "t", "Todo Tracker", connected = false)
        phone.sync.settings.gistId = "g1"
        phone.sync.settings.joinedTo = "gist|Todo Tracker"
        assertTrue(phone.sync.settings.joined)
        phone.sync.changeLibrary("Work stuff")
        assertFalse(phone.sync.settings.joined)
        assertNull(phone.sync.settings.gistId)
        assertEquals("Work stuff", phone.sync.settings.library)
    }

    // ---- The GitHub sign-in ----

    @Test
    fun theTokenIsKeptSealedAndForgottenWhenSyncIsTurnedOff() {
        val phone = Phone()
        phone.sync.turnOn(SyncSettings.GIST, "  ghp_secret123  ", "Todo Tracker", connected = false)
        val kept = phone.values.map["token"]!!
        assertFalse(kept.contains("ghp_secret123"))
        assertEquals("ghp_secret123", phone.sync.settings.token)

        // A key that can't be read any more (restored to another phone): no token, sign in again.
        phone.box.works = false
        assertNull(phone.sync.settings.token)
        phone.box.works = true

        phone.sync.turnOff()
        assertNull(phone.values.map["token"])
        assertNull(phone.sync.settings.token)
    }

    @Test
    fun aPhoneAndALaptopMeetInAGistWithThePhonesToken() {
        val github = FakeGitHub().apply { token = "ghp_phone" }
        val laptopStore = VaultStore.open(File(dir, "laptop/vault"), ZoneOffset.UTC, { now })
        var laptopGist: String? = null
        val laptop = SyncEngine(laptopStore, GistRemote(github, { "ghp_phone" }, { laptopGist }, { laptopGist = it }), SyncState(File(dir, "laptop/sync"), File(dir, "laptop/device"), "Laptop")) { now }
        laptopStore.update { it.addTask(NewTask("From the laptop", groupId = it.groups[0].id), Actor.USER, now) }
        laptop.sync()
        tick()

        val files = File(dir, "phone").also { it.mkdirs() }
        val values = MapValues()
        val store = LocalBoard.open(files, ZoneOffset.UTC) { now }.store!!
        val settings = SyncSettings(values, FakeBox())
        val sync = PhoneSync(store, settings, File(files, "sync"), { "Pixel" }, { s ->
            GistRemote(github, { s.token ?: throw java.io.IOException("Sign in to GitHub again.") }, { s.gistId }, { s.gistId = it }, s.library)
        }) { now }
        sync.turnOn(SyncSettings.GIST, "ghp_phone", "Todo Tracker", connected = false)
        store.update { it.addTask(NewTask("From the phone", groupId = it.groups[0].id), Actor.USER, now) }
        assertTrue(sync.sync().joined)
        assertEquals(laptopGist, settings.gistId)
        assertEquals(1, github.ids.size)
        assertEquals(listOf("From the laptop", "From the phone"), store.read { b -> b.items.map { it.title }.sorted() })
        tick()
        laptop.sync()
        assertEquals(listOf("From the laptop", "From the phone"), laptopStore.read { b -> b.items.map { it.title }.sorted() })

        // A token GitHub doesn't take is said plainly.
        github.token = "another"
        tick()
        store.update { it.addTask(NewTask("Later", groupId = it.groups[0].id), Actor.USER, now) }
        val e = assertThrows(GitHubException::class.java) { sync.sync() }
        assertEquals(401, e.status)
        assertNotEquals(null, sync.status().lastError)
    }

    @Test
    fun aTokenGitHubRefusesAtTheFirstSyncIsSaidOnTheSyncScreen() {
        val github = FakeGitHub().apply { token = "right" }
        val files = File(dir, "phone").also { it.mkdirs() }
        val store = LocalBoard.open(files, ZoneOffset.UTC) { now }.store!!
        val settings = SyncSettings(MapValues(), FakeBox())
        val sync = PhoneSync(store, settings, File(files, "sync"), { "Pixel" }, { s -> GistRemote(github, { s.token!! }, { s.gistId }, { s.gistId = it }, s.library) }) { now }
        sync.turnOn(SyncSettings.GIST, "wrong", "Todo Tracker", connected = false)
        assertThrows(GitHubException::class.java) { sync.sync() }
        assertTrue(sync.status().lastError!!.contains("sign-in"))
        assertFalse(settings.joined)
        assertTrue(github.ids.isEmpty())
    }

    // Known, shared with the C# store: an empty tab's folder doesn't sync (only files do), so on the device without it
    // the tab is "dormant" and written last in config.json; the two configs then differ in order for good. To be fixed
    // in C#, Swift and Kotlin together (keep a dormant tab in its place), with a fixture.
    @Ignore("Tab order diverges when a tab's folder is empty on one device (C# store too)")
    @Test
    fun aPhoneWithItsOwnTabEndsWithTheSameTabsAsTheLaptop() {
        val laptop = Laptop()
        laptop.store.update { it.addTask(NewTask("Plan the trip", groupId = it.groups[0].id), Actor.USER, now) }
        laptop.sync()
        val files = File(dir, "phone").also { it.mkdirs() }
        val board = TaskBoard(seedDefaultGroups = false)
        board.groups.add(dev.todotracker.core.TaskGroup(name = "Home", color = "#a855f7"))
        board.addTask(NewTask("Water the plants", groupId = board.groups[0].id), Actor.USER, now)
        LocalBoard.legacyFile(files).writeText(BoardCodec.encode(board))
        val phone = Phone(files)
        phone.sync.turnOn(SyncSettings.FOLDER, null, "Todo Tracker", connected = false)
        repeat(3) {
            phone.sync()
            laptop.sync()
        }
        phone.sync()
        val config = ".todo-tracker/config.json"
        assertEquals(File(dir, "laptop/vault/$config").readText(), File(files, "vault/$config").readText())
    }

    // ---- Review fixes: identity, places, failures ----

    @Test
    fun aCopiedPhoneStartsAsANewDeviceAndJoinsAgain() {
        val laptop = Laptop()
        laptop.sync()
        val files = File(dir, "phone").also { it.mkdirs() }
        val store = LocalBoard.open(files, ZoneOffset.UTC) { now }.store!!
        val values = MapValues()
        fun phoneSync(install: String) = PhoneSync(store, SyncSettings(values, FakeBox()), File(files, "sync"), { "Pixel" }, { FolderRemote(shared) }, installId = install) { now }
        val first = phoneSync("install-1")
        first.turnOn(SyncSettings.FOLDER, null, "Todo Tracker", connected = false)
        first.sync()
        val id = first.deviceId
        assertTrue(first.settings.joined)

        // The same files and settings on another install (a restored or transferred phone): a new device that joins again.
        val copy = phoneSync("install-2")
        assertNotEquals(id, copy.deviceId)
        assertFalse(copy.settings.joined)
        assertTrue(copy.sync().joined)
        assertEquals(2, laptop.sync().let { laptop.engine.state.status.devices.size })
        // What the old identity remembered is kept aside, never deleted.
        assertTrue(files.listFiles()!!.any { it.name.startsWith("sync.copied") })
    }

    @Test
    fun theSameInstallKeepsItsIdentity() {
        val files = File(dir, "phone").also { it.mkdirs() }
        val store = LocalBoard.open(files, ZoneOffset.UTC) { now }.store!!
        val values = MapValues()
        val a = PhoneSync(store, SyncSettings(values, FakeBox()), File(files, "sync"), { "Pixel" }, { FolderRemote(shared) }, installId = "same") { now }
        val id = a.deviceId
        val b = PhoneSync(store, SyncSettings(values, FakeBox()), File(files, "sync"), { "Pixel" }, { FolderRemote(shared) }, installId = "same") { now }
        assertEquals(id, b.deviceId)
    }

    @Test
    fun anotherLibraryStartsWithNothingRememberedButTheSameDevice() {
        val files = File(dir, "phone").also { it.mkdirs() }
        val store = LocalBoard.open(files, ZoneOffset.UTC) { now }.store!!
        val sync = PhoneSync(store, SyncSettings(MapValues(), FakeBox()), File(files, "sync"), { "Pixel" }, { s -> FolderRemote(File(dir, "lib/" + s.library)) }) { now }
        sync.turnOn(SyncSettings.FOLDER, null, "Todo Tracker", connected = false)
        val id = store.update { it.addTask(NewTask("Gone soon", groupId = it.groups[0].id), Actor.USER, now).id }
        sync.sync().also { tick() }
        store.update { it.delete(id, Actor.USER, now) }
        sync.sync().also { tick() }
        val device = sync.deviceId
        fun deleted(library: String) = SyncJson.readSnapshot(File(dir, "lib/$library/devices/$device.json").readText()).deleted
        assertEquals(1, deleted("Todo Tracker").size)

        sync.changeLibrary("Work stuff")
        assertEquals(device, sync.deviceId)
        assertNull(sync.status().lastSync)
        assertTrue(sync.sync().joined)
        // Nothing this phone deleted in the other library is said to be deleted in this one.
        assertEquals(0, deleted("Work stuff").size)
    }

    @Test
    fun theTokenIsSealedBeforeSyncIsOn() {
        val files = File(dir, "phone").also { it.mkdirs() }
        val store = LocalBoard.open(files, ZoneOffset.UTC) { now }.store!!
        val broken = object : SecretBox {
            override fun seal(plain: String): String = throw IllegalStateException("The keystore isn't available.")

            override fun open(sealed: String): String? = null
        }
        val settings = SyncSettings(MapValues(), broken)
        val sync = PhoneSync(store, settings, File(files, "sync"), { "Pixel" }, { FolderRemote(shared) }) { now }
        assertThrows(IllegalStateException::class.java) { sync.turnOn(SyncSettings.GIST, "ghp_x", "Todo Tracker", connected = false) }
        assertFalse(sync.on)
    }
}
