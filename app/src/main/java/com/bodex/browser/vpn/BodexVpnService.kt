package com.bodex.browser.vpn

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Intent
import android.net.VpnService
import android.os.Build
import android.os.ParcelFileDescriptor
import android.util.Log
import hev.htproxy.TProxyService
import java.io.File
import java.io.IOException

class BodexVpnService : VpnService() {
    private var tun: ParcelFileDescriptor? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP -> stopTunnel()
            ACTION_START -> {
                val host = intent.getStringExtra(EXTRA_HOST).orEmpty()
                val port = intent.getIntExtra(EXTRA_PORT, 0)
                val user = intent.getStringExtra(EXTRA_USER).orEmpty()
                val pass = intent.getStringExtra(EXTRA_PASS).orEmpty()
                if (host.isBlank() || port !in 1..65535) {
                    stopSelf()
                    return START_NOT_STICKY
                }
                startTunnel(host, port, user, pass)
            }
        }
        return START_NOT_STICKY
    }

    private fun startTunnel(host: String, port: Int, user: String, pass: String) {
        if (tun != null) return
        try {
            createNotificationChannel()
            val notification = Notification.Builder(this, CHANNEL_ID)
                .setContentTitle("Bodex Browser")
                .setContentText("جاري تشغيل اتصال SOCKS5")
                .setSmallIcon(android.R.drawable.stat_sys_warning)
                .setOngoing(true)
                .build()
            if (Build.VERSION.SDK_INT >= 34) {
                startForeground(NOTIFICATION_ID, notification, android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
            } else {
                startForeground(NOTIFICATION_ID, notification)
            }

            val builder = Builder()
                .setSession("Bodex SOCKS5")
                .setMtu(1500)
                .setBlocking(false)
                .addAddress("198.18.0.1", 15)
                .addRoute("0.0.0.0", 0)
                .addDnsServer("1.1.1.1")
                .addDisallowedApplication(packageName)

            tun = builder.establish() ?: throw IllegalStateException("تعذر إنشاء واجهة VPN")
            val config = File(cacheDir, "bodex-socks5.conf")
            config.writeText(buildString {
                append("tunnel:\n")
                append("  mtu: 1500\n")
                append("  ipv4: 198.18.0.1\n")
                append("socks5:\n")
                append("  address: \"").append(yaml(host)).append("\"\n")
                append("  port: ").append(port).append("\n")
                append("  udp: 'udp'\n")
                if (user.isNotEmpty() && pass.isNotEmpty()) {
                    append("  username: \"").append(yaml(user)).append("\"\n")
                    append("  password: \"").append(yaml(pass)).append("\"\n")
                }
            })
            if (!TProxyService.TProxyStartService(config.absolutePath, tun!!.fd)) {
                throw IllegalStateException("محرك SOCKS5 لم يبدأ")
            }
            Log.i(TAG, "SOCKS5 tunnel started")
            broadcastState(true, "متصل عبر SOCKS5")
        } catch (e: Exception) {
            Log.e(TAG, "VPN start failed", e)
            stopTunnel()
            broadcastState(false, e.message ?: "فشل تشغيل البروكسي")
        }
    }

    private fun stopTunnel() {
        try { if (TProxyService.TProxyIsRunning()) TProxyService.TProxyStopService() } catch (_: Throwable) {}
        try { tun?.close() } catch (_: IOException) {}
        tun = null
        if (Build.VERSION.SDK_INT >= 24) stopForeground(STOP_FOREGROUND_REMOVE) else @Suppress("DEPRECATION") stopForeground(true)
        broadcastState(false, "غير متصل")
        stopSelf()
    }

    private fun broadcastState(connected: Boolean, message: String) {
        sendBroadcast(Intent(ACTION_STATE).setPackage(packageName)
            .putExtra(EXTRA_CONNECTED, connected).putExtra(EXTRA_MESSAGE, message))
    }

    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val manager = getSystemService(NotificationManager::class.java)
            manager.createNotificationChannel(NotificationChannel(CHANNEL_ID, "Bodex VPN", NotificationManager.IMPORTANCE_LOW))
        }
    }

    private fun yaml(value: String) = value.replace("\\", "\\\\").replace("\"", "\\\"").replace("\n", "")
    override fun onRevoke() { stopTunnel(); super.onRevoke() }
    override fun onDestroy() { stopTunnel(); super.onDestroy() }

    companion object {
        const val ACTION_START = "com.bodex.browser.vpn.START"
        const val ACTION_STOP = "com.bodex.browser.vpn.STOP"
        const val ACTION_STATE = "com.bodex.browser.vpn.STATE"
        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"
        const val EXTRA_USER = "user"
        const val EXTRA_PASS = "pass"
        const val EXTRA_CONNECTED = "connected"
        const val EXTRA_MESSAGE = "message"
        private const val CHANNEL_ID = "bodex_vpn"
        private const val NOTIFICATION_ID = 41
        private const val TAG = "BodexVpnService"
    }
}
