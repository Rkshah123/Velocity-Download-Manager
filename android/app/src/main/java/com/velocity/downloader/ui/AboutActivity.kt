package com.velocity.downloader.ui

import android.os.Bundle
import android.view.View
import android.widget.TextView
import com.velocity.downloader.R

class AboutActivity : ThemedActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_about)

        extendAppBar(findViewById(R.id.appBar))
        avoidNavigationBar(findViewById(R.id.content))

        findViewById<View>(R.id.backButton).setOnClickListener { finish() }
        findViewById<TextView>(R.id.versionValue).text = versionName()
    }

    private fun versionName(): String = try {
        @Suppress("DEPRECATION")
        packageManager.getPackageInfo(packageName, 0).versionName ?: "1.0.0"
    } catch (exception: Exception) {
        "1.0.0"
    }
}
