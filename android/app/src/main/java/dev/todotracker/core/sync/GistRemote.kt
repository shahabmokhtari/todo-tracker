package dev.todotracker.core.sync

import dev.todotracker.core.vault.Net
import dev.todotracker.core.vault.NetJson
import java.io.FileNotFoundException
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URI
import java.net.URL
import java.util.Base64
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive

/** One HTTP request; [headers] are sent as they are. */
class HttpRequest(val method: String, val url: String, val headers: Map<String, String>, val body: ByteArray? = null)

/** An HTTP answer; header names are looked up case-insensitively. */
class HttpResponse(val status: Int, val headers: Map<String, String>, val body: ByteArray) {
    fun header(name: String): String? = headers.entries.firstOrNull { it.key.equals(name, ignoreCase = true) }?.value

    val text: String get() = String(body, Charsets.UTF_8)
}

/** Sends requests (HttpURLConnection by default; tests use a fake GitHub). */
fun interface HttpTransport {
    fun send(request: HttpRequest): HttpResponse
}

/** HttpURLConnection, which Android's implementation lets send PATCH (the JVM's needs the method set by hand). */
object UrlConnectionTransport : HttpTransport {
    override fun send(request: HttpRequest): HttpResponse {
        val connection = URL(request.url).openConnection() as HttpURLConnection
        try {
            setMethod(connection, request.method)
            connection.connectTimeout = 30_000
            connection.readTimeout = 60_000
            connection.instanceFollowRedirects = true
            for ((k, v) in request.headers) connection.setRequestProperty(k, v)
            request.body?.let { body ->
                connection.doOutput = true
                connection.setFixedLengthStreamingMode(body.size)
                connection.outputStream.use { it.write(body) }
            }
            val status = connection.responseCode
            val stream = if (status >= 400) connection.errorStream else connection.inputStream
            val body = stream?.use { it.readBytes() } ?: ByteArray(0)
            val headers = connection.headerFields.filterKeys { it != null }.mapValues { it.value.joinToString(",") }
            return HttpResponse(status, headers, body)
        } finally {
            connection.disconnect()
        }
    }

    private fun setMethod(connection: HttpURLConnection, method: String) {
        try {
            connection.requestMethod = method
        } catch (e: java.net.ProtocolException) {
            // Android's HttpURLConnection (OkHttp inside) takes PATCH; a desktop JVM's refuses it.
            throw IOException("This system can't send $method requests; give GistRemote another HttpTransport.", e)
        }
    }
}

/** GitHub answered with an error; [status] is the HTTP status. */
class GitHubException(message: String, val status: Int) : IOException(message)

/**
 * A private GitHub gist as the meeting place (TodoTracker.Core.Sync.GistRemote): one file per device,
 * `device-<id>.json` (its snapshot and every content, base64), next to the marker `todo-tracker-sync.md`. Checks for
 * changes with the gist's ETag (304: nothing new), and only ever changes its own file.
 *
 * [token] gives a GitHub token with the gist scope (from the device flow or pasted; kept in the Android Keystore by
 * the app). [loadId]/[saveId] remember the gist used.
 */
