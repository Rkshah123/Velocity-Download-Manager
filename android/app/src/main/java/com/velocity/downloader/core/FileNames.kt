package com.velocity.downloader.core

import java.net.URLDecoder

/**
 * File-name rules, kept free of Android types so they can be unit-tested on the JVM: a name may come from
 * `Content-Disposition`, from the URL path, or from the content type when the server supplies nothing usable.
 */
object FileNames {

    fun sanitize(raw: String): String {
        var name = raw.trim()
            .replace(Regex("[\\\\/:*?\"<>|\\u0000-\\u001F]"), "_")
            .trim()
            .trimEnd('.')
        if (name.isEmpty()) name = "download"
        if (name.length > 180) {
            val dot = name.lastIndexOf('.')
            name = if (dot in 1..(name.length - 1)) {
                val extension = name.substring(dot).take(12)
                name.take(180 - extension.length) + extension
            } else {
                name.take(180)
            }
        }
        return name
    }

    /** `filename*=UTF-8''name` wins over a plain `filename=`, as RFC 6266 says. */
    fun fromDisposition(value: String?): String? {
        if (value.isNullOrBlank()) return null
        val parts = value.split(';')
        parts.firstOrNull { it.trim().startsWith("filename*=", ignoreCase = true) }?.let { part ->
            val raw = part.substringAfter('=').trim()
            val encoded = if (raw.contains("''")) raw.substringAfter("''") else raw
            val decoded = runCatching { URLDecoder.decode(encoded, "UTF-8") }.getOrDefault(encoded)
            return sanitize(decoded)
        }
        parts.firstOrNull { it.trim().startsWith("filename=", ignoreCase = true) }?.let { part ->
            val raw = part.substringAfter('=').trim().trim('"')
            if (raw.isNotBlank()) return sanitize(runCatching { URLDecoder.decode(raw, "UTF-8") }.getOrDefault(raw))
        }
        return null
    }

    fun fromUrl(url: String): String? {
        val path = runCatching { java.net.URI(url).rawPath }.getOrNull() ?: return null
        val last = path.trimEnd('/').substringAfterLast('/')
        if (last.isBlank()) return null
        // In a URL path a `+` is a literal plus, not a space, so it is protected from the form decoder.
        val decoded = runCatching { URLDecoder.decode(last.replace("+", "%2B"), "UTF-8") }.getOrDefault(last)
        return sanitize(decoded)
    }

    fun extensionFor(contentType: String?): String? = when (contentType?.substringBefore(';')?.trim()?.lowercase()) {
        "video/mp4" -> "mp4"
        "video/x-matroska" -> "mkv"
        "video/webm" -> "webm"
        "video/quicktime" -> "mov"
        "video/x-msvideo" -> "avi"
        "audio/mpeg" -> "mp3"
        "audio/mp4" -> "m4a"
        "audio/ogg", "audio/opus" -> "ogg"
        "audio/flac", "audio/x-flac" -> "flac"
        "audio/wav", "audio/x-wav" -> "wav"
        "application/pdf" -> "pdf"
        "application/zip" -> "zip"
        "application/x-zip-compressed" -> "zip"
        "application/vnd.rar", "application/x-rar-compressed" -> "rar"
        "application/x-7z-compressed" -> "7z"
        "application/x-iso9660-image", "application/octet-stream.iso" -> "iso"
        "application/vnd.android.package-archive" -> "apk"
        "application/msword" -> "doc"
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" -> "docx"
        "application/vnd.ms-excel" -> "xls"
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" -> "xlsx"
        "application/vnd.ms-powerpoint" -> "ppt"
        "application/vnd.openxmlformats-officedocument.presentationml.presentation" -> "pptx"
        "text/plain" -> "txt"
        "application/json" -> "json"
        "image/jpeg" -> "jpg"
        "image/png" -> "png"
        "image/gif" -> "gif"
        "image/webp" -> "webp"
        "image/svg+xml" -> "svg"
        "text/html" -> "html"
        else -> null
    }

    /** Only adds an extension when the server gave no name with one; a name the server chose is never rewritten. */
    fun ensureExtension(name: String, contentType: String?): String {
        val dot = name.lastIndexOf('.')
        val hasExtension = dot > 0 && dot < name.length - 1 && name.length - dot <= 12
        if (hasExtension) return name
        val extension = extensionFor(contentType) ?: return name
        return "$name.$extension"
    }

    fun unique(candidate: String, existing: Set<String>): String {
        if (candidate !in existing) return candidate
        val dot = candidate.lastIndexOf('.')
        val base = if (dot > 0) candidate.substring(0, dot) else candidate
        val extension = if (dot > 0) candidate.substring(dot) else ""
        var index = 1
        while (true) {
            val name = "$base ($index)$extension"
            if (name !in existing) return name
            index++
        }
    }
}
