package com.hb.browser

import android.annotation.SuppressLint
import android.os.Bundle
import android.webkit.*
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView

private val Black = Color(0xFF080A0D)
private val SurfaceDark = Color(0xFF15191F)
private val Gold = Color(0xFFD6AD55)
private val TextMuted = Color(0xFF9BA3AE)
private const val HOME_URL = "https://www.google.com"

data class BrowserTab(val id: Int, val title: String, val url: String)

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        WebView.setWebContentsDebuggingEnabled(false)
        setContent { HBTheme { BrowserScreen() } }
    }
}

@Composable
private fun HBTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = darkColorScheme(
            primary = Gold, onPrimary = Black, background = Black,
            surface = SurfaceDark, onSurface = Color.White
        ),
        content = content
    )
}

@SuppressLint("SetJavaScriptEnabled")
@Composable
private fun BrowserScreen() {
    val tabs = remember { mutableStateListOf(BrowserTab(1, "علامة تبويب جديدة", HOME_URL)) }
    var selectedId by remember { mutableIntStateOf(1) }
    var nextId by remember { mutableIntStateOf(2) }
    var address by remember { mutableStateOf(HOME_URL) }
    var pageLoading by remember { mutableStateOf(false) }
    var menuOpen by remember { mutableStateOf(false) }
    var tabsDialog by remember { mutableStateOf(false) }
    val webViews = remember { mutableMapOf<Int, WebView>() }
    val keyboard = LocalSoftwareKeyboardController.current
    val currentTab = tabs.firstOrNull { it.id == selectedId } ?: tabs.first()

    fun updateTab(id: Int, title: String? = null, url: String? = null) {
        val index = tabs.indexOfFirst { it.id == id }
        if (index >= 0) {
            val old = tabs[index]
            tabs[index] = old.copy(title = title ?: old.title, url = url ?: old.url)
        }
    }

    fun navigate(raw: String) {
        val value = raw.trim()
        if (value.isBlank()) return
        val target = when {
            value.startsWith("https://") || value.startsWith("http://") -> value
            value.contains(".") && !value.contains(" ") -> "https://$value"
            else -> "https://www.google.com/search?q=" + java.net.URLEncoder.encode(value, "UTF-8")
        }
        address = target
        webViews[selectedId]?.loadUrl(target)
        updateTab(selectedId, url = target)
        keyboard?.hide()
    }

    fun addTab() {
        val id = nextId++
        tabs.add(BrowserTab(id, "علامة تبويب جديدة", HOME_URL))
        selectedId = id
        address = HOME_URL
    }

    BackHandler(enabled = webViews[selectedId]?.canGoBack() == true) {
        webViews[selectedId]?.goBack()
    }

    Column(Modifier.fillMaxSize().background(Black)) {
        Row(
            Modifier.fillMaxWidth().background(Black).padding(horizontal = 14.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Box(Modifier.size(38.dp).background(SurfaceDark, CircleShape), contentAlignment = Alignment.Center) {
                Text("HB", color = Gold, fontWeight = FontWeight.Black, fontSize = 17.sp)
            }
            Spacer(Modifier.width(10.dp))
            Column(Modifier.weight(1f)) {
                Text("HB Browser", color = Color.White, fontWeight = FontWeight.Bold, fontSize = 18.sp)
                Text("تصفح سريع وبسيط", color = TextMuted, fontSize = 11.sp)
            }
            IconButton(onClick = { tabsDialog = true }) {
                Icon(Icons.Default.Tab, contentDescription = "Tabs", tint = Gold)
            }
            Text(tabs.size.toString(), color = Gold, fontSize = 13.sp)
            IconButton(onClick = { addTab() }) {
                Icon(Icons.Default.Add, contentDescription = "New tab", tint = Gold)
            }
        }

        Row(
            Modifier.fillMaxWidth().padding(horizontal = 10.dp, vertical = 5.dp)
                .background(SurfaceDark, RoundedCornerShape(16.dp)).padding(horizontal = 8.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            TextField(
                value = address,
                onValueChange = { address = it },
                modifier = Modifier.weight(1f),
                singleLine = true,
                placeholder = { Text("ابحث أو اكتب عنوان موقع", color = TextMuted, fontSize = 13.sp) },
                colors = TextFieldDefaults.colors(
                    focusedContainerColor = Color.Transparent,
                    unfocusedContainerColor = Color.Transparent,
                    focusedIndicatorColor = Color.Transparent,
                    unfocusedIndicatorColor = Color.Transparent,
                    cursorColor = Gold
                ),
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Go),
                keyboardActions = KeyboardActions(onGo = { navigate(address) })
            )
            IconButton(onClick = { navigate(address) }) {
                Icon(Icons.Default.ArrowForward, contentDescription = "Go", tint = Gold)
            }
            Box {
                IconButton(onClick = { menuOpen = true }) {
                    Icon(Icons.Default.MoreVert, contentDescription = "Menu", tint = Color.White)
                }
                DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                    DropdownMenuItem(text = { Text("مسح الكاش") }, onClick = {
                        menuOpen = false
                        webViews.values.forEach { it.clearCache(true) }
                    })
                    DropdownMenuItem(text = { Text("مسح سجل التصفح") }, onClick = {
                        menuOpen = false
                        webViews.values.forEach { it.clearHistory() }
                    })
                }
            }
        }

        if (pageLoading) {
            LinearProgressIndicator(Modifier.fillMaxWidth().height(2.dp), color = Gold, trackColor = SurfaceDark)
        } else Spacer(Modifier.height(2.dp))

        key(selectedId) {
            AndroidView(
                modifier = Modifier.weight(1f).fillMaxWidth(),
                factory = { context ->
                    WebView(context).apply {
                        settings.apply {
                            javaScriptEnabled = true
                            domStorageEnabled = true
                            databaseEnabled = true
                            loadsImagesAutomatically = true
                            mediaPlaybackRequiresUserGesture = true
                            javaScriptCanOpenWindowsAutomatically = false
                            setSupportMultipleWindows(false)
                            allowFileAccess = false
                            allowContentAccess = false
                            setGeolocationEnabled(false)
                            mixedContentMode = WebSettings.MIXED_CONTENT_NEVER_ALLOW
                        }
                        webViewClient = object : WebViewClient() {
                            override fun onPageStarted(view: WebView, url: String, favicon: android.graphics.Bitmap?) {
                                address = url
                                pageLoading = true
                                updateTab(selectedId, url = url)
                            }
                            override fun onPageFinished(view: WebView, url: String) {
                                address = url
                                pageLoading = false
                                updateTab(selectedId, title = view.title ?: "HB Browser", url = url)
                            }
                        }
                        webChromeClient = object : WebChromeClient() {
                            override fun onGeolocationPermissionsShowPrompt(
                                origin: String?, callback: GeolocationPermissions.Callback?
                            ) {
                                callback?.invoke(origin, false, true)
                            }
                        }
                        webViews[selectedId] = this
                        loadUrl(currentTab.url)
                    }
                },
                update = { webViews[selectedId] = it }
            )
        }

        Row(
            Modifier.fillMaxWidth().background(SurfaceDark).navigationBarsPadding()
                .padding(horizontal = 14.dp, vertical = 5.dp),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            IconButton(onClick = { webViews[selectedId]?.takeIf { it.canGoBack() }?.goBack() }) {
                Icon(Icons.Default.ArrowBack, contentDescription = "Back", tint = Color.White)
            }
            IconButton(onClick = { webViews[selectedId]?.takeIf { it.canGoForward() }?.goForward() }) {
                Icon(Icons.Default.ArrowForward, contentDescription = "Forward", tint = Color.White)
            }
            IconButton(onClick = { webViews[selectedId]?.loadUrl(HOME_URL); address = HOME_URL }) {
                Icon(Icons.Default.Home, contentDescription = "Home", tint = Gold)
            }
            IconButton(onClick = { webViews[selectedId]?.reload() }) {
                Icon(Icons.Default.Refresh, contentDescription = "Refresh", tint = Color.White)
            }
            IconButton(onClick = {
                if (tabs.size > 1) {
                    webViews.remove(selectedId)?.destroy()
                    tabs.removeAll { it.id == selectedId }
                    selectedId = tabs.last().id
                    address = tabs.last().url
                } else {
                    webViews[selectedId]?.loadUrl(HOME_URL)
                    address = HOME_URL
                }
            }) {
                Icon(Icons.Default.Close, contentDescription = "Close tab", tint = Color.White)
            }
        }
    }

    if (tabsDialog) {
        AlertDialog(
            onDismissRequest = { tabsDialog = false },
            containerColor = SurfaceDark,
            title = { Text("علامات التبويب", color = Color.White) },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    tabs.forEach { tab ->
                        TextButton(onClick = {
                            selectedId = tab.id
                            address = tab.url
                            tabsDialog = false
                        }) {
                            Text(tab.title.ifBlank { tab.url }, color = if (tab.id == selectedId) Gold else Color.White)
                        }
                    }
                }
            },
            confirmButton = {
                TextButton(onClick = { tabsDialog = false; addTab() }) { Text("تبويب جديد", color = Gold) }
            }
        )
    }
}
