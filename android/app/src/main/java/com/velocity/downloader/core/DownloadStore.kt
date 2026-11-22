package com.velocity.downloader.core

import android.content.Context
import android.os.Handler
import android.os.Looper
import org.json.JSONArray
import java.io.File
import java.util.concurrent.CopyOnWriteArrayList

/**
 * The single source of truth for the download list. It is in-memory for speed and mirrored to a JSON file on every
 * change so a process death (or a swipe from recents) never loses the queue. Listeners are always notified on the
 * main thread, which keeps the UI free of threading mistakes.
 */
class DownloadStore private constructor(private val context: Context) {

    private val file = File(context.filesDir, "downloads.json")
    private val items = CopyOnWriteArrayList<DownloadRecord>()
    private val listeners = CopyOnWriteArrayList<() -> Unit>()
    private val main = Handler(Looper.getMainLooper())
    private val writeLock = Any()
    private var loaded = false

    fun load(): List<DownloadRecord> {
        synchronized(writeLock) {
            if (loaded) return items.toList()
            loaded = true
            if (!file.exists()) return items.toList()
            val text = runCatching { file.readText() }.getOrNull() ?: return items.toList()
            val array = runCatching { JSONArray(text) }.getOrNull() ?: return items.toList()
            for (index in 0 until array.length()) {
                val json = array.optJSONObject(index) ?: continue
                DownloadRecord.fromJson(json)?.let { items.add(it) }
            }
            return items.toList()
        }
    }

    fun list(): List<DownloadRecord> = items.toList()

    fun find(id: String): DownloadRecord? = items.firstOrNull { it.id == id }

    fun add(record: DownloadRecord) {
        items.removeAll { it.id == record.id }
        items.add(0, record)
        persist()
        notifyChanged()
    }

    fun update(record: DownloadRecord) {
        val index = items.indexOfFirst { it.id == record.id }
        if (index < 0) {
            items.add(0, record)
        } else {
            items[index] = record
        }
        persist()
        notifyChanged()
    }

    fun remove(id: String) {
        items.removeAll { it.id == id }
        persist()
        notifyChanged()
    }

    fun clearFinished() {
        items.removeAll { it.status == DownloadStatus.COMPLETED || it.status == DownloadStatus.CANCELLED }
        persist()
        notifyChanged()
    }

    fun addListener(listener: () -> Unit) {
        listeners.addIfAbsent(listener)
    }

    fun removeListener(listener: () -> Unit) {
        listeners.remove(listener)
    }

    fun notifyChanged() {
        main.post { listeners.toList().forEach { it() } }
    }

    private fun persist() {
        synchronized(writeLock) {
            val array = JSONArray()
            items.forEach { array.put(it.toJson()) }
            val temporary = File(context.filesDir, "downloads.json.tmp")
            runCatching {
                temporary.writeText(array.toString())
                if (file.exists()) file.delete()
                temporary.renameTo(file)
            }
        }
    }

    companion object {
        @Volatile
        private var instance: DownloadStore? = null

        fun get(context: Context): DownloadStore =
            instance ?: synchronized(this) {
                instance ?: DownloadStore(context.applicationContext).also { it.load(); instance = it }
            }
    }
}
