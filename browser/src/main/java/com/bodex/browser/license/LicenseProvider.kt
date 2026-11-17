package com.bodex.browser.license

import android.content.ContentProvider
import android.content.ContentValues
import android.content.pm.PackageManager
import android.database.Cursor
import android.net.Uri
import android.os.Bundle

class LicenseProvider : ContentProvider() {
    override fun onCreate(): Boolean = true

    override fun call(method: String, arg: String?, extras: Bundle?): Bundle {
        val ctx = context ?: return Bundle()
        if (ctx.checkCallingPermission(READ_PERMISSION) != PackageManager.PERMISSION_GRANTED) {
            throw SecurityException("License access requires the shared app signature")
        }
        if (method != METHOD_GET_LICENSE) return Bundle()
        val prefs = ctx.getSharedPreferences("hb_license", android.content.Context.MODE_PRIVATE)
        return Bundle().apply {
            putString("token", prefs.getString("license_token", null))
            putLong("last_validation", prefs.getLong("last_validation", 0L))
        }
    }

    override fun query(uri: Uri, projection: Array<out String>?, selection: String?, selectionArgs: Array<out String>?, sortOrder: String?): Cursor? = null
    override fun getType(uri: Uri): String? = null
    override fun insert(uri: Uri, values: ContentValues?): Uri? = null
    override fun delete(uri: Uri, selection: String?, selectionArgs: Array<out String>?): Int = 0
    override fun update(uri: Uri, values: ContentValues?, selection: String?, selectionArgs: Array<out String>?): Int = 0

    companion object {
        const val METHOD_GET_LICENSE = "get_license"
        private const val READ_PERMISSION = "com.hb.browser.permission.READ_LICENSE"
    }
}