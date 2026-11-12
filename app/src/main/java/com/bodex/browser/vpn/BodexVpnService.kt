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
import java.net.Inet4Address
import java.net.InetAddress

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
        if (tun != null) {
            val tunnelAlive = try { TProxyService.TProxyIsRunning() } catch (_: Throwable) { false }
            if (tunnelAlive && LocalSocks5Relay.isRunning()) {
                broadcastState(true, "متصل عبر SOCKS5")
                return
            }
            stopTunnel()
        }
        try {
            createNotificationChannel()
            val notification = Notification.Builder(this, CHANNEL_ID)
                .setContentTitle("H.B Proxy")
                .setContentText("جاري تشغيل اتصال SOCKS5")
                .setSmallIcon(android.R.drawable.stat_sys_warning)
                .setOngoing(true)
                .build()
            if (Build.VERSION.SDK_INT >= 34) {
                startForeground(NOTIFICATION_ID, notification, android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
            } else {
                startForeground(NOTIFICATION_ID, notification)
            }

            // Resolve the upstream SOCKS5 server before installing VPN routes.
            val proxyAddresses = InetAddress.getAllByName(host).distinctBy { it.hostAddress }
            if (proxyAddresses.isEmpty()) {
                throw IllegalArgumentException("تعذر تحديد عنوان خادم SOCKS5")
            }
            val proxyAddress = proxyAddresses.firstOrNull { it is Inet4Address } ?: proxyAddresses.first()

            val builder = Builder()
                .setSession("H.B Proxy SOCKS5")
                .setMtu(1500)
                .setBlocking(false)
                .addAddress("198.18.0.1", 15)
                .addAddress("fc00::1", 128)
                // Include this app in the TUN so Chromium TCP and UDP are
                // both routed through HEV. The local SOCKS bridge protects its
                // upstream TCP/UDP sockets before they reach the real proxy.
                .addRoute("0.0.0.0", 0)
                .addRoute("::", 0)
                // Use HEV's mapped DNS endpoint; DNS is resolved through SOCKS5.
                .addDnsServer("198.18.0.2")

            tun = builder.establish() ?: throw IllegalStateException("تعذر إنشاء واجهة VPN")
            // The HEV engine connects only to the loopback bridge. The bridge
            // authenticates to the real upstream SOCKS5 server and protects
            // its TCP and UDP sockets from re-entering this VPN.
            if (!LocalSocks5Relay.start(proxyAddress.hostAddress ?: host, port, user, pass, this)) {
                throw IllegalStateException("تعذر تشغيل وسيط SOCKS5 المحلي")
            }

            val config = File(cacheDir, "bodex-socks5.conf")
            config.writeText(buildString {
                append("tunnel:\n")
                append("  mtu: 1500\n")
                append("  ipv4: 198.18.0.1\n")
                append("  ipv6: 'fc00::1'\n")
                append("socks5:\n")
                append("  address: \"127.0.0.1\"\n")
                append("  port: ").append(LocalSocks5Relay.PORT).append("\n")
                append("  udp: 'udp'\n")
                append("mapdns:\n")
                append("  address: 198.18.0.2\n")
                append("  port: 53\n")
                append("  network: 240.0.0.0\n")
                append("  netmask: 240.0.0.0\n")
                append("  cache-size: 10000\n")
                append("misc:\n")
                append("  log-file: \"").append(yaml(File(cacheDir, "hev-tunnel.log").absolutePath)).append("\"\n")
                append("  log-level: debug\n")
            })
            if (!TProxyService.TProxyStartService(config.absolutePath, tun!!.fd)) {
                throw IllegalStateException("محرك SOCKS5 لم يبدأ")
            }
            Log.i(TAG, "SOCKS5 tunnel and local Chromium relay started; upstream=$host:$port")
            broadcastState(true, "متصل عبر SOCKS5")
        } catch (e: Exception) {
            Log.e(TAG, "VPN start failed", e)
            stopTunnel()
            broadcastState(false, e.message ?: "فشل تشغيل البروكسي")
        }
    }

    private fun stopTunnel() {
        try { if (TProxyService.TProxyIsRunning()) TProxyService.TProxyStopService() } catch (_: Throwable) {}
        try { LocalSocks5Relay.stop() } catch (_: Throwable) {}
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
            manager.createNotificationChannel(NotificationChannel(CHANNEL_ID, "H.B Proxy", NotificationManager.IMPORTANCE_LOW))
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
