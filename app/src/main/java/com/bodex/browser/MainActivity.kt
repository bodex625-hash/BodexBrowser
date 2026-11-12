package com.bodex.browser

import android.app.Activity
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.VpnService
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.SettingsEthernet
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.bodex.browser.vpn.BodexVpnService

private val Navy = Color(0xFF0B1220)
private val Panel = Color(0xFF142238)
private val Cyan = Color(0xFF20C8F5)
private val Muted = Color(0xFF9FB1C8)

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent { HBProxyTheme { ProxyHome() } }
    }
}

@Composable
private fun HBProxyTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = darkColorScheme(
            primary = Cyan,
            background = Navy,
            surface = Panel,
            onBackground = Color.White,
            onSurface = Color.White
        ),
        content = content
    )
}

@Composable
private fun ProxyHome() {
    val context = androidx.compose.ui.platform.LocalContext.current
    val prefs = remember { context.getSharedPreferences("bodex_state", Context.MODE_PRIVATE) }

    var host by remember { mutableStateOf(prefs.getString("proxy_host", "") ?: "") }
    var port by remember { mutableStateOf(prefs.getString("proxy_port", "") ?: "") }
    var username by remember { mutableStateOf(prefs.getString("proxy_username", "") ?: "") }
    var password by remember { mutableStateOf(prefs.getString("proxy_password", "") ?: "") }
    var profileLabel by remember { mutableStateOf(prefs.getString("proxy_label", "My proxy") ?: "My proxy") }
    // Do not trust the last saved UI flag after Android has killed the VPN service.
    // A fresh START request re-attaches to an already-running service if it survived.
    var connected by remember { mutableStateOf(false) }
    var connectionMessage by remember { mutableStateOf("غير متصل") }
    var saved by remember { mutableStateOf(false) }
    var browserLaunched by remember { mutableStateOf(false) }

    fun persistSettings(
        newHost: String = host,
        newPort: String = port,
        newUsername: String = username,
        newPassword: String = password,
        newLabel: String = profileLabel
    ) {
        prefs.edit()
            .putString("proxy_host", newHost)
            .putString("proxy_port", newPort)
            .putString("proxy_username", newUsername)
            .putString("proxy_password", newPassword)
            .putString("proxy_label", newLabel)
            .apply()
        saved = newHost.isNotBlank() && (newPort.toIntOrNull()?.let { it in 1..65535 } == true)
    }

    val validConfig = host.isNotBlank() && (port.toIntOrNull()?.let { it in 1..65535 } == true)

    DisposableEffect(context) {
        val receiver = object : BroadcastReceiver() {
            override fun onReceive(ctx: Context?, intent: Intent?) {
                if (intent?.action == BodexVpnService.ACTION_STATE) {
                    connected = intent.getBooleanExtra(BodexVpnService.EXTRA_CONNECTED, false)
                    connectionMessage = intent.getStringExtra(BodexVpnService.EXTRA_MESSAGE)
                        ?: if (connected) "متصل" else "غير متصل"
                    prefs.edit()
                        .putBoolean("connected", connected)
                        .putString("message", connectionMessage)
                        .apply()
                    if (connected && !browserLaunched) {
                        browserLaunched = true
                        context.startActivity(Intent(context, BrowserActivity::class.java))
                    } else if (!connected) {
                        browserLaunched = false
                    }
                }
            }
        }
        val filter = IntentFilter(BodexVpnService.ACTION_STATE)
        if (Build.VERSION.SDK_INT >= 33) {
            context.registerReceiver(receiver, filter, Context.RECEIVER_NOT_EXPORTED)
        } else {
            @Suppress("DEPRECATION")
            context.registerReceiver(receiver, filter)
        }
        onDispose { context.unregisterReceiver(receiver) }
    }

    val startVpn: () -> Unit = {
        persistSettings()
        val serviceIntent = Intent(context, BodexVpnService::class.java).apply {
            action = BodexVpnService.ACTION_START
            putExtra(BodexVpnService.EXTRA_HOST, host.trim())
            putExtra(BodexVpnService.EXTRA_PORT, port.toIntOrNull() ?: 0)
            putExtra(BodexVpnService.EXTRA_USER, username)
            putExtra(BodexVpnService.EXTRA_PASS, password)
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            context.startForegroundService(serviceIntent)
        } else {
            context.startService(serviceIntent)
        }
        connectionMessage = "تم إرسال طلب الاتصال؛ جارٍ التحقق من VPN"
    }

    val vpnPermission = rememberLauncherForActivityResult(
        ActivityResultContracts.StartActivityForResult()
    ) { result ->
        if (result.resultCode == Activity.RESULT_OK) startVpn()
    }

    Surface(modifier = Modifier.fillMaxSize(), color = Navy) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(20.dp),
            verticalArrangement = Arrangement.spacedBy(18.dp)
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(
                    modifier = Modifier
                        .size(48.dp)
                        .background(Cyan.copy(alpha = 0.14f), RoundedCornerShape(14.dp)),
                    contentAlignment = Alignment.Center
                ) {
                    Icon(
                        Icons.Default.SettingsEthernet,
                        contentDescription = null,
                        tint = Cyan,
                        modifier = Modifier.size(29.dp)
                    )
                }
                Spacer(Modifier.width(12.dp))
                Column {
                    Text("H.B Proxy", color = Color.White, fontSize = 25.sp, fontWeight = FontWeight.Black)
                    Text("SOCKS5 • VPN TUNNEL", color = Muted, fontSize = 11.sp, letterSpacing = 1.4.sp)
                }
                Spacer(Modifier.weight(1f))
                Text("v0.2.0", color = Muted, fontSize = 12.sp)
            }

            Card(
                colors = CardDefaults.cardColors(containerColor = Panel),
                shape = RoundedCornerShape(22.dp)
            ) {
                Column(
                    Modifier.fillMaxWidth().padding(18.dp),
                    horizontalAlignment = Alignment.CenterHorizontally
                ) {
                    Text("حالة الاتصال", color = Muted)
                    Spacer(Modifier.height(8.dp))
                    Text(
                        connectionMessage,
                        color = if (connected) Color(0xFF7FE0B0) else Color(0xFFFFC266),
                        fontSize = 22.sp,
                        fontWeight = FontWeight.Bold
                    )
                    Text("اتصال SOCKS5 عبر VPN للهاتف بالكامل", color = Muted, fontSize = 12.sp)
                    Spacer(Modifier.height(16.dp))
                    Row(
                        Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(10.dp)
                    ) {
                        Button(
                            onClick = {
                                val permission = VpnService.prepare(context)
                                if (permission == null) startVpn() else vpnPermission.launch(permission)
                            },
                            enabled = !connected && validConfig,
                            modifier = Modifier.weight(1f)
                        ) { Text("تشغيل البروكسي") }

                        OutlinedButton(
                            onClick = {
                                val stop = Intent(context, BodexVpnService::class.java)
                                    .setAction(BodexVpnService.ACTION_STOP)
                                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                                    context.startForegroundService(stop)
                                } else {
                                    context.startService(stop)
                                }
                                connected = false
                                connectionMessage = "جارٍ إيقاف البروكسي"
                            },
                            enabled = connected,
                            modifier = Modifier.weight(1f)
                        ) { Text("إيقاف البروكسي") }
                    }
                    if (connected) {
                        OutlinedButton(
                            onClick = { context.startActivity(Intent(context, BrowserActivity::class.java)) },
                            modifier = Modifier.fillMaxWidth()
                        ) { Text("فتح المتصفح") }
                    }
                }
            }

            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Default.SettingsEthernet, contentDescription = null, tint = Cyan)
                Spacer(Modifier.width(8.dp))
                Text("إعداد البروكسي", color = Color.White, fontWeight = FontWeight.Bold, fontSize = 18.sp)
            }

            Card(
                colors = CardDefaults.cardColors(containerColor = Panel),
                shape = RoundedCornerShape(18.dp)
            ) {
                Column(
                    Modifier.padding(16.dp),
                    verticalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    OutlinedTextField(
                        value = profileLabel,
                        onValueChange = {
                            profileLabel = it
                            persistSettings(newLabel = it)
                        },
                        label = { Text("اسم الملف") },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth()
                    )
                    Text("نوع البروكسي: SOCKS5", color = Cyan, fontWeight = FontWeight.Bold)
                    OutlinedTextField(
                        value = host,
                        onValueChange = {
                            host = it
                            persistSettings(newHost = it)
                        },
                        label = { Text("Server / IP") },
                        placeholder = { Text("proxy.example.com") },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth()
                    )
                    OutlinedTextField(
                        value = port,
                        onValueChange = {
                            val value = it.filter(Char::isDigit).take(5)
                            port = value
                            persistSettings(newPort = value)
                        },
                        label = { Text("Port") },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth()
                    )
                    OutlinedTextField(
                        value = username,
                        onValueChange = {
                            username = it
                            persistSettings(newUsername = it)
                        },
                        label = { Text("Username (اختياري)") },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth()
                    )
                    OutlinedTextField(
                        value = password,
                        onValueChange = {
                            password = it
                            persistSettings(newPassword = it)
                        },
                        label = { Text("Password (اختياري)") },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth()
                    )
                    Button(
                        onClick = { persistSettings() },
                        enabled = validConfig,
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Text("حفظ إعدادات البروكسي")
                    }
                    if (saved && validConfig) {
                        Text(
                            "تم حفظ بيانات البروكسي تلقائياً على هذا الجهاز",
                            color = Color(0xFF7FE0B0),
                            fontSize = 12.sp
                        )
                    } else {
                        Text(
                            "أدخل عنوان الخادم والمنفذ الصحيحين. تُحفظ التعديلات تلقائياً.",
                            color = Muted,
                            fontSize = 12.sp
                        )
                    }
                }
            }

            Text(
                "H.B Proxy • SOCKS5 VPN",
                color = Muted,
                fontSize = 11.sp,
                modifier = Modifier.align(Alignment.CenterHorizontally)
            )
        }
    }
}
