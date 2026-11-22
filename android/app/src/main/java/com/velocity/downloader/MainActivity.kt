package com.velocity.downloader

import android.Manifest
import android.content.ClipboardManager
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.view.View
import android.widget.Button
import android.widget.EditText
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AlertDialog
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.velocity.downloader.core.DownloadRecord
import com.velocity.downloader.core.DownloadStatus
import com.velocity.downloader.core.DownloadStore
import com.velocity.downloader.core.FileNames
import com.velocity.downloader.core.Links
import com.velocity.downloader.core.OutputStore
import com.velocity.downloader.core.Settings
import com.velocity.downloader.service.DownloadService
import com.velocity.downloader.ui.SettingsActivity
import com.velocity.downloader.ui.ThemedActivity
import com.velocity.downloader.ui.Ui
import com.velocity.downloader.ui.VelocityTheme
import java.io.File
import java.util.UUID

class MainActivity : ThemedActivity() {

    private lateinit var urlInput: EditText
    private lateinit var downloadButton: Button
    private lateinit var folderButton: Button
    private lateinit var folderLabel: TextView
    private lateinit var statusLine: TextView
    private lateinit var emptyView: TextView
    private lateinit var adapter: DownloadAdapter
    private lateinit var store: DownloadStore
    private lateinit var settings: Settings

    private val refreshListener: () -> Unit = { render() }

    private val folderPicker = registerForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        if (uri == null) return@registerForActivityResult
        try {
            contentResolver.takePersistableUriPermission(
                uri,
                Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION
            )
            settings.fixedFolderUri = uri.toString()
            settings.autoFolder = false
            updateFolderLabel()
        } catch (exception: SecurityException) {
            showStatus("That folder cannot be written to. Choose another one.", error = true)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        store = DownloadStore.get(this)
        settings = Settings.get(this)
        extendAppBar(findViewById(R.id.homeContent))
        avoidNavigationBar(findViewById(R.id.list))
        lightStatusBarIcons(settings.theme == VelocityTheme.BEIGE)

        urlInput = findViewById(R.id.urlInput)
        downloadButton = findViewById(R.id.downloadButton)
        folderButton = findViewById(R.id.folderButton)
        folderLabel = findViewById(R.id.folderLabel)
        statusLine = findViewById(R.id.statusLine)
        emptyView = findViewById(R.id.empty)

        adapter = DownloadAdapter(::handleAction)
        findViewById<RecyclerView>(R.id.list).apply {
            layoutManager = LinearLayoutManager(this@MainActivity)
            adapter = this@MainActivity.adapter
            itemAnimator = null
        }

        downloadButton.setOnClickListener { startDownload() }
        folderButton.setOnClickListener { folderPicker.launch(null) }
        findViewById<View>(R.id.settingsButton).setOnClickListener {
            startActivity(Intent(this, SettingsActivity::class.java))
        }

        updateFolderLabel()
        prefillFromClipboard()
        handleIntent(intent)
        requestNotificationPermission()
    }

    override fun onStart() {
        super.onStart()
        store.addListener(refreshListener)
        render()
        // If Android stopped the service while the app was away, opening Velocity puts its queue back to work.
        if (store.list().any { it.status == DownloadStatus.QUEUED || it.status == DownloadStatus.DOWNLOADING || it.status == DownloadStatus.ANALYZING }) {
            DownloadService.resumePending(this)
        }
        removeRowsForDeletedFiles()
    }

    override fun onResume() {
        super.onResume()
        updateFolderLabel()
    }

    override fun onStop() {
        super.onStop()
        store.removeListener(refreshListener)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleIntent(intent)
    }

    /* ─────────────────────────── actions ─────────────────────────── */

    private fun startDownload() {
        val pasted = urlInput.text.toString()
        if (pasted.isBlank()) {
            showStatus("Paste a download link first, then tap Download.", error = true)
            return
        }
        if (Links.hasUnsupportedScheme(pasted)) {
            showStatus(
                "Velocity downloads http and https links. Android has no built-in support for other schemes such as ftp:// or magnet:, which need their own app.",
                error = true
            )
            return
        }
        // A link shared together with its title, quoted, or pasted without its scheme is still that link.
        val url = Links.normalize(pasted)
        if (url == null) {
            showStatus("That text does not contain a web link. Paste an http:// or https:// address.", error = true)
            return
        }

        val guess = FileNames.fromUrl(url) ?: "download-${System.currentTimeMillis()}"
        val record = DownloadRecord(
            id = UUID.randomUUID().toString(),
            url = url,
            fileName = guess,
            dirUri = settings.activeFolderUri,
            status = DownloadStatus.QUEUED
        )

        if (settings.startWithoutConfirmation) {
            enqueue(record)
            return
        }

        val folder = settings.activeFolderUri?.let { runCatching { OutputStore.displayName(this, Uri.parse(it)) }.getOrNull() }
            ?: getString(R.string.downloads)
        Ui.show(AlertDialog.Builder(this)
            .setTitle(R.string.confirm_title)
            .setMessage("${record.fileName}\n$folder")
            .setPositiveButton(R.string.confirm_start) { _, _ -> enqueue(record) }
            .setNegativeButton(R.string.confirm_cancel, null)
            .create())
    }

