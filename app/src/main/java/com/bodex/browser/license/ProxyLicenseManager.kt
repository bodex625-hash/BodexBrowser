package com.bodex.browser.license

import android.content.Context
import android.net.Uri
import android.provider.Settings
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest

object ProxyLicenseManager {
    enum class State { ACTIVE, OFFLINE_GRACE, NEEDS_CODE, INVALID }

    private const val API = "https://hb-browser-license.bodex625.workers.dev/api"
    private const val PROVIDER = "content://com.hb.browser.license"
    private const val PREFS = "proxy_license"
    private const val TOKEN = "license_token"
    private const val LAST_OK = "last_validation"
    private const val CHECK_INTERVAL = 24L * 60 * 60 * 1000
    private const val OFFLINE_GRACE = 72L * 60 * 60 * 1000
    private const val LEGACY_PACKAGE_ID = "com.hb.browser"

    private fun deviceId(context: Context): String {
        val androidId = Settings.Secure.getString(
            context.contentResolver,
            Settings.Secure.ANDROID_ID
        ) ?: "unknown"
        val raw = "$androidId|$LEGACY_PACKAGE_ID|HB-LICENSE-V1"
        return MessageDigest.getInstance("SHA-256").digest(raw.toByteArray())
            .joinToString("") { "%02x".format(it) }
    }

    private fun sharedToken(context: Context): String? {
        val bundle = context.contentResolver.call(
            Uri.parse(PROVIDER),
            "get_license",
            null,
            null
        ) ?: return null
        return bundle.getString("token")?.takeIf { it.isNotBlank() }
    }

    private fun validate(token: String, deviceId: String): Boolean {
        val body = JSONObject()
            .put("action", "validate")
            .put("token", token)
            .put("device_id", deviceId)
        val connection = (URL(API).openConnection() as HttpURLConnection).apply {
            requestMethod = "POST"
            connectTimeout = 10000
            readTimeout = 10000
            doOutput = true
            setRequestProperty("Content-Type", "application/json; charset=utf-8")
        }
        return try {
            connection.outputStream.use {
                it.write(body.toString().toByteArray(Charsets.UTF_8))
            }
            val stream = if (connection.responseCode in 200..299) {
                connection.inputStream
            } else {
                connection.errorStream
            }
            JSONObject(stream.bufferedReader().use { it.readText() }).optBoolean("ok")
        } finally {
            connection.disconnect()
        }
    }

    fun check(context: Context, forceNetwork: Boolean = false): State {
        val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val token = try {
            sharedToken(context)
        } catch (_: Exception) {
            null
        } ?: return State.NEEDS_CODE

        val previousToken = prefs.getString(TOKEN, null)
        if (previousToken != token) {
            prefs.edit().putString(TOKEN, token).putLong(LAST_OK, 0L).apply()
        }

        val lastOk = prefs.getLong(LAST_OK, 0L)
        val age = System.currentTimeMillis() - lastOk
        if (!forceNetwork && age in 0 until CHECK_INTERVAL) return State.ACTIVE

        return try {
            if (validate(token, deviceId(context))) {
                prefs.edit().putLong(LAST_OK, System.currentTimeMillis()).apply()
                State.ACTIVE
            } else {
                prefs.edit().remove(TOKEN).remove(LAST_OK).apply()
                State.INVALID
            }
        } catch (_: Exception) {
            if (lastOk > 0L && age in 0..OFFLINE_GRACE) State.OFFLINE_GRACE
            else State.INVALID
        }
    }

    fun isAllowed(state: State): Boolean =
        state == State.ACTIVE || state == State.OFFLINE_GRACE
}
