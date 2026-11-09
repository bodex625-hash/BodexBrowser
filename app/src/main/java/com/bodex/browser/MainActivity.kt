package com.bodex.browser

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Language
import androidx.compose.material.icons.filled.Security
import androidx.compose.material.icons.filled.SettingsEthernet
import androidx.compose.material3.*
import androidx.compose.runtime.*
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
    var proxyType by remember { mutableStateOf("SOCKS5") }
    var host by remember { mutableStateOf("") }
    var port by remember { mutableStateOf("") }
    var username by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var profileLabel by remember { mutableStateOf("My proxy") }
    var saved by remember { mutableStateOf(false) }
    var url by remember { mutableStateOf("https://example.com") }

    Surface(modifier = Modifier.fillMaxSize(), color = Navy) {
        Column(
            modifier = Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(20.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp)
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Default.Security, contentDescription = null, tint = Cyan, modifier = Modifier.size(34.dp))
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
                    Text("غير متصل", color = Color(0xFFFFC266), fontSize = 28.sp, fontWeight = FontWeight.Bold)
                    Text("محرك VPN قيد الدمج والاختبار", color = Muted, fontSize = 12.sp)
                    Spacer(Modifier.height(14.dp))
                    Button(onClick = { }, enabled = false, modifier = Modifier.fillMaxWidth(), colors = ButtonDefaults.buttonColors(disabledContainerColor = Color(0xFF28506A))) {
                        Text("تشغيل البروكسي — قريباً", color = Color.White)
                    }
                }
            }

            SectionTitle("إعداد البروكسي", Icons.Default.SettingsEthernet)
            Card(colors = CardDefaults.cardColors(containerColor = Panel), shape = RoundedCornerShape(18.dp)) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    OutlinedTextField(value = profileLabel, onValueChange = { profileLabel = it; saved = false }, label = { Text("اسم الملف") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                    Text("نوع البروكسي", color = Muted)
                    SingleChoiceSegmentedButtonRow(modifier = Modifier.fillMaxWidth()) {
                        listOf("SOCKS5", "HTTP").forEachIndexed { index, value ->
                            SegmentedButton(selected = proxyType == value, onClick = { proxyType = value; saved = false }, shape = SegmentedButtonDefaults.itemShape(index, 2)) { Text(value) }
                        }
                    }
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
                    OutlinedTextField(value = url, onValueChange = { url = it }, label = { Text("عنوان الموقع") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                    Button(onClick = { }, enabled = false, modifier = Modifier.fillMaxWidth()) { Text("فتح الموقع — سيُفعّل بعد إضافة WebView") }
                    Text("المرحلة الحالية تعرض هيكل الواجهة فقط؛ لم يتم توجيه حركة التصفح أو الهاتف عبر البروكسي بعد.", color = Muted, fontSize = 12.sp)
                }
            }

            Text("Bodex Browser • Android", color = Muted, fontSize = 11.sp, modifier = Modifier.align(Alignment.CenterHorizontally))
        }
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
