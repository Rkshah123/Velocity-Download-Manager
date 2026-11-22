package com.velocity.downloader.core

import java.io.IOException

data class RemoteInfo(
    val url: String,
    val finalUrl: String,
    val fileName: String,
    val totalBytes: Long,
    val contentType: String?,
    val etag: String?,
    val lastModified: String?,
    val supportsRange: Boolean
)

/**
 * Works out what a link actually points at. A HEAD is tried first because it moves no file bytes; a server that
 * refuses HEAD, hides its length or answers 405 is asked again with `Range: bytes=0-0`, which both proves that the
 * resource is readable and reports whether ranges are honoured. Nothing here rejects a file for its type.
 */
object UrlAnalyzer {

    fun analyze(rawUrl: String, headers: Map<String, String> = emptyMap()): RemoteInfo {
        val url = rawUrl.trim()
        var failure: String? = null

        try {
            // Analysis has its own short patience: a server that does not answer a probe quickly is asked directly.
            // A phone network can be slow to answer; the probe waits long enough not to call a working link dead.
            val head = Http.open(url, "HEAD", headers, connectTimeoutMs = 20_000, readTimeoutMs = 20_000)
            try {
                if (head.code in 200..299) {
                    val name = pickName(head, url)
                    return RemoteInfo(
                        url = url,
                        finalUrl = head.finalUrl,
                        fileName = name,
                        totalBytes = head.totalBytes,
                        contentType = head.contentType,
                        etag = head.etag,
                        lastModified = head.lastModified,
                        supportsRange = head.acceptRanges
                    )
                }
                failure = Http.describe(head.code)
            } finally {
                head.connection.disconnect()
            }
        } catch (exception: IOException) {
            failure = "The link could not be reached: ${exception.message}"
        }

        // HEAD was not enough; a one-byte ranged GET settles readability, size and range support in one request.
        var response = try {
            Http.open(url, "GET", headers, 0, 0, connectTimeoutMs = 20_000, readTimeoutMs = 25_000)
        } catch (exception: IOException) {
            throw IOException(failure ?: "The link could not be reached: ${exception.message}")
        }

        // A server that dislikes the probe's range still serves the file to a plain request: ask again without it.
        if (response.code == 416 || response.code == 405 || response.code == 400) {
            response.connection.disconnect()
            response = try {
                Http.open(url, "GET", headers, connectTimeoutMs = 20_000, readTimeoutMs = 25_000)
            } catch (exception: IOException) {
                throw IOException(failure ?: "The link could not be reached: ${exception.message}")
            }
        }

        try {
            if (response.code !in 200..299) {
                throw IOException(failure ?: Http.describe(response.code))
            }
            val supportsRange = response.code == 206 || response.acceptRanges
            return RemoteInfo(
                url = url,
                finalUrl = response.finalUrl,
                fileName = pickName(response, url),
                totalBytes = response.totalBytes,
                contentType = response.contentType,
                etag = response.etag,
                lastModified = response.lastModified,
                supportsRange = supportsRange
            )
        } finally {
            response.connection.disconnect()
        }
    }

    private fun pickName(response: HttpResponse, originalUrl: String): String {
        val name = FileNames.fromDisposition(response.disposition)
            ?: FileNames.fromUrl(response.finalUrl)
            ?: FileNames.fromUrl(originalUrl)
            ?: "download-${System.currentTimeMillis()}"
        return FileNames.ensureExtension(name, response.contentType)
    }
}
