package com.velocity.downloader.core

/** Byte, speed and time formatting for the list rows and the notification. */
object Formats {

    fun bytes(value: Long): String {
        if (value < 0) return "unknown"
        if (value < 1024) return "$value B"
        val units = arrayOf("KB", "MB", "GB", "TB")
        var size = value.toDouble() / 1024.0
        var index = 0
        while (size >= 1024 && index < units.lastIndex) {
            size /= 1024.0
            index++
        }
        return if (size >= 100) "${size.toInt()} ${units[index]}" else String.format(java.util.Locale.US, "%.1f %s", size, units[index])
    }

    fun speed(bytesPerSecond: Long): String = if (bytesPerSecond <= 0) "" else bytes(bytesPerSecond) + "/s"

    fun remaining(seconds: Long): String = when {
        seconds < 0 -> ""
        seconds < 60 -> "${seconds}s left"
        seconds < 3600 -> "${seconds / 60}m ${seconds % 60}s left"
        else -> "${seconds / 3600}h ${(seconds % 3600) / 60}m left"
    }

    fun eta(done: Long, total: Long, speed: Long): Long =
        if (speed > 0 && total > done) (total - done) / speed else -1
}
