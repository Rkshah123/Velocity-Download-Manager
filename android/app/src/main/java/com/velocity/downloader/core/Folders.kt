package com.velocity.downloader.core

/**
 * Turns the two "suggest folders" switches into the sub folders of the automatic Downloads folder. The type switch
 * groups a file by what it is, the URL switch by the site it came from, and both can be on at once.
 */
object Folders {

    fun subfolder(settings: Settings, record: DownloadRecord): String? {
        if (!settings.autoFolder) return null
        val parts = mutableListOf<String>()
        if (settings.suggestByType) typeFolder(record.fileName, record.contentType)?.let { parts.add(sanitize(it)) }
        if (settings.suggestByUrl) {
            host(record.finalUrl ?: record.url)?.let { parts.add(sanitize(it)) }
        }
        return parts.filter { it.isNotBlank() }.takeIf { it.isNotEmpty() }?.joinToString("/")
    }

    fun typeFolder(fileName: String?, contentType: String?): String? {
        val mime = contentType?.substringBefore(';')?.trim()?.lowercase().orEmpty()
        val extension = fileName?.substringAfterLast('.', "")?.lowercase().orEmpty()
        return when {
            mime.startsWith("image/") || extension in IMAGES -> "Images"
            mime.startsWith("video/") || extension in VIDEOS -> "Videos"
            mime.startsWith("audio/") || extension in AUDIO -> "Audio"
            mime.startsWith("text/") || mime.contains("pdf") || mime.contains("document") ||
                mime.contains("sheet") || mime.contains("presentation") || extension in DOCUMENTS -> "Documents"
            extension in ARCHIVES || mime.contains("zip") || mime.contains("rar") || mime.contains("compressed") -> "Archives"
            extension == "apk" -> "Apps"
            else -> "Other"
        }
    }

    fun host(url: String): String? = try {
        val parsed = java.net.URI(url)
        parsed.host?.removePrefix("www.")?.takeIf { it.isNotBlank() }
    } catch (exception: Exception) {
        null
    }

    /** A folder name that every file system accepts: no separators, no reserved characters, a sensible length. */
    fun sanitize(name: String): String = name
        .replace(Regex("[\\\\/:*?\"<>|\\p{Cntrl}]"), "-")
        .replace(Regex("\\s+"), " ")
        .trim(' ', '.')
        .take(48)

    private val IMAGES = setOf("jpg", "jpeg", "png", "gif", "webp", "bmp", "heic", "svg", "avif")
    private val VIDEOS = setOf("mp4", "mkv", "webm", "avi", "mov", "m4v", "3gp", "flv", "ts")
    private val AUDIO = setOf("mp3", "m4a", "aac", "wav", "flac", "ogg", "opus", "wma")
    private val DOCUMENTS = setOf("pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "csv", "epub", "html", "htm")
    private val ARCHIVES = setOf("zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso")
}
