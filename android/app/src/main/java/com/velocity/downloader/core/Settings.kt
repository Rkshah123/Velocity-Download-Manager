package com.velocity.downloader.core

import android.content.Context
import java.util.concurrent.CopyOnWriteArrayList

/**
 * Every preference the app exposes, in one place. The values live in SharedPreferences so they survive a restart, and
 * the download engine reads them live rather than receiving a copy, which keeps a change in Settings effective for the
 * transfer that is already running.
 */
class Settings private constructor(context: Context) {

    private val prefs = context.applicationContext.getSharedPreferences("velocity_settings", Context.MODE_PRIVATE)
    private val listeners = CopyOnWriteArrayList<(String) -> Unit>()

    init {
        // A folder picked with the previous version of the Home screen becomes the fixed folder.
        if (prefs.getString(KEY_FIXED_FOLDER, null) == null) {
            val legacy = context.getSharedPreferences("velocity", Context.MODE_PRIVATE)
            legacy.getString("dirUri", null)?.let { uri ->
                prefs.edit().putString(KEY_FIXED_FOLDER, uri).putBoolean(KEY_AUTO_FOLDER, false).apply()
            }
        }
        BandwidthLimiter.bytesPerSecond = speedLimit
    }

    var theme: String
        get() = prefs.getString(KEY_THEME, THEME_DARK) ?: THEME_DARK
        set(value) = prefs.edit().putString(KEY_THEME, if (value == THEME_BEIGE) THEME_BEIGE else THEME_DARK).apply()

    var startWithoutConfirmation: Boolean
        get() = prefs.getBoolean(KEY_START_WITHOUT_CONFIRMATION, true)
        set(value) = prefs.edit().putBoolean(KEY_START_WITHOUT_CONFIRMATION, value).apply()

    var removeDeletedFiles: Boolean
        get() = prefs.getBoolean(KEY_REMOVE_DELETED, true)
        set(value) = prefs.edit().putBoolean(KEY_REMOVE_DELETED, value).apply()

    var removeCompleted: Boolean
        get() = prefs.getBoolean(KEY_REMOVE_COMPLETED, false)
        set(value) = prefs.edit().putBoolean(KEY_REMOVE_COMPLETED, value).apply()

    var skipWebPages: Boolean
        get() = prefs.getBoolean(KEY_SKIP_WEB_PAGES, true)
        set(value) = prefs.edit().putBoolean(KEY_SKIP_WEB_PAGES, value).apply()

    var useServerTime: Boolean
        get() = prefs.getBoolean(KEY_SERVER_TIME, false)
        set(value) = prefs.edit().putBoolean(KEY_SERVER_TIME, value).apply()

    var autoFolder: Boolean
        get() = prefs.getBoolean(KEY_AUTO_FOLDER, true)
        set(value) = prefs.edit().putBoolean(KEY_AUTO_FOLDER, value).apply()

    var suggestByType: Boolean
        get() = prefs.getBoolean(KEY_SUGGEST_TYPE, false)
        set(value) = prefs.edit().putBoolean(KEY_SUGGEST_TYPE, value).apply()

    var suggestByUrl: Boolean
        get() = prefs.getBoolean(KEY_SUGGEST_URL, false)
        set(value) = prefs.edit().putBoolean(KEY_SUGGEST_URL, value).apply()

    /** The tree the user picked for a fixed folder, or null when the automatic Downloads folder is used. */
    var fixedFolderUri: String?
        get() = prefs.getString(KEY_FIXED_FOLDER, null)
        set(value) = prefs.edit().putString(KEY_FIXED_FOLDER, value).apply()

    /** The tree a new transfer should write to right now; null means the shared Downloads collection. */
    val activeFolderUri: String?
        get() = if (autoFolder) null else fixedFolderUri

    /** Bytes per second across all transfers; 0 is unlimited. */
    var speedLimit: Long
        get() = prefs.getLong(KEY_SPEED_LIMIT, 0L).coerceAtLeast(0L)
        set(value) {
            val limit = value.coerceAtLeast(0L)
            prefs.edit().putLong(KEY_SPEED_LIMIT, limit).apply()
            BandwidthLimiter.bytesPerSecond = limit
        }

    /** How many transfers may run at the same time. */
    var maxConcurrent: Int
        get() = prefs.getInt(KEY_MAX_CONCURRENT, 3).coerceIn(1, 8)
        set(value) {
            prefs.edit().putInt(KEY_MAX_CONCURRENT, value.coerceIn(1, 8)).apply()
            notifyChanged(KEY_MAX_CONCURRENT)
        }

    fun addListener(listener: (String) -> Unit) {
        listeners.addIfAbsent(listener)
    }

    fun removeListener(listener: (String) -> Unit) {
        listeners.remove(listener)
    }

    private fun notifyChanged(key: String) {
        listeners.toList().forEach { it(key) }
    }

    companion object {
        const val THEME_DARK = "dark"
        const val THEME_BEIGE = "beige"

        /** The speeds the Download speed slider offers; index 0 is unlimited. */
        val SPEED_STEPS = longArrayOf(0L, 64L, 128L, 256L, 512L, 1024L, 2048L, 4096L, 8192L, 16384L, 32768L)
            .map { it * 1024L }
            .toLongArray()

        private const val KEY_THEME = "theme"
        private const val KEY_START_WITHOUT_CONFIRMATION = "start_without_confirmation"
        private const val KEY_REMOVE_DELETED = "remove_deleted"
        private const val KEY_REMOVE_COMPLETED = "remove_completed"
        private const val KEY_SKIP_WEB_PAGES = "skip_web_pages"
        private const val KEY_SERVER_TIME = "server_time"
        private const val KEY_AUTO_FOLDER = "auto_folder"
        private const val KEY_SUGGEST_TYPE = "suggest_type"
        private const val KEY_SUGGEST_URL = "suggest_url"
        private const val KEY_FIXED_FOLDER = "fixed_folder"
        private const val KEY_SPEED_LIMIT = "speed_limit"
        private const val KEY_MAX_CONCURRENT = "max_concurrent"

        @Volatile
        private var instance: Settings? = null

        fun get(context: Context): Settings =
            instance ?: synchronized(this) {
                instance ?: Settings(context.applicationContext).also { instance = it }
            }
    }
}
