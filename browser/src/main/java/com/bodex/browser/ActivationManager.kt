package com.bodex.browser

import android.content.Context
import android.provider.Settings
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest

object ActivationManager {
    private const val PREFS = "hb_license"
    private const val TOKEN = "license_token"
    private const val LAST_OK = "last_validation"
    private const val CHECK_INTERVAL = 24L * 60 * 60 * 1000
    private const val OFFLINE_GRACE = 72L * 60 * 60 * 1000

    // Keep the original HB Browser identifier so existing issued codes remain
    // bound to the same Android device after the package is integrated.
    private const val LEGACY_PACKAGE_ID = "com.hb.browser"

    enum class State { ACTIVE, OFFLINE_GRACE, NEEDS_CODE, INVALID, SERVER_NOT_CONFIGURED }

    private fun deviceId(context: Context): String {
        val androidId = Settings.Secure.getString(
            context.contentResolver,
            Settings.Secure.ANDROID_ID
        ) ?: "unknown"
        val raw = "$androidId|$LEGACY_PACKAGE_ID|HB-LICENSE-V1"
        return MessageDigest.getInstance("SHA-256").digest(raw.toByteArray())
            .joinToString("") { "%02x".format(it) }
    }

    private fun request(action: String, values: JSONObject): JSONObject {
        val base = BuildConfig.HB_LICENSE_API
        if (base.contains("YOUR-DOMAIN")) {
            throw IllegalStateException("SERVER_NOT_CONFIGURED")
        }
        val body = JSONObject(values.toString()).put("action", action)
        val connection = (URL(base).openConnection() as HttpURLConnection).apply {
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
            JSONObject(stream.bufferedReader().use { it.readText() })
        } finally {
            connection.disconnect()
        }
    }

    fun check(context: Context): State {
        val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val token = prefs.getString(TOKEN, null) ?: return State.NEEDS_CODE
        val lastOk = prefs.getLong(LAST_OK, 0L)
        val age = System.currentTimeMillis() - lastOk
        if (age in 0 until CHECK_INTERVAL) return State.ACTIVE

        return try {
            val result = request(
                "validate",
                JSONObject().put("token", token).put("device_id", deviceId(context))
            )
            if (result.optBoolean("ok")) {
                prefs.edit().putLong(LAST_OK, System.currentTimeMillis()).apply()
                State.ACTIVE
            } else {
                prefs.edit().remove(TOKEN).remove(LAST_OK).apply()
                State.INVALID
            }
        } catch (e: IllegalStateException) {
            State.SERVER_NOT_CONFIGURED
        } catch (_: Exception) {
            if (lastOk > 0 && age in 0..OFFLINE_GRACE) State.OFFLINE_GRACE else State.INVALID
        }
    }

    fun activate(context: Context, code: String): String {
        val result = request(
            "activate",
            JSONObject().put("code", code.trim().uppercase()).put("device_id", deviceId(context))
        )
        if (!result.optBoolean("ok")) {
            throw IllegalArgumentException(result.optString("message", "الكود غير صالح"))
        }
        val token = result.optString("token")
        if (token.isBlank()) throw IllegalStateException("لم يصل رمز التفعيل من الخادم")

        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
            .putString(TOKEN, token)
            .putLong(LAST_OK, System.currentTimeMillis())
            .apply()
        return result.optString("expires_at", "اشتراك فعّال")
    }
}
