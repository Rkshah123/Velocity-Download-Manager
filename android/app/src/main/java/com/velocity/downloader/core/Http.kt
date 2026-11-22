package com.velocity.downloader.core

import java.io.IOException
import java.net.CookieHandler
import java.net.CookieManager
import java.net.HttpURLConnection
import java.net.URI
import java.net.URL

/** One opened response; the caller owns the stream and must disconnect the connection. */
data class HttpResponse(
    val connection: HttpURLConnection,
    val code: Int,
    val finalUrl: String,
    val totalBytes: Long,
    val etag: String?,
    val lastModified: String?,
    val contentType: String?,
    val disposition: String?,
    val acceptRanges: Boolean
)

/**
 * HTTP plumbing shared by analysis and transfer. Redirects are followed here rather than by the connection, which
 * only follows same-protocol redirects and does not know 308; cookies are read from the browser's cookie store and
 * from the JVM cookie manager, and every request asks for an identity encoding so a Range offset always means real
 * file bytes.
 */
object Http {

    const val USER_AGENT = "Velocity/1.0 (Android; download manager)"

    /** Sent once as a fallback: some CDNs answer 403 to a client that does not look like a browser. */
    private const val BROWSER_USER_AGENT =
        "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36"

    private const val MAX_REDIRECTS = 10

    fun installCookies() {
        if (CookieHandler.getDefault() == null) CookieHandler.setDefault(CookieManager())
    }

    fun describe(code: Int): String = when (code) {
        400 -> "The server rejected the request (HTTP 400)."
        401 -> "The server requires a sign-in (HTTP 401). Open the page in your browser, sign in, then paste the link again."
        403 -> "The server refused the request (HTTP 403). The link may be tied to a browser session, or it may have expired."
        404 -> "The server has no file at that address (HTTP 404). The link may have expired."
        405 -> "The server does not allow this request method (HTTP 405)."
        407 -> "A proxy in front of this network requires a sign-in (HTTP 407)."
        410 -> "The link has expired (HTTP 410). Get a fresh link and try again."
        416 -> "The server could not continue from the saved position."
        429 -> "The server is limiting requests (HTTP 429). Try again in a moment."
        451 -> "The server refuses to serve this file in your region (HTTP 451)."
        in 500..599 -> "The server had a problem (HTTP $code). Try again in a moment."
        in 300..399 -> "The server redirected the request but did not say where to (HTTP $code)."
        else -> "The server answered HTTP $code."
    }

    fun open(
        url: String,
        method: String,
        headers: Map<String, String> = emptyMap(),
        rangeStart: Long? = null,
        rangeEnd: Long? = null,
        connectTimeoutMs: Int = 20_000,
        readTimeoutMs: Int = 30_000
    ): HttpResponse {
        var current = url
        var hops = 0
        var browserAgent = false
        val agentProvided = headers.keys.any { it.equals("User-Agent", ignoreCase = true) }

        while (true) {
            val agent = if (browserAgent) BROWSER_USER_AGENT else headers["User-Agent"] ?: USER_AGENT
            val response = openOnce(current, method, headers, rangeStart, rangeEnd, agent, connectTimeoutMs, readTimeoutMs)
            val code = response.code

            if (code in 300..399) {
                val location = response.connection.getHeaderField("Location")
                response.connection.disconnect()
                if (location.isNullOrBlank()) throw IOException(describe(code))
                if (++hops > MAX_REDIRECTS) {
                    throw IOException("The link redirected more than $MAX_REDIRECTS times, so it was stopped.")
                }
                current = resolve(current, location)
                continue
            }

            // A link that is only refused for our client is retried once as a browser before the refusal is reported.
            if (code == 403 && !browserAgent && !agentProvided) {
                browserAgent = true
                response.connection.disconnect()
                continue
            }

            return response
        }
    }

    private fun openOnce(
        url: String,
        method: String,
        headers: Map<String, String>,
        rangeStart: Long?,
        rangeEnd: Long?,
        agent: String,
        connectTimeoutMs: Int,
        readTimeoutMs: Int
    ): HttpResponse {
        val connection = URL(url).openConnection() as HttpURLConnection
        connection.instanceFollowRedirects = false
        connection.requestMethod = method
        connection.connectTimeout = connectTimeoutMs
        connection.readTimeout = readTimeoutMs
        connection.useCaches = false
        connection.setRequestProperty("User-Agent", agent)
        connection.setRequestProperty("Accept", "*/*")
        // A Range offset is only meaningful byte-for-byte, so compression must stay out of the way.
        connection.setRequestProperty("Accept-Encoding", "identity")
        for ((name, value) in headers) {
            if (name.equals("User-Agent", ignoreCase = true)) continue
            connection.setRequestProperty(name, value)
        }
        browserCookie(url)?.let { connection.setRequestProperty("Cookie", it) }
        if (rangeStart != null) {
            connection.setRequestProperty("Range", "bytes=$rangeStart-" + (rangeEnd?.toString() ?: ""))
        }

        val code = connection.responseCode
        val total = totalFrom(connection, code)
        return HttpResponse(
            connection = connection,
            code = code,
            finalUrl = connection.url?.toString() ?: url,
            totalBytes = total,
            etag = connection.getHeaderField("ETag"),
            lastModified = connection.getHeaderField("Last-Modified"),
            contentType = connection.contentType,
            disposition = connection.getHeaderField("Content-Disposition"),
            acceptRanges = connection.getHeaderField("Accept-Ranges")?.contains("bytes", true) == true
        )
    }

    /** Resolves a `Location` that may be absolute, protocol-relative (`//host/path`) or a plain relative path. */
    private fun resolve(base: String, location: String): String {
        val target = Links.percentEncode(location.trim())
        return try {
            URI(base).resolve(target).toString()
        } catch (exception: Exception) {
            try {
                URL(URL(base), target).toString()
            } catch (second: Exception) {
                throw IOException("The link redirected to an address that could not be read: $location")
            }
        }
    }

    private fun totalFrom(connection: HttpURLConnection, code: Int): Long {
        val contentRange = connection.getHeaderField("Content-Range")
        if (contentRange != null) {
            val slash = contentRange.lastIndexOf('/')
            if (slash >= 0) {
                val value = contentRange.substring(slash + 1).trim()
                if (value != "*") value.toLongOrNull()?.let { return it }
            }
        }
        if (code == 206) {
            // A 206 without a usable total: the length of this slice is not the file's length.
            return -1
        }
        val length = connection.getHeaderField("Content-Length")?.toLongOrNull() ?: return -1
        return if (length > 0) length else -1
    }

    private fun browserCookie(url: String): String? = try {
        android.webkit.CookieManager.getInstance().getCookie(url)?.takeIf { it.isNotBlank() }
    } catch (exception: Throwable) {
        // The browser cookie store is only reachable on a device; its absence never blocks a download.
        null
    }
}
