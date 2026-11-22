package com.velocity.downloader.ui

import android.os.Bundle
import android.view.View
import android.widget.ImageButton
import android.widget.ImageView
import android.widget.SeekBar
import android.widget.TextView
import com.velocity.downloader.R
import com.velocity.downloader.core.Formats
import com.velocity.downloader.core.Settings
import kotlin.math.abs

/** The two limits the reference screen offers, each an expandable section. */
class TrafficLimitsActivity : ThemedActivity() {

    private lateinit var settings: Settings
    private lateinit var speedValue: TextView
    private lateinit var connectionsValue: TextView
    private lateinit var decreaseButton: ImageButton
    private lateinit var increaseButton: ImageButton

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_traffic_limits)
        settings = Settings.get(this)

        extendAppBar(findViewById(R.id.appBar))
        avoidNavigationBar(findViewById(R.id.content))

        findViewById<View>(R.id.backButton).setOnClickListener { finish() }

        speedValue = findViewById(R.id.speedValue)
        connectionsValue = findViewById(R.id.connectionsValue)
        decreaseButton = findViewById(R.id.decreaseButton)
        increaseButton = findViewById(R.id.increaseButton)

        val speedBody = findViewById<View>(R.id.speedBody)
        val connectionsBody = findViewById<View>(R.id.connectionsBody)
        val speedChevron = findViewById<ImageView>(R.id.speedChevron)
        val connectionsChevron = findViewById<ImageView>(R.id.connectionsChevron)

        toggle(R.id.speedHeader, speedBody, speedChevron, false)
        toggle(R.id.connectionsHeader, connectionsBody, connectionsChevron, false)

        val seek = findViewById<SeekBar>(R.id.speedSeek)
        Ui.tintSeekBar(seek)
        seek.max = Settings.SPEED_STEPS.size - 1
        seek.progress = nearestStep(settings.speedLimit)
        showSpeed(Settings.SPEED_STEPS[seek.progress])
        seek.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(bar: SeekBar, progress: Int, fromUser: Boolean) {
                val limit = Settings.SPEED_STEPS[progress.coerceIn(0, Settings.SPEED_STEPS.size - 1)]
                if (fromUser) settings.speedLimit = limit
                showSpeed(limit)
            }

            override fun onStartTrackingTouch(bar: SeekBar) = Unit

            override fun onStopTrackingTouch(bar: SeekBar) = Unit
        })

        decreaseButton.setOnClickListener { changeConnections(-1) }
        increaseButton.setOnClickListener { changeConnections(1) }
        showConnections(settings.maxConcurrent)
    }

    private fun toggle(headerId: Int, body: View, chevron: ImageView, expand: Boolean) {
        body.visibility = if (expand) View.VISIBLE else View.GONE
        chevron.rotation = if (expand) 180f else 0f
        findViewById<View>(headerId).setOnClickListener {
            val opening = body.visibility != View.VISIBLE
            body.visibility = if (opening) View.VISIBLE else View.GONE
            chevron.animate().rotation(if (opening) 180f else 0f).setDuration(150).start()
        }
    }

    private fun showSpeed(limit: Long) {
        speedValue.text = if (limit <= 0) getString(R.string.speed_unlimited) else Formats.bytes(limit) + "/s"
    }

    private fun showConnections(value: Int) {
        connectionsValue.text = value.toString()
        decreaseButton.alpha = if (value <= 1) 0.45f else 1f
        increaseButton.alpha = if (value >= 8) 0.45f else 1f
    }

    private fun changeConnections(delta: Int) {
        val next = (settings.maxConcurrent + delta).coerceIn(1, 8)
        settings.maxConcurrent = next
        showConnections(next)
    }

    private fun nearestStep(limit: Long): Int {
        val index = Settings.SPEED_STEPS.indexOfFirst { it >= limit }
        if (index < 0) return Settings.SPEED_STEPS.lastIndex
        if (index == 0) return 0
        val lower = Settings.SPEED_STEPS[index - 1]
        val upper = Settings.SPEED_STEPS[index]
        return if (abs(limit - lower) <= abs(upper - limit)) index - 1 else index
    }
}
