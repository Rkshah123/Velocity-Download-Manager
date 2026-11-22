package com.velocity.downloader.ui

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.provider.DocumentsContract
import android.view.View
import android.widget.ImageButton
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.widget.AppCompatRadioButton
import androidx.appcompat.widget.SwitchCompat
import com.velocity.downloader.R
import com.velocity.downloader.core.OutputStore
import com.velocity.downloader.core.Settings

class DownloadSettingsActivity : ThemedActivity() {

    private lateinit var settings: Settings
    private lateinit var radioAuto: AppCompatRadioButton
    private lateinit var radioFixed: AppCompatRadioButton
    private lateinit var rowSuggestType: View
    private lateinit var rowSuggestUrl: View
    private lateinit var switchSuggestType: SwitchCompat
    private lateinit var switchSuggestUrl: SwitchCompat
    private lateinit var folderRow: View
    private lateinit var folderValue: TextView
    private lateinit var folderPath: TextView

    private val folderPicker = registerForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        if (uri == null) return@registerForActivityResult
        try {
            contentResolver.takePersistableUriPermission(
                uri,
                Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION
            )
            settings.fixedFolderUri = uri.toString()
            settings.autoFolder = false
            refresh()
        } catch (exception: SecurityException) {
            Toast.makeText(this, "That folder cannot be written to. Choose another one.", Toast.LENGTH_LONG).show()
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_download_settings)
        settings = Settings.get(this)

        extendAppBar(findViewById(R.id.appBar))
        avoidNavigationBar(findViewById(R.id.content))

        findViewById<View>(R.id.backButton).setOnClickListener { finish() }

        radioAuto = findViewById(R.id.radioAutoFolder)
        radioFixed = findViewById(R.id.radioFixedFolder)
        rowSuggestType = findViewById(R.id.rowSuggestType)
        rowSuggestUrl = findViewById(R.id.rowSuggestUrl)
        switchSuggestType = findViewById(R.id.switchSuggestType)
        switchSuggestUrl = findViewById(R.id.switchSuggestUrl)
        folderRow = findViewById(R.id.folderRow)
        folderValue = findViewById(R.id.folderValue)
        folderPath = findViewById(R.id.folderPath)

        bindSwitch(R.id.switchConfirm, settings.startWithoutConfirmation) { settings.startWithoutConfirmation = it }
        bindSwitch(R.id.switchRemoveDeleted, settings.removeDeletedFiles) { settings.removeDeletedFiles = it }
        bindSwitch(R.id.switchRemoveCompleted, settings.removeCompleted) { settings.removeCompleted = it }
        bindSwitch(R.id.switchWebPages, settings.skipWebPages) { settings.skipWebPages = it }
        bindSwitch(R.id.switchServerTime, settings.useServerTime) { settings.useServerTime = it }
        bindSwitch(R.id.switchSuggestType, settings.suggestByType) { settings.suggestByType = it }
        bindSwitch(R.id.switchSuggestUrl, settings.suggestByUrl) { settings.suggestByUrl = it }

        Ui.tintRadio(radioAuto)
        Ui.tintRadio(radioFixed)

        radioAuto.setOnClickListener {
            settings.autoFolder = true
            refresh()
        }
        radioFixed.setOnClickListener {
            if (settings.fixedFolderUri == null) {
                folderPicker.launch(null)
            } else {
                settings.autoFolder = false
                refresh()
            }
        }

        folderRow.setOnClickListener { pickFolder() }
        findViewById<ImageButton>(R.id.folderButton).setOnClickListener { pickFolder() }

        refresh()
    }

    private fun bindSwitch(id: Int, initial: Boolean, changed: (Boolean) -> Unit) {
        val switch = findViewById<SwitchCompat>(id)
        Ui.tintSwitch(switch)
        switch.isChecked = initial
        switch.setOnCheckedChangeListener { _, checked -> changed(checked) }
    }

    private fun pickFolder() {
        if (!settings.autoFolder) folderPicker.launch(null)
    }

    private fun refresh() {
        val auto = settings.autoFolder
        radioAuto.isChecked = auto
        radioFixed.isChecked = !auto

        Ui.setEnabled(rowSuggestType, auto)
        Ui.setEnabled(rowSuggestUrl, auto)
        switchSuggestType.isEnabled = auto
        switchSuggestUrl.isEnabled = auto

        Ui.setEnabled(folderRow, !auto)

        val uri = settings.fixedFolderUri?.let { runCatching { Uri.parse(it) }.getOrNull() }
        val name = if (!auto) uri?.let { runCatching { OutputStore.displayName(this, it) }.getOrNull() } else null
        folderValue.text = name ?: getString(R.string.no_folder_chosen)
        folderValue.alpha = if (!auto) 1f else 0.45f

        if (!auto && uri != null) {
            folderPath.text = runCatching { DocumentsContract.getTreeDocumentId(uri) }.getOrNull() ?: uri.path
            folderPath.visibility = View.VISIBLE
        } else {
            folderPath.visibility = View.GONE
        }
    }
}
