package dev.todotracker.core

import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.Base64
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSession
import javax.net.ssl.X509TrustManager
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

/**
 * The computer's certificate is its own (self-signed, and no host name matches it): it's trusted when, and only when,
 * it's exactly the one whose fingerprint (base64url SHA-256 of its DER) came in the pairing link.
 */
object CertificatePin {
    fun fingerprint(der: ByteArray): String =
        Base64.getUrlEncoder().withoutPadding().encodeToString(MessageDigest.getInstance("SHA-256").digest(der))

    fun matches(certificate: java.security.cert.Certificate?, fingerprint: String): Boolean {
        val der = runCatching { certificate?.encoded }.getOrNull() ?: return false
        // Constant time, though a fingerprint isn't a secret.
        return MessageDigest.isEqual(fingerprint(der).toByteArray(), fingerprint.toByteArray())
    }

    /** Accepts the server's chain only when its first (leaf) certificate is the pinned one. Never a client. */
    class TrustManager(private val fingerprint: String) : X509TrustManager {
        override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) =
            throw CertificateException("Not a server.")

        override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
            if (chain.isNullOrEmpty() || !matches(chain[0], fingerprint)) {
                throw CertificateException("That isn't the computer this phone was connected to.")
            }
        }

        override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
    }

    /** The host name can't match (the computer is reached by address); the pinned certificate is checked again instead. */
    class Hostnames(private val fingerprint: String) : HostnameVerifier {
        override fun verify(hostname: String?, session: SSLSession?): Boolean =
            runCatching { matches(session?.peerCertificates?.firstOrNull(), fingerprint) }.getOrDefault(false)
    }
}

/** What came back from the computer. */
class PhoneResponse(val status: Int, val body: String, val etag: String? = null)

/** Sends one request to one host ("192.168.1.20:5318"); throws IOException when it can't be reached. */
fun interface PhoneTransport {
    fun exchange(host: String, request: PhoneRequest, headers: Map<String, String>): PhoneResponse
}

/** HTTPS only (never plain HTTP), trusting only the pinned certificate. */
class PinnedHttpsTransport(fingerprint: String, private val connectMillis: Int = 5_000, private val readMillis: Int = 15_000) : PhoneTransport {
    private val sockets = SSLContext.getInstance("TLS").apply { init(null, arrayOf(CertificatePin.TrustManager(fingerprint)), null) }.socketFactory
    private val hostnames = CertificatePin.Hostnames(fingerprint)

    override fun exchange(host: String, request: PhoneRequest, headers: Map<String, String>): PhoneResponse {
        val connection = URL("https://$host${request.path}").openConnection() as HttpsURLConnection
        try {
            connection.sslSocketFactory = sockets
            connection.hostnameVerifier = hostnames
            connection.connectTimeout = connectMillis
            connection.readTimeout = readMillis
            connection.useCaches = false
            connection.instanceFollowRedirects = false
            connection.requestMethod = request.method
            for ((name, value) in headers) connection.setRequestProperty(name, value)
            connection.setRequestProperty("Accept", "application/json")
            if (request.body != null) {
                connection.doOutput = true
                connection.setRequestProperty("Content-Type", "application/json")
                connection.outputStream.use { it.write(request.body.toByteArray(Charsets.UTF_8)) }
            }
            val status = connection.responseCode
            val stream = if (status >= 400) connection.errorStream else runCatching { connection.inputStream }.getOrNull()
            val body = stream?.use { String(it.readBytes(), Charsets.UTF_8) }.orEmpty()
            return PhoneResponse(status, body, connection.getHeaderField("ETag"))
        } finally {
            connection.disconnect()
        }
    }
}

/** Why a request to the computer didn't do what was asked. */
sealed class PhoneException(message: String) : Exception(message) {
    /** No host answered (another network, the computer asleep, the app closed). */
    class Unreachable(reason: String) : PhoneException(reason)

    /** The computer doesn't know this phone any more (disconnected there). */
    class Revoked(message: String) : PhoneException(message)

    /** The computer answered no, with its reason (said for the user). */
    class Refused(val status: Int, message: String) : PhoneException(message)
}

/** Answer to a claim: the phone's token, and the computer's name. */
data class Claimed(val token: String, val computer: String)

/**
 * Talks to a connected computer: tries its addresses in turn (the one that answered last first), with the phone's
 * token. One that answers at all is the computer; one that can't be reached, or isn't that computer, is skipped.
 */
class PhoneClient(hosts: List<String>, private val transport: PhoneTransport, private val token: String? = null, preferred: String? = null) {
    private val hosts: List<String> = hosts.distinct()

    /** The address that answered last (tried first next time). */
    @Volatile
    var current: String? = preferred?.takeIf { it in hosts }
        private set

    /** The order the addresses are tried in now. */
    fun order(): List<String> = current?.let { listOf(it) + hosts.filter { h -> h != it } } ?: hosts

    fun exchange(request: PhoneRequest, headers: Map<String, String> = emptyMap()): PhoneResponse {
        val all = if (token != null) headers + ("Authorization" to "Bearer $token") else headers
        var last: IOException? = null
        for (host in order()) {
            try {
                return transport.exchange(host, request, all).also { current = host }
            } catch (e: IOException) {
                last = e
            }
        }
        throw PhoneException.Unreachable(last?.message ?: "no address to try")
    }

    /** Sends the request; the body back when the computer did it. */
    fun send(request: PhoneRequest): String {
        val response = exchange(request)
        check(response)
        return response.body
    }

    fun perform(action: RemoteAction): String = send(PhoneApi.request(action))

    /** The board if it changed since the read that gave [etag] (null: unchanged), the export's text, and its tag now. */
    fun export(etag: String?): Export? {
        val response = exchange(PhoneApi.export, etag?.let { mapOf("If-None-Match" to it) } ?: emptyMap())
        if (response.status == 304) return null
        check(response)
        return Export(response.body, response.etag)
    }

    data class Export(val text: String, val etag: String?)

    /** Trades the pairing code for this phone's token. */
    fun claim(code: String, name: String): Claimed {
        val response = exchange(PhoneApi.claim(code, name))
        if (response.status !in 200..299) throw PhoneException.Refused(response.status, message(response.body, response.status))
        val o = runCatching { Json.parseToJsonElement(response.body).jsonObject }.getOrNull()
        val token = o?.get("token")?.jsonPrimitive?.content?.takeIf { it.startsWith("ttp_") }
            ?: throw PhoneException.Refused(response.status, "The computer didn't send this phone a key. Try connecting again.")
        return Claimed(token, o["computer"]?.jsonPrimitive?.content?.ifBlank { null } ?: "your computer")
    }

    private fun check(response: PhoneResponse) {
        when {
            response.status in 200..299 -> Unit
            response.status == 401 -> throw PhoneException.Revoked(message(response.body, 401))
            else -> throw PhoneException.Refused(response.status, message(response.body, response.status))
        }
    }

    companion object {
        /** The computer's own words for a refusal (problem details, or {"error": …}). */
        fun message(body: String, status: Int): String {
            val o = runCatching { Json.parseToJsonElement(body) as? JsonObject }.getOrNull()
            for (key in listOf("detail", "error", "title")) {
                val text = runCatching { o?.get(key)?.jsonPrimitive?.content }.getOrNull()
                if (!text.isNullOrBlank()) return text
            }
            return when (status) {
                403 -> "The computer said no."
                404 -> "That isn't on the computer any more."
                429 -> "Too many tries. Wait a minute, then try again."
                else -> "The computer couldn't do that (error $status)."
            }
        }
    }
}
