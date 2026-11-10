package com.bodex.browser

import android.os.Bundle
import android.app.Activity
import android.view.View
import android.view.WindowInsets
import android.Manifest
import android.webkit.GeolocationPermissions
import android.webkit.PermissionRequest
import android.webkit.WebChromeClient
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.compose.ui.viewinterop.AndroidView
import android.content.Intent
import android.content.BroadcastReceiver
import android.content.IntentFilter
import android.net.VpnService
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.foundation.Image
import com.bodex.browser.vpn.BodexVpnService
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Language
import androidx.compose.material.icons.filled.SettingsEthernet
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.DisposableEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

private val Navy = Color(0xFF0B1220)
private val Panel = Color(0xFF142238)
private val Cyan = Color(0xFF20C8F5)
private val Muted = Color(0xFF9FB1C8)

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent { BodexTheme { BodexHome() } }
    }
}

@Composable
private fun BodexTheme(content: @Composable () -> Unit) {
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
private fun BodexHome() {
    var host by remember { mutableStateOf("") }
    var port by remember { mutableStateOf("") }
    var username by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var profileLabel by remember { mutableStateOf("My proxy") }
    var saved by remember { mutableStateOf(false) }
    var url by remember { mutableStateOf("https://example.com") }
    val context = LocalContext.current
    val prefs = remember { context.getSharedPreferences("bodex_state", android.content.Context.MODE_PRIVATE) }
    var connected by remember { mutableStateOf(prefs.getBoolean("connected", false)) }
    var browserMode by remember { mutableStateOf(false) }

    DisposableEffect(browserMode, context) {
        val window = (context as? Activity)?.window
        val decor = window?.decorView
        val previousFlags = decor?.systemUiVisibility ?: 0
        if (browserMode && window != null && decor != null) {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                window.insetsController?.let { controller ->
                    controller.systemBarsBehavior =
                        android.view.WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
                    controller.hide(WindowInsets.Type.statusBars() or WindowInsets.Type.navigationBars())
                }
            } else {
                @Suppress("DEPRECATION")
                decor.systemUiVisibility = (
                    View.SYSTEM_UI_FLAG_FULLSCREEN or
                    View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or
                    View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY or
                    View.SYSTEM_UI_FLAG_LAYOUT_STABLE or
                    View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN or
                    View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                )
            }
        } else if (window != null && decor != null) {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                window.insetsController?.show(WindowInsets.Type.statusBars() or WindowInsets.Type.navigationBars())
            } else {
                @Suppress("DEPRECATION")
                decor.systemUiVisibility = previousFlags
            }
        }
        onDispose {
            if (window != null && decor != null) {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                    window.insetsController?.show(WindowInsets.Type.statusBars() or WindowInsets.Type.navigationBars())
                } else {
                    @Suppress("DEPRECATION")
                    decor.systemUiVisibility = previousFlags
                }
            }
        }
    }
    var connectionMessage by remember { mutableStateOf(prefs.getString("message", "غير متصل") ?: "غير متصل") }
    DisposableEffect(context) {
        val receiver = object : BroadcastReceiver() {
            override fun onReceive(ctx: android.content.Context?, intent: Intent?) {
                if (intent?.action == BodexVpnService.ACTION_STATE) {
                    connected = intent.getBooleanExtra(BodexVpnService.EXTRA_CONNECTED, false)
                    if (connected) browserMode = true
                    connectionMessage = intent.getStringExtra(BodexVpnService.EXTRA_MESSAGE) ?: if (connected) "متصل" else "غير متصل"
                    prefs.edit().putBoolean("connected", connected).putString("message", connectionMessage).apply()
                }
            }
        }
        val filter = IntentFilter(BodexVpnService.ACTION_STATE)
        if (Build.VERSION.SDK_INT >= 33) {
            context.registerReceiver(receiver, filter, android.content.Context.RECEIVER_NOT_EXPORTED)
        } else {
            @Suppress("DEPRECATION")
            context.registerReceiver(receiver, filter)
        }
        onDispose { context.unregisterReceiver(receiver) }
    }
    val startVpn: () -> Unit = {
        val serviceIntent = Intent(context, BodexVpnService::class.java).apply {
            action = BodexVpnService.ACTION_START
            putExtra(BodexVpnService.EXTRA_HOST, host.trim())
            putExtra(BodexVpnService.EXTRA_PORT, port.toIntOrNull() ?: 0)
            putExtra(BodexVpnService.EXTRA_USER, username)
            putExtra(BodexVpnService.EXTRA_PASS, password)
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) context.startForegroundService(serviceIntent) else context.startService(serviceIntent)
        connectionMessage = "تم إرسال طلب الاتصال؛ تحقق من حالة VPN"
    }
    val vpnPermission = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        if (result.resultCode == android.app.Activity.RESULT_OK) startVpn()
    }

    val webPermissions = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { }
    val webView = remember(context) {
        WebView(context).apply {
            setBackgroundColor(android.graphics.Color.rgb(11, 18, 32))
            settings.javaScriptEnabled = true
            settings.domStorageEnabled = true
            settings.databaseEnabled = true
            settings.setGeolocationEnabled(false)
            settings.mediaPlaybackRequiresUserGesture = false
            settings.mixedContentMode = android.webkit.WebSettings.MIXED_CONTENT_NEVER_ALLOW
            settings.javaScriptCanOpenWindowsAutomatically = false
            settings.setSupportMultipleWindows(false)
            webViewClient = object : WebViewClient() {
                override fun shouldOverrideUrlLoading(view: WebView, request: android.webkit.WebResourceRequest): Boolean {
                    val scheme = request.url.scheme?.lowercase()
                    return scheme != "https" && scheme != "http"
                }
            }
            webChromeClient = object : WebChromeClient() {
                override fun onPermissionRequest(request: PermissionRequest) {
                    post {
                        val requested = request.resources.toSet()
                        val supported = requested.all {
                            it == PermissionRequest.RESOURCE_VIDEO_CAPTURE ||
                            it == PermissionRequest.RESOURCE_AUDIO_CAPTURE
                        }
                        val cameraGranted = context.checkSelfPermission(Manifest.permission.CAMERA) == android.content.pm.PackageManager.PERMISSION_GRANTED
                        val micGranted = context.checkSelfPermission(Manifest.permission.RECORD_AUDIO) == android.content.pm.PackageManager.PERMISSION_GRANTED
                        val allowed = supported &&
                            (!requested.contains(PermissionRequest.RESOURCE_VIDEO_CAPTURE) || cameraGranted) &&
                            (!requested.contains(PermissionRequest.RESOURCE_AUDIO_CAPTURE) || micGranted)
                        if (allowed) request.grant(request.resources) else request.deny()
                    }
                }

                override fun onGeolocationPermissionsShowPrompt(origin: String, callback: GeolocationPermissions.Callback) {
                    callback.invoke(origin, false, false)
                }
            }
            loadUrl("https://example.com")
        }
    }
    DisposableEffect(webView) {
        onDispose {
            webView.stopLoading()
            webView.destroy()
        }
    }

    Surface(modifier = Modifier.fillMaxSize(), color = Navy) {
        if (browserMode && connected) {
            FullBrowserScreen(onBackToProxy = { browserMode = false })
        } else {
        Column(
            modifier = Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(20.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp)
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Image(painter = painterResource(id = R.drawable.bodex_logo), contentDescription = "Bodex logo", modifier = Modifier.size(42.dp))
                Spacer(Modifier.width(10.dp))
                Column {
                    Text("BODEX", color = Color.White, fontSize = 25.sp, fontWeight = FontWeight.Black)
                    Text("PROXY BROWSER", color = Muted, fontSize = 11.sp, letterSpacing = 2.sp)
                }
                Spacer(Modifier.weight(1f))
                Text("v0.1.0", color = Muted, fontSize = 12.sp)
            }

            Card(colors = CardDefaults.cardColors(containerColor = Panel), shape = RoundedCornerShape(22.dp)) {
                Column(Modifier.fillMaxWidth().padding(18.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                    Text("حالة الاتصال", color = Muted)
                    Spacer(Modifier.height(8.dp))
                    Text(connectionMessage, color = if (connected) Color(0xFF7FE0B0) else Color(0xFFFFC266), fontSize = 22.sp, fontWeight = FontWeight.Bold)
                    Text("اتصال SOCKS5 عبر VPN للهاتف", color = Muted, fontSize = 12.sp)
                    if (connected) {
                        TextButton(onClick = { browserMode = true }) { Text("فتح المتصفح الكامل", color = Cyan) }
                    }
                    Spacer(Modifier.height(14.dp))
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                        Button(
                            onClick = {
                                val permission = VpnService.prepare(context)
                                if (permission == null) startVpn() else vpnPermission.launch(permission)
                            },
                            enabled = !connected && saved,
                            modifier = Modifier.weight(1f)
                        ) { Text("تشغيل البروكسي") }
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
                }
            }

            SectionTitle("إعداد البروكسي", Icons.Default.SettingsEthernet)
            Card(colors = CardDefaults.cardColors(containerColor = Panel), shape = RoundedCornerShape(18.dp)) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    OutlinedTextField(value = profileLabel, onValueChange = { profileLabel = it; saved = false }, label = { Text("اسم الملف") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                    Text("نوع البروكسي: SOCKS5", color = Cyan, fontWeight = FontWeight.Bold)
                    Text("يدعم إعدادات SOCKS5 مع اسم المستخدم وكلمة المرور حسب مزوّد الخدمة.", color = Muted, fontSize = 12.sp)
                    OutlinedTextField(value = host, onValueChange = { host = it; saved = false }, label = { Text("Server / IP") }, placeholder = { Text("proxy.example.com") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                    OutlinedTextField(value = port, onValueChange = { port = it.filter(Char::isDigit).take(5); saved = false }, label = { Text("Port") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                    OutlinedTextField(value = username, onValueChange = { username = it; saved = false }, label = { Text("Username (اختياري)") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                    OutlinedTextField(value = password, onValueChange = { password = it; saved = false }, label = { Text("Password (اختياري)") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                    Button(onClick = { saved = host.isNotBlank() && (port.toIntOrNull()?.let { it in 1..65535 } == true) }, enabled = host.isNotBlank() && (port.toIntOrNull()?.let { it in 1..65535 } == true), modifier = Modifier.fillMaxWidth()) {
                        Text("حفظ الإعداد محلياً")
                    }
                    if (saved) Text("تم التحقق من الحقول — الحفظ الدائم المشفر سيضاف في المرحلة التالية", color = Color(0xFF7FE0B0), fontSize = 12.sp)
                }
            }

            SectionTitle("المتصفح المدمج", Icons.Default.Language)
            Card(colors = CardDefaults.cardColors(containerColor = Panel), shape = RoundedCornerShape(18.dp)) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    // Keep the native WebView first: on some Android versions it can draw above Compose siblings.
                    AndroidView(factory = { webView }, modifier = Modifier.fillMaxWidth().height(360.dp))
                    OutlinedTextField(value = url, onValueChange = { url = it }, label = { Text("عنوان الموقع") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                    Button(onClick = {
                        val target = url.trim().let { if (it.startsWith("https://") || it.startsWith("http://")) it else "https://$it" }
                        webView.loadUrl(target)
                    }, enabled = url.isNotBlank(), modifier = Modifier.fillMaxWidth()) { Text("فتح الموقع") }
                    OutlinedButton(
                        onClick = {
                            webPermissions.launch(arrayOf(
                                Manifest.permission.CAMERA,
                                Manifest.permission.RECORD_AUDIO
                            ))
                        },
                        modifier = Modifier.fillMaxWidth()
                    ) { Text("تفعيل أذونات الكاميرا والميكروفون") }
                    Text("WebRTC مفعّل. GPS الهاتف محظور داخل المتصفح؛ أذونات الكاميرا والميكروفون تخضع لموافقة أندرويد.", color = Muted, fontSize = 12.sp)
                }
            }

            Text("Bodex Browser • Android", color = Muted, fontSize = 11.sp, modifier = Modifier.align(Alignment.CenterHorizontally))
        }
        }
    }
}

@Composable
private fun FullBrowserScreen(onBackToProxy: () -> Unit) {
    val context = LocalContext.current
    val tabs = remember { mutableStateListOf<WebView>() }
    var selectedTab by remember { mutableIntStateOf(0) }
    var address by remember { mutableStateOf("https://www.google.com") }
    var pageTitle by remember { mutableStateOf("تبويب جديد") }

    fun createTab(initialUrl: String): WebView {
        return WebView(context).apply {
            setBackgroundColor(android.graphics.Color.WHITE)
            settings.javaScriptEnabled = true
            settings.domStorageEnabled = true
            settings.databaseEnabled = true
            settings.setGeolocationEnabled(false)
            settings.mediaPlaybackRequiresUserGesture = false
            settings.mixedContentMode = android.webkit.WebSettings.MIXED_CONTENT_NEVER_ALLOW
            settings.javaScriptCanOpenWindowsAutomatically = false
            settings.setSupportMultipleWindows(false)
            webViewClient = object : WebViewClient() {
                override fun onPageStarted(view: WebView, url: String?, favicon: android.graphics.Bitmap?) {
                    if (tabs.getOrNull(selectedTab) === view && !url.isNullOrBlank()) address = url
                }
                override fun onPageFinished(view: WebView, url: String?) {
                    if (tabs.getOrNull(selectedTab) === view) pageTitle = view.title ?: "Bodex Browser"
                }
                override fun shouldOverrideUrlLoading(view: WebView, request: android.webkit.WebResourceRequest): Boolean {
                    val scheme = request.url.scheme?.lowercase()
                    return scheme != "https" && scheme != "http"
                }
            }
            webChromeClient = object : WebChromeClient() {
                override fun onGeolocationPermissionsShowPrompt(origin: String, callback: GeolocationPermissions.Callback) {
                    callback.invoke(origin, false, false)
                }
                override fun onPermissionRequest(request: PermissionRequest) {
                    post {
                        val requested = request.resources.toSet()
                        val supported = requested.all {
                            it == PermissionRequest.RESOURCE_VIDEO_CAPTURE ||
                            it == PermissionRequest.RESOURCE_AUDIO_CAPTURE
                        }
                        val cameraGranted = context.checkSelfPermission(Manifest.permission.CAMERA) == android.content.pm.PackageManager.PERMISSION_GRANTED
                        val micGranted = context.checkSelfPermission(Manifest.permission.RECORD_AUDIO) == android.content.pm.PackageManager.PERMISSION_GRANTED
                        val allowed = supported &&
                            (!requested.contains(PermissionRequest.RESOURCE_VIDEO_CAPTURE) || cameraGranted) &&
                            (!requested.contains(PermissionRequest.RESOURCE_AUDIO_CAPTURE) || micGranted)
                        if (allowed) request.grant(request.resources) else request.deny()
                    }
                }
            }
            loadUrl(initialUrl)
        }
    }

    LaunchedEffect(Unit) {
        if (tabs.isEmpty()) tabs.add(createTab("https://www.google.com"))
    }
    DisposableEffect(Unit) {
        onDispose {
            tabs.forEach { it.stopLoading(); it.destroy() }
            tabs.clear()
        }
    }

    Column(Modifier.fillMaxSize().background(Navy)) {
        Row(
            Modifier.fillMaxWidth().background(Panel).padding(horizontal = 8.dp, vertical = 6.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(6.dp)
        ) {
            Text("BODEX", color = Cyan, fontWeight = FontWeight.Black, fontSize = 18.sp)
            Spacer(Modifier.weight(1f))
            Text("VPN ●", color = Color(0xFF7FE0B0), fontSize = 12.sp)
            TextButton(onClick = onBackToProxy) { Text("البروكسي", color = Muted) }
        }
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 6.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(4.dp)
        ) {
            OutlinedTextField(
                value = address,
                onValueChange = { address = it },
                modifier = Modifier.weight(1f),
                singleLine = true,
                label = { Text("ابحث أو اكتب عنوان الموقع") },
                shape = RoundedCornerShape(14.dp)
            )
            Button(onClick = {
                val raw = address.trim()
                val target = when {
                    raw.startsWith("https://") || raw.startsWith("http://") -> raw
                    raw.contains(".") && !raw.contains(" ") -> "https://$raw"
                    else -> "https://www.google.com/search?q=" + android.net.Uri.encode(raw)
                }
                tabs.getOrNull(selectedTab)?.loadUrl(target)
            }, enabled = address.isNotBlank(), contentPadding = PaddingValues(horizontal = 12.dp, vertical = 12.dp)) { Text("اذهب") }
        }
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(4.dp)
        ) {
            TextButton(onClick = { tabs.getOrNull(selectedTab)?.let { if (it.canGoBack()) it.goBack() } }) { Text("‹", fontSize = 24.sp, color = Color.White) }
            TextButton(onClick = { tabs.getOrNull(selectedTab)?.let { if (it.canGoForward()) it.goForward() } }) { Text("›", fontSize = 24.sp, color = Color.White) }
            TextButton(onClick = { tabs.getOrNull(selectedTab)?.reload() }) { Text("⟳", fontSize = 22.sp, color = Color.White) }
            Spacer(Modifier.weight(1f))
            TextButton(onClick = {
                tabs.add(createTab("https://www.google.com"))
                selectedTab = tabs.lastIndex
                address = "https://www.google.com"
            }) { Text("+ تبويب", color = Cyan) }
            TextButton(onClick = {
                if (tabs.size > 1) {
                    tabs.removeAt(selectedTab).apply { stopLoading(); destroy() }
                    selectedTab = (selectedTab - 1).coerceAtLeast(0)
                    address = tabs[selectedTab].url ?: "https://www.google.com"
                } else {
                    tabs.firstOrNull()?.loadUrl("https://www.google.com")
                }
            }) { Text("×", color = Color.White, fontSize = 20.sp) }
            Text("${tabs.size}", color = Muted)
        }
        Row(
            Modifier.fillMaxWidth().heightIn(min = 38.dp).background(Panel).padding(horizontal = 6.dp),
            horizontalArrangement = Arrangement.spacedBy(6.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            tabs.forEachIndexed { index, tab ->
                TextButton(
                    onClick = {
                        selectedTab = index
                        address = tab.url ?: "https://www.google.com"
                        pageTitle = tab.title ?: "تبويب ${index + 1}"
                    },
                    modifier = Modifier.weight(1f).heightIn(max = 42.dp),
                    contentPadding = PaddingValues(horizontal = 4.dp, vertical = 2.dp),
                    colors = ButtonDefaults.textButtonColors(contentColor = if (index == selectedTab) Cyan else Muted)
                ) { Text(tab.title?.take(12)?.ifBlank { "تبويب ${index + 1}" } ?: "تبويب ${index + 1}", maxLines = 1, fontSize = 11.sp) }
            }
        }
        if (tabs.isNotEmpty()) {
            key(selectedTab) {
                AndroidView(
                    factory = { tabs[selectedTab] },
                    modifier = Modifier.fillMaxWidth().weight(1f),
                    update = { }
                )
            }
        } else {
            Box(Modifier.fillMaxWidth().weight(1f), contentAlignment = Alignment.Center) { CircularProgressIndicator(color = Cyan) }
        }
        Text(
            pageTitle,
            color = Muted,
            fontSize = 10.sp,
            maxLines = 1,
            modifier = Modifier.fillMaxWidth().background(Panel).padding(horizontal = 10.dp, vertical = 4.dp)
        )
    }
}

@Composable
private fun SectionTitle(title: String, icon: androidx.compose.ui.graphics.vector.ImageVector) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Icon(icon, contentDescription = null, tint = Cyan)
        Spacer(Modifier.width(8.dp))
        Text(title, color = Color.White, fontWeight = FontWeight.Bold, fontSize = 18.sp)
    }
}
