package com.velocity.downloader.ui

import android.content.Intent
import android.os.Bundle
import android.view.View
import android.widget.TextView
import androidx.appcompat.app.AlertDialog
import com.velocity.downloader.R
import com.velocity.downloader.core.Settings

/** The four things Settings offers: Download Settings, Traffic Limits, About and Quit. */
class SettingsActivity : ThemedActivity() {

    private lateinit var settings: Settings

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_settings)
        settings = Settings.get(this)

        extendAppBar(findViewById(R.id.appBar))

        findViewById<View>(R.id.backButton).setOnClickListener { finish() }
        findViewById<View>(R.id.themeRow).setOnClickListener { chooseTheme() }
        findViewById<View>(R.id.rowDownloadSettings).setOnClickListener {
            startActivity(Intent(this, DownloadSettingsActivity::class.java))
        }
        findViewById<View>(R.id.rowTrafficLimits).setOnClickListener {
            startActivity(Intent(this, TrafficLimitsActivity::class.java))
        }
        findViewById<View>(R.id.rowAbout).setOnClickListener {
            startActivity(Intent(this, AboutActivity::class.java))
        }
        findViewById<View>(R.id.rowQuit).setOnClickListener { confirmQuit() }
    }

    override fun onResume() {
        super.onResume()
        findViewById<TextView>(R.id.themeValue).text = VelocityTheme.label(settings.theme)
    }

    private fun chooseTheme() {
        val themes = arrayOf(Settings.THEME_DARK, Settings.THEME_BEIGE)
        val labels = themes.map { VelocityTheme.label(it) }.toTypedArray()
        val checked = themes.indexOf(settings.theme).coerceAtLeast(0)
        Ui.show(AlertDialog.Builder(this)
            .setTitle(R.string.choose_theme)
            .setSingleChoiceItems(labels, checked) { dialog, which ->
                settings.theme = themes[which]
                dialog.dismiss()
                recreate()
            }
            .setNegativeButton(R.string.confirm_cancel, null)
            .create())
    }

    private fun confirmQuit() {
        Ui.show(AlertDialog.Builder(this)
            .setMessage(R.string.quit_question)
            .setPositiveButton(R.string.yes) { _, _ -> finishAffinity() }
            .setNegativeButton(R.string.no, null)
            .create())
    }
}
