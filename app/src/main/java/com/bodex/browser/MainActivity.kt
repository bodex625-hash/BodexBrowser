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
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.SettingsEthernet
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.snapshots.SnapshotStateList
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.bodex.browser.vpn.BodexVpnService
import com.bodex.browser.license.ProxyLicenseManager
import android.widget.Toast
import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID

private val Navy = Color(0xFF0B1220)
private val Panel = Color(0xFF142238)
private val Cyan = Color(0xFF20C8F5)
private val Muted = Color(0xFF9FB1C8)

private data class ProxyProfile(
    val id: String,
    val name: String,
    val host: String,
    val port: String,
    val username: String,
    val password: String
)

private fun readProxyProfiles(prefs: android.content.SharedPreferences): List<ProxyProfile> {
    val raw = prefs.getString("proxy_profiles_json", null)
    if (!raw.isNullOrBlank()) {
        return try {
            val array = JSONArray(raw)
            (0 until array.length()).mapNotNull { index ->
                val item = array.optJSONObject(index) ?: return@mapNotNull null
                ProxyProfile(
                    id = item.optString("id").ifBlank { UUID.randomUUID().toString() },
                    name = item.optString("name", "Proxy"),
                    host = item.optString("host"),
                    port = item.optString("port"),
                    username = item.optString("username"),
                    password = item.optString("password")
                )
            }
        } catch (_: Exception) { emptyList() }
    }
    // One-time migration: preserve the existing single-proxy configuration.
    val host = prefs.getString("proxy_host", "").orEmpty()
    val port = prefs.getString("proxy_port", "").orEmpty()
    if (host.isNotBlank() && port.isNotBlank()) {
        return listOf(
            ProxyProfile(
                id = UUID.randomUUID().toString(),
                name = prefs.getString("proxy_label", "My proxy") ?: "My proxy",
                host = host,
                port = port,
                username = prefs.getString("proxy_username", "").orEmpty(),
                password = prefs.getString("proxy_password", "").orEmpty()
            )
        )
    }
    return emptyList()
}

