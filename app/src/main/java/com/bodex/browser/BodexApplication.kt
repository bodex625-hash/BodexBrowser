package com.bodex.browser

import android.app.Application
import android.content.Context
import com.cefrium.Cefrium

class BodexApplication : Application() {
    override fun attachBaseContext(base: Context) {
        // Disable the engine's component updater before its ContentProvider
        // initializes Chromium; all browsing requests must use the fixed proxy.
        super.attachBaseContext(base)
        Cefrium.setComponentUpdatesEnabled(false)
    }
}
