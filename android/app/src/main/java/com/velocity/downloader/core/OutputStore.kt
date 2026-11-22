package com.velocity.downloader.core

import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.DocumentsContract
import android.provider.MediaStore
import java.io.File
import java.io.IOException

/**
 * Puts a finished file where the user asked for it. A tree chosen with Android's folder picker is written through
 * scoped storage; without a choice the file lands in the shared Downloads collection. A name that already exists is
 * never reused, so a download can never silently replace another file.
 */
object OutputStore {

    fun save(
        context: Context,
        record: DownloadRecord,
        source: File,
        subfolder: String? = null,
        useServerTime: Boolean = false
    ): Uri {
        val mime = record.contentType?.substringBefore(';')?.trim()?.takeIf { it.isNotBlank() } ?: guessMime(record.fileName)
        val tree = record.dirUri?.let { runCatching { Uri.parse(it) }.getOrNull() }
        val modified = if (useServerTime) httpDate(record.lastModified) else null
        return if (tree != null) {
            saveToTree(context, tree, record.fileName, mime, source)
        } else {
            saveToDownloads(context, record.fileName, mime, source, subfolder, modified)
        }
    }

    fun displayName(context: Context, treeUri: Uri): String? = try {
        val document = DocumentsContract.buildDocumentUriUsingTree(treeUri, DocumentsContract.getTreeDocumentId(treeUri))
        context.contentResolver.query(document, arrayOf(DocumentsContract.Document.COLUMN_DISPLAY_NAME), null, null, null)?.use { cursor ->
            if (cursor.moveToFirst()) cursor.getString(0) else null
        }
    } catch (exception: Exception) {
        null
    }

    private fun saveToTree(context: Context, tree: Uri, desiredName: String, mime: String, source: File): Uri {
        val resolver = context.contentResolver
        val name = FileNames.unique(desiredName, existingNames(context, tree))
        val parent = DocumentsContract.buildDocumentUriUsingTree(tree, DocumentsContract.getTreeDocumentId(tree))
        val document = DocumentsContract.createDocument(resolver, parent, mime, name)
            ?: throw IOException("The chosen folder did not accept the file.")
        try {
            resolver.openOutputStream(document, "wt")?.use { output ->
                source.inputStream().use { input -> input.copyTo(output, DEFAULT_BUFFER_SIZE) }
                output.flush()
            } ?: throw IOException("The chosen folder could not be opened for writing.")
        } catch (exception: Exception) {
            runCatching { DocumentsContract.deleteDocument(resolver, document) }
            throw exception
        }
        return document
    }

    private fun existingNames(context: Context, tree: Uri): Set<String> {
        val names = mutableSetOf<String>()
        val children = DocumentsContract.buildChildDocumentsUriUsingTree(tree, DocumentsContract.getTreeDocumentId(tree))
        context.contentResolver.query(
            children,
            arrayOf(DocumentsContract.Document.COLUMN_DISPLAY_NAME),
            null,
            null,
            null
        )?.use { cursor ->
            while (cursor.moveToNext()) cursor.getString(0)?.let { names.add(it) }
        }
        return names
    }

    private fun saveToDownloads(
        context: Context,
        desiredName: String,
        mime: String,
        source: File,
        subfolder: String?,
        modifiedSeconds: Long?
    ): Uri {
        // Suggested folders live inside Download/Velocity, and every part is cleaned before it reaches a path.
        val suggested = cleanedFolder(subfolder)
        val relative = if (suggested.isEmpty()) {
            Environment.DIRECTORY_DOWNLOADS
        } else {
            (listOf(Environment.DIRECTORY_DOWNLOADS, "Velocity") + suggested).joinToString("/")
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            val resolver = context.contentResolver
            val values = ContentValues().apply {
                put(MediaStore.MediaColumns.DISPLAY_NAME, desiredName)
                put(MediaStore.MediaColumns.MIME_TYPE, mime)
                put(MediaStore.MediaColumns.RELATIVE_PATH, relative)
                put(MediaStore.MediaColumns.IS_PENDING, 1)
                if (modifiedSeconds != null) put(MediaStore.MediaColumns.DATE_MODIFIED, modifiedSeconds)
            }
            val collection = MediaStore.Downloads.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY)
            val uri = resolver.insert(collection, values) ?: throw IOException("Android did not create a Downloads entry.")
            try {
                resolver.openOutputStream(uri)?.use { output ->
                    source.inputStream().use { input -> input.copyTo(output, DEFAULT_BUFFER_SIZE) }
                    output.flush()
                } ?: throw IOException("Android did not open the Downloads entry for writing.")
                values.clear()
                values.put(MediaStore.MediaColumns.IS_PENDING, 0)
                resolver.update(uri, values, null, null)
            } catch (exception: Exception) {
                runCatching { resolver.delete(uri, null, null) }
                throw exception
            }
            return uri
        }

        val folder = File(context.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS) ?: context.filesDir, "")
        val targetFolder = if (suggested.isEmpty()) folder else File(folder, "Velocity" + suggested.joinToString("/") { "/$it" })
        targetFolder.mkdirs()
        val existing = targetFolder.list()?.toSet() ?: emptySet()
        val target = File(targetFolder, FileNames.unique(desiredName, existing))
        source.copyTo(target, overwrite = false)
        if (modifiedSeconds != null) target.setLastModified(modifiedSeconds * 1000L)
        return Uri.fromFile(target)
    }

    private fun cleanedFolder(subfolder: String?): List<String> =
        subfolder.orEmpty()
            .split('/')
            .map { Folders.sanitize(it) }
            .filter { it.isNotBlank() && it != "." && it != ".." }

    /** Reads the server's Last-Modified header so the saved file keeps the file's own date. */
    private fun httpDate(value: String?): Long? = try {
        val format = java.text.SimpleDateFormat("EEE, dd MMM yyyy HH:mm:ss zzz", java.util.Locale.US)
        format.timeZone = java.util.TimeZone.getTimeZone("GMT")
        value?.let { format.parse(it)?.time?.div(1000L) }
    } catch (exception: Exception) {
        null
    }

    private fun guessMime(fileName: String): String {
        val extension = fileName.substringAfterLast('.', "").lowercase()
        return when (extension) {
            "mp4" -> "video/mp4"
            "mkv" -> "video/x-matroska"
            "webm" -> "video/webm"
            "mp3" -> "audio/mpeg"
            "m4a" -> "audio/mp4"
            "pdf" -> "application/pdf"
            "zip" -> "application/zip"
            "rar" -> "application/vnd.rar"
            "7z" -> "application/x-7z-compressed"
            "apk" -> "application/vnd.android.package-archive"
            "jpg", "jpeg" -> "image/jpeg"
            "png" -> "image/png"
            "gif" -> "image/gif"
            "txt" -> "text/plain"
            "html" -> "text/html"
            else -> "application/octet-stream"
        }
    }
}