private fun writeProxyProfiles(
    prefs: android.content.SharedPreferences,
    profiles: List<ProxyProfile>,
    selectedId: String?
) {
    val array = JSONArray()
    profiles.forEach { profile ->
        array.put(
            JSONObject()
                .put("id", profile.id)
                .put("name", profile.name)
                .put("host", profile.host)
                .put("port", profile.port)
                .put("username", profile.username)
                .put("password", profile.password)
        )
    }
    prefs.edit()
        .putString("proxy_profiles_json", array.toString())
        .putString("selected_proxy_id", selectedId)
        .apply()
}

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
    val profiles = remember { mutableStateListOf<ProxyProfile>().apply { addAll(readProxyProfiles(prefs)) } }

    var selectedId by remember {
        mutableStateOf(
            prefs.getString("selected_proxy_id", null)
                ?: profiles.firstOrNull()?.id
        )
    }
    var host by remember { mutableStateOf("") }
    var port by remember { mutableStateOf("") }
    var username by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var profileLabel by remember { mutableStateOf("") }
    var editingId by remember { mutableStateOf<String?>(null) }
    var connected by remember { mutableStateOf(prefs.getBoolean("connected", false)) }
    var connectionMessage by remember {
        mutableStateOf(prefs.getString("message", "غير متصل") ?: "غير متصل")
    }
    var saved by remember { mutableStateOf(false) }

    fun loadIntoEditor(profile: ProxyProfile) {
        selectedId = profile.id
        editingId = profile.id
        profileLabel = profile.name
        host = profile.host
        port = profile.port
        username = profile.username
        password = profile.password
        saved = true
        writeProxyProfiles(prefs, profiles, selectedId)
    }

    fun startNewProfile() {
        selectedId = null
        editingId = null
        profileLabel = ""
        host = ""
        port = ""
        username = ""
        password = ""
        saved = false
    }

    val validConfig = host.isNotBlank() &&
        (port.toIntOrNull()?.let { it in 1..65535 } == true) &&
        profileLabel.isNotBlank()

    fun saveProfile() {
        if (!validConfig) return
        val id = editingId ?: UUID.randomUUID().toString()
        val profile = ProxyProfile(
            id = id,
            name = profileLabel.trim(),
            host = host.trim(),
            port = port,
            username = username,
            password = password
        )
        val index = profiles.indexOfFirst { it.id == id }
        if (index >= 0) profiles[index] = profile else profiles.add(profile)
        selectedId = id
        editingId = id
        writeProxyProfiles(prefs, profiles, selectedId)
        saved = true
        Toast.makeText(context, "تم حفظ البروكسي ضمن القائمة", Toast.LENGTH_SHORT).show()
    }

    fun deleteProfile(profile: ProxyProfile) {
        if (connected && prefs.getString("active_proxy_id", null) == profile.id) {
            Toast.makeText(context, "أوقف الاتصال قبل حذف البروكسي المستخدم حالياً", Toast.LENGTH_LONG).show()
            return
        }
        profiles.removeAll { it.id == profile.id }
        if (selectedId == profile.id) {
            selectedId = profiles.firstOrNull()?.id
            val next = profiles.firstOrNull()
            if (next != null) loadIntoEditor(next) else startNewProfile()
        }
        writeProxyProfiles(prefs, profiles, selectedId)
    }

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
                    if (connected) {
                        val browserIntent = context.packageManager.getLaunchIntentForPackage("com.hb.browser")
                        browserIntent?.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                        if (browserIntent != null) context.startActivity(browserIntent)
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
        if (saved && validConfig) {
            prefs.edit().putString("active_proxy_id", selectedId).apply()
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
            connectionMessage = if (connected) "جارٍ تبديل البروكسي..." else "تم إرسال طلب الاتصال؛ جارٍ التحقق من VPN"
        }
    }

    val vpnPermission = rememberLauncherForActivityResult(
        ActivityResultContracts.StartActivityForResult()
    ) { result ->
        if (result.resultCode == Activity.RESULT_OK) startVpn()
    }

    val requestStart: () -> Unit = {
        Thread {
            val licenseState = ProxyLicenseManager.check(context)
            (context as? Activity)?.runOnUiThread {
                if (ProxyLicenseManager.isAllowed(licenseState)) {
                    val permission = VpnService.prepare(context)
                    if (permission == null) startVpn() else vpnPermission.launch(permission)
                } else {
                    try {
                        val activation = Intent().setClassName(
                            "com.hb.browser",
                            "com.bodex.browser.ActivationActivity"
                        )
                        context.startActivity(activation)
                    } catch (_: Exception) {
                        Toast.makeText(
                            context,
                            "يجب تثبيت HB Browser لتفعيل الاشتراك أولاً",
                            Toast.LENGTH_LONG
                        ).show()
                    }
                }
            }
        }.start()
    }

    val openBrowser: () -> Unit = {
        try {
            val browserIntent = context.packageManager.getLaunchIntentForPackage("com.hb.browser")
            if (browserIntent != null) context.startActivity(browserIntent)
            else Toast.makeText(context, "HB Browser غير مثبت", Toast.LENGTH_LONG).show()
        } catch (_: Exception) {
            Toast.makeText(context, "تعذر فتح HB Browser", Toast.LENGTH_LONG).show()
        }
    }

    Surface(modifier = Modifier.fillMaxSize(), color = Navy) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(20.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp)
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(
                    modifier = Modifier
                        .size(48.dp)
                        .background(Cyan.copy(alpha = 0.14f), RoundedCornerShape(14.dp)),
                    contentAlignment = Alignment.Center
                ) {
                    Icon(Icons.Default.SettingsEthernet, contentDescription = null, tint = Cyan, modifier = Modifier.size(29.dp))
                }
                Spacer(Modifier.width(12.dp))
                Column {
                    Text("H.B Proxy", color = Color.White, fontSize = 25.sp, fontWeight = FontWeight.Black)
                    Text("SOCKS5 • VPN TUNNEL", color = Muted, fontSize = 11.sp, letterSpacing = 1.4.sp)
                }
                Spacer(Modifier.weight(1f))
                Text("v0.2.0", color = Muted, fontSize = 12.sp)
            }

            Card(colors = CardDefaults.cardColors(containerColor = Panel), shape = RoundedCornerShape(22.dp)) {
                Column(Modifier.fillMaxWidth().padding(18.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                    Text("حالة الاتصال", color = Muted)
                    Spacer(Modifier.height(8.dp))
                    Text(connectionMessage, color = if (connected) Color(0xFF7FE0B0) else Color(0xFFFFC266), fontSize = 22.sp, fontWeight = FontWeight.Bold)
                    Text(
                        if (connected) "البروكسي النشط: ${prefs.getString("active_proxy_id", null)?.let { id -> profiles.firstOrNull { it.id == id }?.name } ?: "غير محدد"}"
                        else "اتصال SOCKS5 عبر VPN للهاتف بالكامل",
                        color = Muted,
                        fontSize = 12.sp
                    )
                    Spacer(Modifier.height(16.dp))
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                        Button(
                            onClick = requestStart,
                            enabled = saved && validConfig,
                            modifier = Modifier.weight(1f)
                        ) { Text(if (connected) "تبديل وتشغيل" else "تشغيل البروكسي") }
                        OutlinedButton(
                            onClick = {
                                val stop = Intent(context, BodexVpnService::class.java).setAction(BodexVpnService.ACTION_STOP)
                                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) context.startForegroundService(stop) else context.startService(stop)
                                connected = false
                                connectionMessage = "جارٍ إيقاف البروكسي"
                            },
                            enabled = connected,
                            modifier = Modifier.weight(1f)
                        ) { Text("إيقاف البروكسي") }
                    }
                    if (connected) {
                        Spacer(Modifier.height(10.dp))
                        OutlinedButton(onClick = openBrowser, modifier = Modifier.fillMaxWidth()) { Text("فتح HB Browser") }
                    }
                }
            }

            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("البروكسيات المحفوظة (${profiles.size})", color = Color.White, fontWeight = FontWeight.Bold, fontSize = 18.sp)
                Spacer(Modifier.weight(1f))
                TextButton(onClick = { startNewProfile() }) { Text("+ إضافة بروكسي") }
            }

            if (profiles.isEmpty()) {
                Text("لا توجد بروكسيات محفوظة بعد. أضف أول بروكسي من النموذج أدناه.", color = Muted, fontSize = 13.sp)
            } else {
                profiles.forEach { profile ->
                    Card(
                        modifier = Modifier.fillMaxWidth().clickable { loadIntoEditor(profile) },
                        colors = CardDefaults.cardColors(containerColor = if (profile.id == selectedId) Color(0xFF193A55) else Panel),
                        shape = RoundedCornerShape(16.dp)
                    ) {
                        Row(
                            Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 10.dp),
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            RadioButton(selected = profile.id == selectedId, onClick = { loadIntoEditor(profile) })
                            Column(Modifier.weight(1f)) {
                                Text(profile.name, color = Color.White, fontWeight = FontWeight.Bold)
                                Text("${profile.host}:${profile.port}", color = Muted, fontSize = 12.sp)
                            }
                            TextButton(onClick = { deleteProfile(profile) }) { Text("حذف", color = Color(0xFFFF8C8C)) }
                        }
                    }
                }
            }

            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Default.SettingsEthernet, contentDescription = null, tint = Cyan)
                Spacer(Modifier.width(8.dp))
                Text(if (editingId == null) "إضافة بروكسي جديد" else "تعديل بيانات البروكسي", color = Color.White, fontWeight = FontWeight.Bold, fontSize = 18.sp)
            }

            Card(colors = CardDefaults.cardColors(containerColor = Panel), shape = RoundedCornerShape(18.dp)) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    OutlinedTextField(
                        value = profileLabel,
                        onValueChange = { profileLabel = it; saved = false },
                        label = { Text("اسم البروكسي (مثلاً: أمريكا)") },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth()
                    )
                    Text("نوع البروكسي: SOCKS5", color = Cyan, fontWeight = FontWeight.Bold)
                    OutlinedTextField(
                        value = host,
                        onValueChange = { host = it; saved = false },
                        label = { Text("Server / IP") },
                        placeholder = { Text("proxy.example.com") },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth()
                    )
                    OutlinedTextField(
                        value = port,
                        onValueChange = { port = it.filter(Char::isDigit).take(5); saved = false },
                        label = { Text("Port") },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth()
                    )
                    OutlinedTextField(
                        value = username,
                        onValueChange = { username = it; saved = false },
                        label = { Text("Username (اختياري)") },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth()
                    )
                    OutlinedTextField(
                        value = password,
                        onValueChange = { password = it; saved = false },
                        label = { Text("Password (اختياري)") },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth()
                    )
                    Button(onClick = { saveProfile() }, enabled = validConfig, modifier = Modifier.fillMaxWidth()) {
                        Text(if (editingId == null) "حفظ البروكسي الجديد" else "حفظ التعديلات")
                    }
                    Text(
                        if (saved) "تم حفظ هذا البروكسي. يمكنك اختياره وتشغيله متى تريد."
                        else "احفظ بيانات البروكسي قبل تشغيله. البروكسيات الأخرى لن تُحذف.",
                        color = if (saved) Color(0xFF7FE0B0) else Muted,
                        fontSize = 12.sp
                    )
                }
            }

            Text("H.B Proxy • SOCKS5 VPN", color = Muted, fontSize = 11.sp, modifier = Modifier.align(Alignment.CenterHorizontally))
        }
    }
}