    private fun enqueue(record: DownloadRecord) {
        DownloadService.enqueue(this, record)
        urlInput.setText("")
        showStatus("Added ${record.fileName} — reading the link…", error = false)
    }

    private fun handleAction(record: DownloadRecord, action: DownloadAction) {
        when (action) {
            DownloadAction.PAUSE -> DownloadService.pause(this, record.id)
            DownloadAction.RESUME -> DownloadService.resume(this, record.id)
            DownloadAction.RETRY -> DownloadService.retry(this, record.id)
            DownloadAction.CANCEL -> DownloadService.cancel(this, record.id)
            // A finished file stays on disk when its row goes; an unfinished one takes its partial file with it.
            DownloadAction.REMOVE -> if (record.status == DownloadStatus.COMPLETED) store.remove(record.id) else DownloadService.cancel(this, record.id)
            DownloadAction.OPEN -> openFile(record)
        }
    }

    private fun openFile(record: DownloadRecord) {
        val saved = record.savedUri ?: return
        val intent = Intent(Intent.ACTION_VIEW)
            .setDataAndType(Uri.parse(saved), record.contentType ?: "*/*")
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
        try {
            startActivity(intent)
        } catch (exception: Exception) {
            Toast.makeText(this, "No app on this phone can open ${record.fileName}.", Toast.LENGTH_LONG).show()
        }
    }

    /* ─────────────────────────── rendering ─────────────────────────── */

    private fun render() {
        val items = store.list()
        adapter.submit(items)
        emptyView.visibility = if (items.isEmpty()) View.VISIBLE else View.GONE
    }

    private fun updateFolderLabel() {
        val name = settings.activeFolderUri?.let { uri ->
            runCatching { OutputStore.displayName(this, Uri.parse(uri)) }.getOrNull()
        }
        folderLabel.text = name ?: getString(R.string.downloads)
    }

    private fun showStatus(message: String, error: Boolean) {
        statusLine.text = message
        statusLine.setTextColor(VelocityTheme.color(this, if (error) R.attr.vWarning else R.attr.vMuted))
        statusLine.visibility = View.VISIBLE
    }

    /** "Automatically remove deleted files from download list": a finished row whose file is gone leaves the list. */
    private fun removeRowsForDeletedFiles() {
        if (!settings.removeDeletedFiles) return
        val completed = store.list().filter { it.status == DownloadStatus.COMPLETED && it.savedUri != null }
        if (completed.isEmpty()) return
        Thread {
            completed.filterNot { savedFileExists(it.savedUri!!) }.forEach { store.remove(it.id) }
        }.start()
    }

    private fun savedFileExists(saved: String): Boolean = try {
        val uri = Uri.parse(saved)
        if (uri.scheme == "file") {
            uri.path?.let { File(it).exists() } ?: false
        } else {
            contentResolver.openInputStream(uri)?.use { true } ?: false
        }
    } catch (exception: Exception) {
        false
    }

    private fun prefillFromClipboard() {
        if (urlInput.text.isNotBlank()) return
        val clipboard = getSystemService(ClipboardManager::class.java) ?: return
        val clip = clipboard.primaryClip ?: return
        if (clip.itemCount == 0) return
        val text = clip.getItemAt(0).coerceToText(this)?.toString()?.trim().orEmpty()
        Links.normalize(text)?.let { urlInput.setText(it) }
    }

    private fun handleIntent(intent: Intent?) {
        if (intent?.action != Intent.ACTION_SEND) return
        val shared = intent.getStringExtra(Intent.EXTRA_TEXT)?.trim().orEmpty()
        if (shared.isNotBlank()) {
            // A share can carry a title next to the link; the link is what is kept.
            urlInput.setText(Links.normalize(shared) ?: shared)
            urlInput.setSelection(urlInput.text.length)
        }
    }

    private fun requestNotificationPermission() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) return
        if (checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED) return
        requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
    }
}
