package com.velocity.downloader

import android.app.Application
import com.velocity.downloader.core.Http

class VelocityApp : Application() {
    override fun onCreate() {
        super.onCreate()
        // Cookies set by a sign-in in the browser (and by redirects) are kept for the download connections.
        Http.installCookies()
    }
}
