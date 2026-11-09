package com.bodex.browser.vpn

import android.net.VpnService
import android.content.Intent
import android.util.Log

/**
 * Android VPN entry point. This is intentionally a scaffold only.
 * Do not call startService() and present the VPN as connected until a tested
 * TUN-to-proxy engine (SOCKS5 + HTTP) is integrated and packet forwarding works.
 */
class BodexVpnService : VpnService() {
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        Log.i(TAG, "VPN service scaffold started; tunnel engine is not integrated yet")
        stopSelf(startId)
        return START_NOT_STICKY
    }

    companion object {
        private const val TAG = "BodexVpnService"
    }
}
