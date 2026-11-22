package com.velocity.downloader.core

import java.net.URI

/**
 * Turns whatever the user pasted into a fetchable http(s) address. Phones copy links together with titles, share
 * text, quotes and brackets, and many apps leave out the scheme, so the text is searched for a link instead of being
 * rejected for not starting with `http`.
 */
object Links {

    private val HTTP = Regex("https?://", RegexOption.IGNORE_CASE)
    private val SCHEME = Regex("^[A-Za-z][A-Za-z0-9+.-]*:")
    private val HOST = Regex(
        "^(?:[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?\\.)+[A-Za-z]{2,}(?::\\d{1,5})?(?:[/?#].*)?$"
    )
    private val IPV4 = Regex("^(?:\\d{1,3}\\.){3}\\d{1,3}(?::\\d{1,5})?(?:[/?#].*)?$")
    private val UNSUPPORTED = Regex(
        "^(?:ftp|ftps|sftp|smb|magnet|file|data|blob|javascript|mailto|tel):",
        RegexOption.IGNORE_CASE
    )

    private val EDGES = charArrayOf('"', '\'', '<', '>', '“', '”', '‘', '’', '(', ')', '[', ']', '{', '}')
    private const val HEX = "0123456789ABCDEF"

    /** `ftp://`, `magnet:` and friends need their own app; the caller explains that instead of calling it invalid. */
    fun hasUnsupportedScheme(raw: String): Boolean = UNSUPPORTED.containsMatchIn(raw.trim())

    /**
     * Returns a usable http(s) URL, or null when the text holds no link at all. A link is taken from the middle of
     * pasted text, `https://` is assumed when only a host was pasted, and characters the phone added around it
     * (quotes, brackets, a trailing full stop) are dropped.
     */
    fun normalize(raw: String): String? {
        val text = raw.trim()
        if (text.isEmpty()) return null

        val embedded = HTTP.find(text)
        val candidate = if (embedded != null) {
            text.substring(embedded.range.first)
        } else {
            val token = text.split(Regex("\\s+"))
                .map { it.trim(*EDGES).trim() }
                .firstOrNull { isHostLike(it) }
                ?: return null
            "https://$token"
        }

        val leading = candidate.trimStart(*EDGES)
        val end = leading.indexOfFirst { it.isWhitespace() || it in "\"'<>“”‘’" }
        var url = (if (end > 0) leading.substring(0, end) else leading).trimEnd(*EDGES)
        url = stripTrailing(url)
        if (!HTTP.containsMatchIn(url)) return null
        url = percentEncode(url)
        if (url.length > 8192) return null
        // "https://" on its own is not a link; a real one always resolves to a host.
        val host = runCatching { URI(url).host }.getOrNull()
        if (host.isNullOrBlank()) return null
        return url
    }

    private fun isHostLike(token: String): Boolean {
        if (token.isEmpty() || token.any { it.isWhitespace() }) return false
        if (token.contains('@')) return false
        if (SCHEME.containsMatchIn(token)) return false
        return HOST.matches(token) || IPV4.matches(token)
    }

    /** Punctuation at the end of a sentence is not part of the address; an unmatched bracket is not either. */
    private fun stripTrailing(value: String): String {
        var url = value
        while (url.isNotEmpty()) {
            val last = url.last()
            val remove = when (last) {
                '.', ',', ';', ':', '!', '?', '\'', '"', '“', '”' -> true
                ')' -> url.count { it == '(' } < url.count { it == ')' }
                ']' -> url.count { it == '[' } < url.count { it == ']' }
                '}' -> url.count { it == '{' } < url.count { it == '}' }
                else -> false
            }
            if (!remove) break
            url = url.dropLast(1)
        }
        return url
    }

    /** Percent-encodes only what a URL cannot carry literally, so existing escapes are left untouched. */
    fun percentEncode(value: String): String {
        val builder = StringBuilder(value.length)
        for (character in value) {
            when {
                character == ' ' -> builder.append("%20")
                character.code in 0x21..0x7E && character !in "\"<>\\^`{|}" -> builder.append(character)
                else -> {
                    for (byte in character.toString().toByteArray(Charsets.UTF_8)) {
                        val value8 = byte.toInt() and 0xFF
                        builder.append('%').append(HEX[value8 shr 4]).append(HEX[value8 and 0x0F])
                    }
                }
            }
        }
        return builder.toString()
    }
}
