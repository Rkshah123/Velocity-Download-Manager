package com.velocity.downloader.core

import org.json.JSONArray
import org.json.JSONObject

enum class DownloadStatus { QUEUED, ANALYZING, DOWNLOADING, PAUSED, COMPLETED, FAILED, CANCELLED }

/** One connection's slice of a segmented transfer: the bytes it owns and how many it has already written. */
data class Segment(val start: Long, val end: Long, val written: Long = 0) {
    val length: Long get() = end - start + 1
}

/**
 * Everything the app knows about one download. It is immutable so a progress report can never race with the queue,
 * and it is serialized to JSON because the service must be able to restore the list after Android kills the process.
 */
data class DownloadRecord(
    val id: String,
    val url: String,
    val fileName: String,
    val finalUrl: String? = null,
    val dirUri: String? = null,
    val totalBytes: Long = -1,
    val downloadedBytes: Long = 0,
    val contentType: String? = null,
    val etag: String? = null,
    val lastModified: String? = null,
    val supportsRange: Boolean = false,
    val connections: Int = 1,
    val status: DownloadStatus = DownloadStatus.QUEUED,
    val error: String? = null,
    val createdAt: Long = System.currentTimeMillis(),
    val completedAt: Long? = null,
    val speedBytesPerSecond: Long = 0,
    val segments: List<Segment> = emptyList(),
    val savedUri: String? = null
) {
    val isActive: Boolean
        get() = status == DownloadStatus.QUEUED || status == DownloadStatus.ANALYZING || status == DownloadStatus.DOWNLOADING

    val isFinished: Boolean
        get() = status == DownloadStatus.COMPLETED || status == DownloadStatus.FAILED || status == DownloadStatus.CANCELLED

    val progress: Int
        get() = if (totalBytes > 0) (((downloadedBytes.coerceAtMost(totalBytes)) * 100) / totalBytes).toInt().coerceIn(0, 100) else 0

    fun toJson(): JSONObject = JSONObject().apply {
        put("id", id)
        put("url", url)
        put("fileName", fileName)
        put("finalUrl", finalUrl ?: JSONObject.NULL)
        put("dirUri", dirUri ?: JSONObject.NULL)
        put("totalBytes", totalBytes)
        put("downloadedBytes", downloadedBytes)
        put("contentType", contentType ?: JSONObject.NULL)
        put("etag", etag ?: JSONObject.NULL)
        put("lastModified", lastModified ?: JSONObject.NULL)
        put("supportsRange", supportsRange)
        put("connections", connections)
        put("status", status.name)
        put("error", error ?: JSONObject.NULL)
        put("createdAt", createdAt)
        put("completedAt", completedAt ?: JSONObject.NULL)
        put("speedBytesPerSecond", speedBytesPerSecond)
        put("savedUri", savedUri ?: JSONObject.NULL)
        put("segments", JSONArray().apply {
            segments.forEach { segment ->
                put(JSONObject().put("start", segment.start).put("end", segment.end).put("written", segment.written))
            }
        })
    }

    companion object {
        fun fromJson(json: JSONObject): DownloadRecord? {
            val id = json.optString("id").takeIf { it.isNotBlank() } ?: return null
            val url = json.optString("url").takeIf { it.isNotBlank() } ?: return null
            val segments = mutableListOf<Segment>()
            json.optJSONArray("segments")?.let { array ->
                for (index in 0 until array.length()) {
                    val item = array.optJSONObject(index) ?: continue
                    segments.add(Segment(item.optLong("start"), item.optLong("end"), item.optLong("written")))
                }
            }
            return DownloadRecord(
                id = id,
                url = url,
                fileName = json.optString("fileName").ifBlank { FileNames.fromUrl(url) ?: "download" },
                finalUrl = json.stringOrNull("finalUrl"),
                dirUri = json.stringOrNull("dirUri"),
                totalBytes = json.optLong("totalBytes", -1),
                downloadedBytes = json.optLong("downloadedBytes", 0),
                contentType = json.stringOrNull("contentType"),
                etag = json.stringOrNull("etag"),
                lastModified = json.stringOrNull("lastModified"),
                supportsRange = json.optBoolean("supportsRange", false),
                connections = json.optInt("connections", 1),
                status = runCatching { DownloadStatus.valueOf(json.optString("status")) }.getOrDefault(DownloadStatus.QUEUED),
                error = json.stringOrNull("error"),
                createdAt = json.optLong("createdAt", System.currentTimeMillis()),
                completedAt = if (json.isNull("completedAt")) null else json.optLong("completedAt"),
                speedBytesPerSecond = json.optLong("speedBytesPerSecond", 0),
                segments = segments,
                savedUri = json.stringOrNull("savedUri")
            )
        }

        private fun JSONObject.stringOrNull(key: String): String? =
            if (!has(key) || isNull(key)) null else optString(key).takeIf { it.isNotBlank() }
    }
}
