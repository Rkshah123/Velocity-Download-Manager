package com.velocity.downloader.core

import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.io.RandomAccessFile
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLongArray
import java.util.concurrent.atomic.AtomicReference
import kotlin.math.min
import org.json.JSONArray
import org.json.JSONObject

/**
 * Moves the bytes of one download into its `.part` file. It is deliberately independent of Android: the service
 * gives it a file, request headers and two predicates, and it reports progress and a final outcome.
 *
 * A known, range-capable file is fetched over several connections that each own a byte range; each connection can be
 * retried on its own without disturbing the others. A server that answers a ranged request with `200 OK` is not
 * broken — it simply cannot do ranges — so the engine falls back to one connection instead of failing.
 */
class DownloadEngine(
    private val partFile: File,
    private val metaFile: File? = null,
    private val headers: Map<String, String> = emptyMap(),
    private val connections: Int = 4,
    private val maxRetries: Int = 6
) {

    private var lastCheckpointAt = 0L

    data class Outcome(
        val completed: Boolean = false,
        val paused: Boolean = false,
        val totalBytes: Long = -1,
        val downloadedBytes: Long = 0,
        val error: String? = null,
        val segments: List<Segment> = emptyList(),
        val supportsRange: Boolean = false,
        val etag: String? = null,
        val lastModified: String? = null,
        val contentType: String? = null
    )

    private class RangeUnsupported : IOException()

    fun run(
        url: String,
        expectedTotal: Long,
        expectedEtag: String? = null,
        savedSegments: List<Segment> = emptyList(),
        isPaused: () -> Boolean,
        emit: (downloaded: Long, usedConnections: Int) -> Unit
    ): Outcome {
        if (expectedTotal > MIN_SEGMENTED && connections > 1) {
            val segmented = try {
                runSegmented(url, expectedTotal, expectedEtag, savedSegments, isPaused, emit)
            } catch (unsupported: RangeUnsupported) {
                null
            }
            if (segmented != null) return segmented
            // The server will not honour ranges: one stream from the beginning is the only correct transfer left.
            partFile.delete()
        }
        return runSingle(url, isPaused, emit)
    }

    /* ─────────────────────────── single connection ─────────────────────────── */

    private fun runSingle(url: String, isPaused: () -> Boolean, emit: (Long, Int) -> Unit): Outcome {
        var bytes = if (partFile.exists()) partFile.length() else 0L
        var total = -1L
        var etag: String? = null
        var lastModified: String? = null
        var contentType: String? = null
        var attempt = 0
        var lastEmit = 0L

        while (true) {
            if (isPaused()) return pausedOutcome(total, bytes, etag, lastModified, contentType)

            val response = try {
                Http.open(url, "GET", headers, if (bytes > 0) bytes else null, null)
            } catch (exception: IOException) {
                if (++attempt > maxRetries) {
                    return Outcome(totalBytes = total, downloadedBytes = bytes, error = "The connection failed: ${exception.message}")
                }
                sleep(attempt)
                continue
            }

            val code = response.code
            if (code == 416) {
                response.connection.disconnect()
                if (bytes > 0 && (total <= 0 || bytes >= total)) {
                    return Outcome(completed = true, totalBytes = if (total > 0) total else bytes, downloadedBytes = bytes)
                }
                bytes = 0
                partFile.delete()
                continue
            }
            if (code == 200 && bytes > 0) {
                // Ranges are not supported after all, so appending would corrupt the file: start it clean.
                bytes = 0
                partFile.delete()
            }
            if (code !in 200..299) {
                response.connection.disconnect()
                val message = Http.describe(code)
                if (code in 400..499 && code != 408 && code != 429) {
                    return Outcome(totalBytes = total, downloadedBytes = bytes, error = message)
                }
                if (++attempt > maxRetries) return Outcome(totalBytes = total, downloadedBytes = bytes, error = message)
                sleep(attempt)
                continue
            }

            total = if (response.totalBytes > 0) response.totalBytes else total
            etag = response.etag ?: etag
            lastModified = response.lastModified ?: lastModified
            contentType = response.contentType ?: contentType

            try {
                FileOutputStream(partFile, bytes > 0).use { output ->
                    response.connection.inputStream.use { input ->
                        val buffer = ByteArray(BUFFER)
                        while (true) {
                            if (isPaused()) {
                                output.flush()
                                return pausedOutcome(total, bytes, etag, lastModified, contentType)
                            }
                            val read = input.read(buffer)
                            if (read < 0) break
                            output.write(buffer, 0, read)
                            BandwidthLimiter.acquire(read)
                            bytes += read
                            val now = System.currentTimeMillis()
                            if (now - lastEmit >= 200) {
                                lastEmit = now
                                emit(bytes, 1)
                            }
                        }
                        output.flush()
                    }
                }
            } catch (exception: IOException) {
                response.connection.disconnect()
                if (isPaused()) return pausedOutcome(total, bytes, etag, lastModified, contentType)
                if (++attempt > maxRetries) {
                    return Outcome(totalBytes = total, downloadedBytes = bytes, error = "The transfer stopped: ${exception.message}")
                }
                sleep(attempt)
                continue
            } finally {
                response.connection.disconnect()
            }

            emit(bytes, 1)
            if (total > 0 && bytes < total) {
                if (++attempt > maxRetries) {
                    return Outcome(totalBytes = total, downloadedBytes = bytes, error = "The connection ended ${total - bytes} bytes early.")
                }
                sleep(attempt)
                continue
            }
            return Outcome(
                completed = true,
                totalBytes = if (total > 0) total else bytes,
                downloadedBytes = bytes,
                supportsRange = false,
                etag = etag,
                lastModified = lastModified,
                contentType = contentType
            )
        }
    }

    /* ─────────────────────────── several connections ─────────────────────────── */

    private fun runSegmented(
        url: String,
        total: Long,
        expectedEtag: String?,
        savedSegments: List<Segment>,
        isPaused: () -> Boolean,
        emit: (Long, Int) -> Unit
    ): Outcome {
        // A transfer the process lost mid-flight leaves its per-connection offsets in the checkpoint beside the
        // partial file, so only the bytes still missing are fetched again.
        val resumed = if (savedSegments.isNotEmpty()) savedSegments else readCheckpoint(total)
        val plan = if (resumed.isNotEmpty() && resumed.sumOf { it.length } == total) {
            resumed
        } else {
            split(total, connections)
        }

        if (!partFile.exists() || partFile.length() != total) {
            RandomAccessFile(partFile, "rw").use { it.setLength(total) }
        }

        val counters = AtomicLongArray(plan.size)
        plan.forEachIndexed { index, segment -> counters.set(index, segment.written) }
        val error = AtomicReference<String?>(null)
        val unsupported = AtomicBoolean(false)
        val changed = AtomicBoolean(false)
        val latch = CountDownLatch(plan.size)
        val pool = Executors.newFixedThreadPool(plan.size)

        fun worker(index: Int) {
            val segment = plan[index]
            var base = segment.written
            var attempt = 0

            while (true) {
                if (isPaused() || error.get() != null || unsupported.get() || changed.get()) return
                val position = segment.start + base
                if (position > segment.end) return

                var file: RandomAccessFile? = null
                var connection: HttpResponse? = null
                try {
                    val response = Http.open(url, "GET", headers, position, segment.end)
                    connection = response
                    if (response.code == 200) {
                        unsupported.set(true)
                        return
                    }
                    if (response.code !in 200..299) {
                        val message = Http.describe(response.code)
                        if (response.code in 400..499 && response.code != 408 && response.code != 429) {
                            error.compareAndSet(null, message)
                            return
                        }
                        if (++attempt > maxRetries) {
                            error.compareAndSet(null, message)
                            return
                        }
                        sleep(attempt)
                        continue
                    }
                    if (response.totalBytes > 0 && response.totalBytes != total) {
                        changed.set(true)
                        return
                    }
                    if (expectedEtag != null && response.etag != null && response.etag != expectedEtag) {
                        changed.set(true)
                        return
                    }

                    file = RandomAccessFile(partFile, "rw")
                    file.seek(position)
                    response.connection.inputStream.use { input ->
                        val buffer = ByteArray(BUFFER)
                        while (true) {
                            if (isPaused()) return
                            val read = input.read(buffer)
                            if (read < 0) break
                            val room = (segment.end - (segment.start + base) + 1).coerceAtMost(read.toLong()).toInt()
                            if (room <= 0) break
                            file.write(buffer, 0, room)
                            BandwidthLimiter.acquire(room)
                            base += room
                            counters.set(index, base)
                        }
                    }

                    if (base >= segment.length) return
                    if (++attempt > maxRetries) {
                        error.compareAndSet(null, "The connection ended before its part of the file was complete.")
                        return
                    }
                    sleep(attempt)
                } catch (exception: IOException) {
                    if (isPaused()) return
                    if (++attempt > maxRetries) {
                        error.compareAndSet(null, "The transfer stopped: ${exception.message}")
                        return
                    }
                    sleep(attempt)
                } finally {
                    runCatching { file?.close() }
                    connection?.connection?.disconnect()
                }
            }
        }

        for (index in plan.indices) {
            pool.execute {
                try {
                    worker(index)
                } finally {
                    latch.countDown()
                }
            }
        }

        while (!latch.await(250, TimeUnit.MILLISECONDS)) {
            emit(counters.total(), plan.size)
            writeCheckpoint(total, plan, counters, force = false)
        }
        pool.shutdown()
        emit(counters.total(), plan.size)
        if (error.get() == null && !unsupported.get() && !isPaused()) writeCheckpoint(total, plan, counters, force = true)

        val written = counters.total()
        val segments = plan.mapIndexed { index, segment -> segment.copy(written = counters.get(index)) }

        if (unsupported.get()) throw RangeUnsupported()
        if (changed.get()) {
            return Outcome(
                totalBytes = total,
                downloadedBytes = written,
                error = "The file or its address changed on the server, so the saved part cannot be continued safely. Tap Retry to start this file again.",
                segments = segments,
                supportsRange = true
            )
        }
        error.get()?.let { message ->
            return Outcome(totalBytes = total, downloadedBytes = written, error = message, segments = segments, supportsRange = true)
        }
        if (isPaused()) {
            return Outcome(paused = true, totalBytes = total, downloadedBytes = written, segments = segments, supportsRange = true)
        }
        if (written >= total) {
            return Outcome(
                completed = true,
                totalBytes = total,
                downloadedBytes = written,
                segments = segments,
                supportsRange = true,
                etag = expectedEtag
            )
        }
        return Outcome(
            totalBytes = total,
            downloadedBytes = written,
            error = "The transfer stopped ${total - written} bytes before the end.",
            segments = segments,
            supportsRange = true
        )
    }

    private fun writeCheckpoint(total: Long, plan: List<Segment>, counters: AtomicLongArray, force: Boolean) {
        val file = metaFile ?: return
        val now = System.currentTimeMillis()
        if (!force && now - lastCheckpointAt < 500) return
        lastCheckpointAt = now
        val segments = JSONArray()
        plan.forEachIndexed { index, segment ->
            segments.put(JSONObject().put("start", segment.start).put("end", segment.end).put("written", counters.get(index)))
        }
        val json = JSONObject().put("total", total).put("segments", segments)
        val temporary = File(file.path + ".tmp")
        runCatching {
            temporary.writeText(json.toString())
            if (file.exists()) file.delete()
            temporary.renameTo(file)
        }
    }

    private fun readCheckpoint(total: Long): List<Segment> {
        val file = metaFile ?: return emptyList()
        if (!file.exists()) return emptyList()
        val json = runCatching { JSONObject(file.readText()) }.getOrNull() ?: return emptyList()
        if (json.optLong("total") != total) return emptyList()
        val array = json.optJSONArray("segments") ?: return emptyList()
        val segments = mutableListOf<Segment>()
        for (index in 0 until array.length()) {
            val item = array.optJSONObject(index) ?: continue
            segments.add(Segment(item.optLong("start"), item.optLong("end"), item.optLong("written")))
        }
        return segments
    }

    private fun split(total: Long, count: Int): List<Segment> {
        val size = (total + count - 1) / count
        val segments = mutableListOf<Segment>()
        var start = 0L
        while (start < total) {
            val end = min(total - 1, start + size - 1)
            segments.add(Segment(start, end, 0))
            start = end + 1
        }
        return segments
    }

    private fun pausedOutcome(total: Long, bytes: Long, etag: String?, lastModified: String?, contentType: String?) =
        Outcome(
            paused = true,
            totalBytes = total,
            downloadedBytes = bytes,
            etag = etag,
            lastModified = lastModified,
            contentType = contentType
        )

    private fun AtomicLongArray.total(): Long {
        var sum = 0L
        for (index in 0 until length()) sum += get(index)
        return sum
    }

    private fun sleep(attempt: Int) {
        Thread.sleep(min(4000L, 400L * attempt).coerceAtLeast(250L))
    }

    private companion object {
        const val MIN_SEGMENTED = 2L * 1024 * 1024
        const val BUFFER = 64 * 1024
    }
}
