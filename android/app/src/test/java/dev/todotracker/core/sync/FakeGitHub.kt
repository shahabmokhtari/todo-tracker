package dev.todotracker.core.sync

import dev.todotracker.core.vault.NetJson
import java.net.URI
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive

/** Just enough of GitHub's gist API (as the C# tests' FakeGitHub): list, create, read with ETags, patch, history, truncation. */
class FakeGitHub : HttpTransport {
    private class Gist(val description: String) {
        val files = LinkedHashMap<String, String>()
        val history = mutableListOf<String>()
    }

    private val gists = LinkedHashMap<String, Gist>()
    var token = "token"
    var fullReads = 0
    var notModified = 0
    var truncateAt = Int.MAX_VALUE
    val requests = mutableListOf<HttpRequest>()

    fun files(id: String): Map<String, String> = LinkedHashMap(gists[id]!!.files)

    val ids: List<String> get() = gists.keys.toList()

    private fun etag(id: String) = "\"${gists[id]!!.history.firstOrNull() ?: "v0"}\""

    private fun ok(body: JsonElement, etag: String?) = HttpResponse(200, if (etag != null) mapOf("ETag" to etag) else emptyMap(), NetJson.write(body).toByteArray())

    private fun gistJson(id: String): JsonObject {
        val g = gists[id]!!
        val files = LinkedHashMap<String, JsonElement>()
        for ((name, content) in g.files) {
            val truncated = content.length > truncateAt
            val o = linkedMapOf<String, JsonElement>("filename" to JsonPrimitive(name), "content" to JsonPrimitive(if (truncated) content.substring(0, truncateAt) else content))
            if (truncated) {
                o["truncated"] = JsonPrimitive(true)
                o["raw_url"] = JsonPrimitive("https://gist.githubusercontent.com/raw/$id/$name")
            }
            files[name] = JsonObject(o)
        }
        return JsonObject(
            linkedMapOf(
                "id" to JsonPrimitive(id), "description" to JsonPrimitive(g.description), "created_at" to JsonPrimitive("2026-01-0${id.drop(1)}T00:00:00Z"),
                "files" to JsonObject(files), "history" to JsonArray(g.history.map { JsonObject(mapOf("version" to JsonPrimitive(it))) }),
            ),
        )
    }

    private fun apply(id: String, body: JsonObject) {
        val g = gists[id]!!
        for ((name, file) in body["files"] as JsonObject) {
            if (file is JsonNull) g.files.remove(name) else g.files[name] = NetJson.str((file as JsonObject)["content"])!!
        }
        g.history.add(0, "v${g.history.size + 1}")
    }

    override fun send(request: HttpRequest): HttpResponse = synchronized(this) {
        requests.add(request)
        val uri = URI(request.url)
        val path = uri.path
        if (uri.host == "gist.githubusercontent.com") {
            val parts = path.split('/')
            return HttpResponse(200, emptyMap(), gists[parts[2]]!!.files[parts[3]]!!.toByteArray())
        }
        if (request.headers["Authorization"] != "Bearer $token") return HttpResponse(401, emptyMap(), "{}".toByteArray())
        val body = request.body?.let { NetJson.parse(String(it)) as JsonObject }
        if (request.method == "GET" && path == "/gists") {
            val page = uri.query?.split('&')?.firstOrNull { it.startsWith("page=") }?.substringAfter('=')?.toInt() ?: 1
            val list = if (page == 1) gists.keys.map { gistJson(it) } else emptyList()
            return ok(JsonArray(list), null)
        }
        if (request.method == "POST" && path == "/gists") {
            val id = "g${gists.size + 1}"
            gists[id] = Gist(NetJson.str(body!!["description"])!!)
            apply(id, body)
            return ok(gistJson(id), etag(id))
        }
        val id = path.removePrefix("/gists/")
        if (!gists.containsKey(id)) return HttpResponse(404, emptyMap(), "{}".toByteArray())
        if (request.method == "GET") {
            if (request.headers["If-None-Match"] == etag(id)) {
                notModified++
                return HttpResponse(304, emptyMap(), ByteArray(0))
            }
            fullReads++
            return ok(gistJson(id), etag(id))
        }
        apply(id, body!!)
        return ok(gistJson(id), etag(id))
    }
}