class GistRemote(
    private val http: HttpTransport,
    private val token: () -> String,
    private val loadId: () -> String?,
    private val saveId: (String) -> Unit,
    library: String = "Todo Tracker",
) : SyncRemote {
    companion object {
        const val DESCRIPTION_PREFIX = "Todo Tracker sync: "
        const val MARKER = "todo-tracker-sync.md"
        const val MARKER_TEXT = "# Todo Tracker sync\n\nTodo Tracker keeps each device's tasks here to sync them. Don't edit these files.\n"

        /** GitHub serves at most 10 MB of a gist file; each device's file stays below this. */
        const val MAX_BUNDLE_BYTES = 9 * 1024 * 1024
        private const val API = "https://api.github.com"
    }

    private val description = DESCRIPTION_PREFIX + library
    private val contents = HashMap<String, Map<String, String>>()
    private var looked = false

    override val identity: String get() = "gist:$description"

    override fun forget(device: String) {
        val id = gistId() ?: return
        if (device.length !in 2..64 || !device.all { it in 'a'..'z' || it in 'A'..'Z' || it in '0'..'9' }) return
        ensure(send("PATCH", "/gists/$id", JsonObject(mapOf("files" to JsonObject(mapOf("device-$device.json" to JsonNull))))))
    }

    override fun read(self: String, knownVersion: String?): RemoteSnapshot? {
        val id = gistId() ?: return if (knownVersion == "none") null else RemoteSnapshot(emptyList(), "none", selfMissing = true)
        val etag = knownVersion?.let { split(it).first }?.takeIf { it.isNotEmpty() }
        val response = send("GET", "/gists/$id", null, etag?.let { mapOf("If-None-Match" to it) } ?: emptyMap())
        if (response.status == 304) return null
        ensure(response)
        val gist = NetJson.parse(response.text) as JsonObject
        val found = mutableListOf<DeviceSnapshot>()
        var partial = false
        contents.clear()
        val files = gist["files"] as? JsonObject ?: JsonObject(emptyMap())
        for ((name, file) in files) {
            if (!name.startsWith("device-") || !name.endsWith(".json") || name == "device-$self.json" || file !is JsonObject) continue
            try {
                // Big files are cut short in the gist's JSON: the whole file is at raw_url.
                val raw = NetJson.str(file["raw_url"])
                val text = if (NetJson.bool(file["truncated"]) == true && raw != null) raw(raw) else NetJson.str(file["content"])
                val bundle = text?.let { NetJson.parseOrNull(it) as? JsonObject }
                val snapshotJson = bundle?.entries?.firstOrNull { Net.equalsCi(it.key, "snapshot") }?.value
                val snapshot = snapshotJson?.takeIf { it !is JsonNull }?.let(SyncJson::readSnapshot)
                if (snapshot != null && snapshot.schema == 1 && SyncJson.hasEntries(snapshot) && name == "device-${snapshot.device}.json") {
                    found.add(snapshot)
                    val c = bundle.entries.firstOrNull { Net.equalsCi(it.key, "contents") }?.value as? JsonObject
                    contents[snapshot.device] = c?.mapNotNull { (k, v) -> NetJson.str(v)?.let { k to it } }?.toMap() ?: emptyMap()
                } else {
                    partial = true
                }
            } catch (e: IOException) {
                // Damaged, too big, or from a newer version: skipped, and looked at again next time.
                partial = true
            } catch (e: IllegalStateException) {
                partial = true
            }
        }
        return RemoteSnapshot(found, join(response.header("ETag"), revision(gist)), partial, selfMissing = files["device-$self.json"] == null || files["device-$self.json"] is JsonNull)
    }

    override fun readContent(device: DeviceSnapshot, entry: SyncEntry): ByteArray {
        val base64 = contents[device.device]?.get(entry.hash) ?: throw FileNotFoundException("${entry.path} from ${device.name} isn't in the gist.")
        return try {
            Base64.getDecoder().decode(base64)
        } catch (e: IllegalArgumentException) {
            throw IOException("${entry.path} from ${device.name} is damaged.")
        }
    }

    override fun publish(snapshot: DeviceSnapshot, read: (SyncEntry) -> ByteArray, readVersion: String?): String? {
        val bundleContents = LinkedHashMap<String, JsonElement>()
        for (e in snapshot.entries) if (!bundleContents.containsKey(e.hash)) bundleContents[e.hash] = JsonPrimitive(Base64.getEncoder().encodeToString(read(e)))
        val bundle = SyncJson.write(JsonObject(linkedMapOf("snapshot" to SyncJson.snapshot(snapshot), "contents" to JsonObject(bundleContents))))
        val size = bundle.toByteArray(Charsets.UTF_8).size
        if (size > MAX_BUNDLE_BYTES) {
            throw IOException("Your tasks are too big for a gist (${size / (1024 * 1024)} MB with attachments; GitHub serves up to ${MAX_BUNDLE_BYTES / (1024 * 1024)} MB per file). Sync through OneDrive or iCloud Drive instead, or move big attachments out of the tasks folder.")
        }
        val files = linkedMapOf<String, JsonElement>("device-${snapshot.device}.json" to JsonObject(mapOf("content" to JsonPrimitive(bundle))))
        val id = gistId()
        val response = if (id == null) {
            files[MARKER] = JsonObject(mapOf("content" to JsonPrimitive(MARKER_TEXT)))
            send("POST", "/gists", JsonObject(linkedMapOf("description" to JsonPrimitive(description), "public" to JsonPrimitive(false), "files" to JsonObject(files))))
        } else {
            send("PATCH", "/gists/$id", JsonObject(mapOf("files" to JsonObject(files))))
        }
        ensure(response)
        val gist = NetJson.parse(response.text) as JsonObject
        if (id == null) {
            NetJson.str(gist["id"])?.let {
                saveId(it)
                // Another device may have made one at the same moment: look again next time.
                looked = false
            }
        }
        // Our write is the only change since we read the gist when the version before it is the one we read.
        val history = gist["history"] as? JsonArray
        val before = if (history != null && history.size > 1) NetJson.str((history[1] as? JsonObject)?.get("version")) else null
        return if (readVersion != null && before != null && before == split(readVersion).second) join(response.header("ETag"), revision(gist)) else null
    }

    private fun join(etag: String?, revision: String?) = "${etag ?: ""}|${revision ?: ""}"

    private fun split(version: String): Pair<String, String> {
        val bar = version.lastIndexOf('|')
        return if (bar < 0) version to "" else version.substring(0, bar) to version.substring(bar + 1)
    }

    private fun revision(gist: JsonObject): String? = (gist["history"] as? JsonArray)?.firstOrNull()?.let { NetJson.str((it as? JsonObject)?.get("version")) }

    private fun headers(): Map<String, String> = linkedMapOf(
        "Authorization" to "Bearer ${token()}",
        "Accept" to "application/vnd.github+json",
        "User-Agent" to "TodoTracker-Sync",
        "X-GitHub-Api-Version" to "2022-11-28",
    )

    private fun send(method: String, path: String, body: JsonElement?, extra: Map<String, String> = emptyMap()): HttpResponse {
        val headers = LinkedHashMap(headers())
        headers.putAll(extra)
        if (body != null) headers["Content-Type"] = "application/json; charset=utf-8"
        return http.send(HttpRequest(method, if (path.startsWith("https://")) path else API + path, headers, body?.let { SyncJson.write(it).toByteArray(Charsets.UTF_8) }))
    }

    private fun ensure(response: HttpResponse) {
        if (response.status in 200..299) return
        val detail = response.text
        val message = when (response.status) {
            401 -> "GitHub didn't accept the sign-in (sign in again, with the gist scope)."
            404 -> "The sync gist wasn't found (was it deleted?)."
            403, 429 -> "GitHub is limiting requests right now; sync tries again later."
            else -> "GitHub answered ${response.status}: ${detail.take(200)}"
        }
        throw GitHubException(message, response.status)
    }

    /**
     * The gist used before, or one made earlier (on another device, or before a reinstall) found by its description and
     * marker file. Looked for once per start (and after making one): if two devices each made one at the same moment,
     * every device moves to the oldest, so they meet again.
     */
    private fun gistId(): String? {
        val known = loadId()?.takeIf { it.isNotEmpty() }
        if (known != null && looked) return known
        val found = mutableListOf<Pair<String, String>>()
        for (page in 1..5) {
            val response = send("GET", "/gists?per_page=100&page=$page", null)
            ensure(response)
            val gists = NetJson.parse(response.text) as JsonArray
            for (gist in gists) {
                val o = gist as? JsonObject ?: continue
                val files = o["files"] as? JsonObject
                val id = NetJson.str(o["id"])
                if (Net.equalsCi(NetJson.str(o["description"]), description) && files?.get(MARKER).let { it != null && it !is JsonNull } && id != null) {
                    found.add(id to (NetJson.str(o["created_at"]) ?: ""))
                }
            }
            if (gists.size < 100) break
        }
        looked = true
        if (found.isEmpty()) return known
        val oldest = found.sortedWith(compareBy<Pair<String, String>, String>(Net.ordinal) { it.second }.thenBy(Net.ordinal) { it.first }).first().first
        if (oldest != known) saveId(oldest)
        return oldest
    }

    private fun raw(url: String): String {
        // The token only ever goes to GitHub.
        val uri = URI(url)
        val host = uri.host ?: ""
        val github = uri.scheme == "https" && (host == "gist.githubusercontent.com" || host.endsWith(".githubusercontent.com", ignoreCase = true) || host == "api.github.com")
        val response = http.send(HttpRequest("GET", url, if (github) headers() else mapOf("User-Agent" to "TodoTracker-Sync")))
        ensure(response)
        if (response.body.size > MAX_BUNDLE_BYTES * 2L) throw IOException("A device's sync file is too big.")
        return response.text
    }
}
