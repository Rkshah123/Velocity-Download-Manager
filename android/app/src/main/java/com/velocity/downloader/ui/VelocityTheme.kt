package com.velocity.downloader.ui

import android.content.Context
import android.graphics.Color
import android.view.View
import androidx.annotation.AttrRes
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import android.os.Bundle
import com.velocity.downloader.R
import com.velocity.downloader.core.Settings

/** The two themes the app is allowed to use. Nothing else defines colours. */
object VelocityTheme {
    const val DARK = Settings.THEME_DARK
    const val BEIGE = Settings.THEME_BEIGE

    fun styleRes(theme: String): Int =
        if (theme == BEIGE) R.style.Theme_Velocity_Beige else R.style.Theme_Velocity_Dark

    fun label(theme: String): String = if (theme == BEIGE) "Beige" else "Dark Jet Black"

    fun color(context: Context, @AttrRes attr: Int): Int {
        val typed = context.obtainStyledAttributes(intArrayOf(attr))
        return try {
            typed.getColor(0, Color.WHITE)
        } finally {
            typed.recycle()
        }
    }
}

/**
 * Applies the saved theme before any view is inflated and rebuilds the screen when the user changes it, so no screen is
 * left with the colours of the other theme.
 */
abstract class ThemedActivity : AppCompatActivity() {

    private var appliedTheme: String = VelocityTheme.DARK

    override fun onCreate(savedInstanceState: Bundle?) {
        appliedTheme = Settings.get(this).theme
        setTheme(VelocityTheme.styleRes(appliedTheme))
        super.onCreate(savedInstanceState)
    }

    override fun onResume() {
        super.onResume()
        if (Settings.get(this).theme != appliedTheme) recreate()
    }

    /** Dark status-bar icons when the area behind them is light (the Beige Home screen). */
    protected fun lightStatusBarIcons(light: Boolean) {
        WindowInsetsControllerCompat(window, window.decorView).isAppearanceLightStatusBars = light
    }

    /** Lets the coloured app bar sit behind the status bar, as in the reference screens. */
    protected fun extendAppBar(bar: View) {
        val base = bar.paddingTop
        ViewCompat.setOnApplyWindowInsetsListener(bar) { view, insets ->
            val top = insets.getInsets(WindowInsetsCompat.Type.systemBars()).top
            view.setPadding(view.paddingLeft, base + top, view.paddingRight, view.paddingBottom)
            insets
        }
    }

    /** Keeps the last row of a scrolling screen above the gesture bar. */
    protected fun avoidNavigationBar(content: View) {
        val base = content.paddingBottom
        ViewCompat.setOnApplyWindowInsetsListener(content) { view, insets ->
            val bottom = insets.getInsets(WindowInsetsCompat.Type.systemBars()).bottom
            view.setPadding(view.paddingLeft, view.paddingTop, view.paddingRight, base + bottom)
            insets
        }
    }
}
