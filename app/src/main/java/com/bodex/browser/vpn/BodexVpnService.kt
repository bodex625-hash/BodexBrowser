package com.bodex.browser.vpn

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Intent
import android.net.VpnService
import android.os.Build
import android.os.Handler
import android.os.Looper
import com.bodex.browser.license.ProxyLicenseManager
import java.util.concurrent.Executors
import android.os.ParcelFileDescriptor
import android.util.Log
import hev.htproxy.TProxyService
import java.io.File
import java.io.IOException
import java.net.Inet4Address
import java.net.InetAddress

class BodexVpnService : VpnService() {
    private var tun: ParcelFileDescriptor? = null
    private val worker = Executors.newSingleThreadExecutor()
    private val monitorHandler = Handler(Looper.getMainLooper())
    private val licenseMonitor = object : Runnable {
        override fun run() {
            worker.execute {
                val state = ProxyLicenseManager.check(this@BodexVpnService)
                if (!ProxyLicenseManager.isAllowed(state)) {
                    stopTunnelWithMessage("انتهت صلاحية الاشتراك أو تم إيقافه")
                } else {
                    monitorHandler.postDelayed(this, 60L * 1000)
                }
            }
        }
    }

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
                enterForeground()
                worker.execute {
                    val licenseState = ProxyLicenseManager.check(this, forceNetwork = true)
                    if (ProxyLicenseManager.isAllowed(licenseState)) {
                        startTunnel(host, port, user, pass)
                        if (tun != null) {
                            monitorHandler.removeCallbacks(licenseMonitor)
                            monitorHandler.postDelayed(licenseMonitor, 60L * 1000)
                        }
                    } else {
                        stopTunnelWithMessage(
                            if (licenseState == ProxyLicenseManager.State.NEEDS_CODE)
                                "فعّل الاشتراك من HB Browser أولاً"
                            else "الاشتراك غير صالح أو منتهي"
                        )
                    }
                }
            }
        }
        return START_NOT_STICKY
    }

    private fun startTunnel(host: String, port: Int, user: String, pass: String) {
        if (tun != null) return
        try {
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
                // This is a proxy-only app: exclude its own sockets so the
                // upstream SOCKS5 connection cannot loop back into this TUN.
                .addDisallowedApplication(packageName)
                .addRoute("0.0.0.0", 0)
                .addRoute("::", 0)
                // Use HEV's mapped DNS endpoint; DNS is resolved through SOCKS5.
                .addDnsServer("198.18.0.2")

            tun = builder.establish() ?: throw IllegalStateException("تعذر إنشاء واجهة VPN")
            val config = File(cacheDir, "bodex-socks5.conf")
            config.writeText(buildString {
                append("tunnel:\n")
                append("  mtu: 1500\n")
                append("  ipv4: 198.18.0.1\n")
                append("  ipv6: 'fc00::1'\n")
                append("socks5:\n")
                append("  address: \"").append(yaml(proxyAddress.hostAddress)).append("\"\n")
                append("  port: ").append(port).append("\n")
                append("  udp: 'udp'\n")
                if (user.isNotEmpty() && pass.isNotEmpty()) {
                    append("  username: \"").append(yaml(user)).append("\"\n")
                    append("  password: \"").append(yaml(pass)).append("\"\n")
                }
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
            Log.i(TAG, "SOCKS5 tunnel started; upstream=$host:$port")
            broadcastState(true, "متصل عبر SOCKS5")
        } catch (e: Exception) {
            Log.e(TAG, "VPN start failed", e)
            stopTunnel()
            broadcastState(false, e.message ?: "فشل تشغيل البروكسي")
        }
    }

    private fun enterForeground() {
        createNotificationChannel()
        val notification = Notification.Builder(this, CHANNEL_ID)
            .setContentTitle("H.B Proxy")
            .setContentText("جاري التحقق من الاشتراك وتشغيل SOCKS5")
            .setSmallIcon(android.R.drawable.stat_sys_warning)
            .setOngoing(true)
            .build()
        if (Build.VERSION.SDK_INT >= 34) {
            startForeground(NOTIFICATION_ID, notification, android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
    }

    private fun stopTunnelWithMessage(message: String) {
        stopTunnel()
        broadcastState(false, message)
    }

    private fun stopTunnel() {
        monitorHandler.removeCallbacks(licenseMonitor)
        try { if (TProxyService.TProxyIsRunning()) TProxyService.TProxyStopService() } catch (_: Throwable) {}
        try { tun?.close() } catch (_: IOException) {}
        tun = null
        if (Build.VERSION.SDK_INT >= 24) stopForeground(STOP_FOREGROUND_REMOVE) else @Suppress("DEPRECATION") stopForeground(true)
        broadcastState(false, "غير متصل")
        stopSelf()
    }

    private fun broadcastState(connected: Boolean, message: String) {
        val stateIntent = Intent(ACTION_STATE)
            .putExtra(EXTRA_CONNECTED, connected)
            .putExtra(EXTRA_MESSAGE, message)
        sendBroadcast(Intent(stateIntent).setPackage(packageName))
        // Notify the separately packaged browser so it closes when the VPN drops.
        sendBroadcast(Intent(stateIntent).setPackage("com.hb.browser"))
    }

    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val manager = getSystemService(NotificationManager::class.java)
            manager.createNotificationChannel(NotificationChannel(CHANNEL_ID, "H.B Proxy", NotificationManager.IMPORTANCE_LOW))
        }
    }

    private fun yaml(value: String) = value.replace("\\", "\\\\").replace("\"", "\\\"").replace("\n", "")
    override fun onRevoke() { stopTunnel(); super.onRevoke() }
    override fun onDestroy() {
        monitorHandler.removeCallbacks(licenseMonitor)
        worker.shutdownNow()
        stopTunnel()
        super.onDestroy()
    }

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
