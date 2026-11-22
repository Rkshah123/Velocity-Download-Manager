package com.velocity.downloader.ui

import android.content.Context
import android.content.res.ColorStateList
import android.graphics.Color
import android.view.View
import android.widget.SeekBar
import androidx.appcompat.widget.AppCompatRadioButton
import androidx.appcompat.widget.SwitchCompat
import androidx.appcompat.app.AlertDialog
import com.velocity.downloader.R

/** Small helpers that give the platform widgets the colours of the active theme. */
object Ui {

    fun tintSwitch(switch: SwitchCompat) {
        val context = switch.context
        val on = VelocityTheme.color(context, R.attr.vAccent)
        val offTrack = VelocityTheme.color(context, R.attr.vTrackOff)
        val offThumb = VelocityTheme.color(context, R.attr.vThumbOff)
        val onThumb = VelocityTheme.color(context, R.attr.vThumbOn)

        switch.thumbTintList = ColorStateList(
            arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()),
            intArrayOf(onThumb, offThumb)
        )
        switch.trackTintList = ColorStateList(
            arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()),
            intArrayOf(on, offTrack)
        )
    }

    fun tintRadio(radio: AppCompatRadioButton) {
        val colors = ColorStateList(
            arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()),
            intArrayOf(VelocityTheme.color(radio.context, R.attr.vAccent), VelocityTheme.color(radio.context, R.attr.vMuted))
        )
        radio.buttonTintList = colors
    }

    fun tintSeekBar(seek: SeekBar) {
        val accent = VelocityTheme.color(seek.context, R.attr.vAccent)
        val track = VelocityTheme.color(seek.context, R.attr.vStroke)
        seek.progressTintList = ColorStateList.valueOf(accent)
        seek.thumbTintList = ColorStateList.valueOf(accent)
        seek.progressBackgroundTintList = ColorStateList.valueOf(track)
    }

    fun setEnabled(view: View, enabled: Boolean) {
        view.isEnabled = enabled
        view.alpha = if (enabled) 1f else 0.45f
    }

    fun dp(context: Context, value: Int): Int = (value * context.resources.displayMetrics.density).toInt()

    fun color(context: Context, attr: Int): Int = VelocityTheme.color(context, attr)

    /** Shows a dialog with sentence-case buttons, as the reference designs use. */
    fun show(dialog: AlertDialog) {
        dialog.setOnShowListener {
            dialog.getButton(AlertDialog.BUTTON_POSITIVE)?.isAllCaps = false
            dialog.getButton(AlertDialog.BUTTON_NEGATIVE)?.isAllCaps = false
            dialog.getButton(AlertDialog.BUTTON_NEUTRAL)?.isAllCaps = false
        }
        dialog.show()
    }

    const val TRANSPARENT = Color.TRANSPARENT
}
