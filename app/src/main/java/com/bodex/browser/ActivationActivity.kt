package com.bodex.browser

import android.app.Activity
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Bundle
import android.view.Gravity
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import java.util.concurrent.Executors

class ActivationActivity : Activity() {
    private val gold = Color.rgb(212, 175, 55)
    private val black = Color.rgb(9, 10, 12)
    private val panel = Color.rgb(23, 25, 29)
    private val executor = Executors.newSingleThreadExecutor()
    private lateinit var status: TextView
    private lateinit var codeInput: EditText
    private lateinit var activateButton: Button

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.statusBarColor = black
        window.navigationBarColor = black
        buildScreen()
        status.text = "جارٍ التحقق من الاشتراك..."
        setControls(false)
        executor.execute {
            val state = ActivationManager.check(this)
            runOnUiThread { if (!isFinishing) handleState(state) }
        }
    }

    private fun buildScreen() {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            setPadding(28, 24, 28, 24)
            setBackgroundColor(black)
        }
        val logo = TextView(this).apply {
            text = "HB"
            textSize = 42f
            typeface = Typeface.DEFAULT_BOLD
            setTextColor(gold)
            gravity = Gravity.CENTER
        }
        val title = TextView(this).apply {
            text = "H.B Proxy"
            textSize = 25f
            typeface = Typeface.DEFAULT_BOLD
            setTextColor(Color.WHITE)
            gravity = Gravity.CENTER
        }
        val subtitle = TextView(this).apply {
            text = "أدخل كود الاشتراك لتفعيل المتصفح"
            textSize = 15f
            setTextColor(Color.LTGRAY)
            gravity = Gravity.CENTER
            setPadding(0, 10, 0, 22)
        }
        codeInput = EditText(this).apply {
            hint = "HB-XXXX-XXXX-XXXX"
            textSize = 17f
            setSingleLine(true)
            setTextColor(Color.WHITE)
            setHintTextColor(Color.GRAY)
            gravity = Gravity.CENTER
            setPadding(16, 12, 16, 12)
            background = rounded(panel, gold)
        }
        activateButton = Button(this).apply {
            text = "تفعيل المتصفح"
            setTextColor(black)
            setBackgroundColor(gold)
            setOnClickListener { activate() }
        }
        status = TextView(this).apply {
            textSize = 13f
            setTextColor(Color.LTGRAY)
            gravity = Gravity.CENTER
            setPadding(0, 18, 0, 0)
        }
        root.addView(logo, LinearLayout.LayoutParams(-1, 70))
        root.addView(title)
        root.addView(subtitle)
        root.addView(codeInput, LinearLayout.LayoutParams(-1, 54))
        root.addView(activateButton, LinearLayout.LayoutParams(-1, 52).apply { topMargin = 14 })
        root.addView(status)
        setContentView(root)
    }

    private fun rounded(color: Int, stroke: Int): GradientDrawable =
        GradientDrawable().apply {
            setColor(color)
            cornerRadius = 18f
            setStroke(2, stroke)
        }

    private fun setControls(enabled: Boolean) {
        codeInput.isEnabled = enabled
        activateButton.isEnabled = enabled
    }

    private fun handleState(state: ActivationManager.State) {
        when (state) {
            ActivationManager.State.ACTIVE -> openProxy()
            ActivationManager.State.OFFLINE_GRACE -> {
                status.text = "تعذر الاتصال بالخادم. يعمل التطبيق مؤقتاً حتى عودة الاتصال."
                openProxy()
            }
            ActivationManager.State.NEEDS_CODE -> {
                status.text = "التطبيق غير مفعّل. أدخل كود الاشتراك."
                setControls(true)
            }
            ActivationManager.State.INVALID -> {
                status.text = "الاشتراك منتهي أو موقوف، أو تعذر التحقق منه. اتصل بالإنترنت."
                setControls(true)
            }
            ActivationManager.State.SERVER_NOT_CONFIGURED -> {
                status.text = "خادم التفعيل غير مضبوط."
                setControls(true)
            }
        }
    }

    private fun activate() {
        val code = codeInput.text.toString().trim()
        if (code.isBlank()) {
            codeInput.error = "أدخل كود التفعيل"
            return
        }
        setControls(false)
        status.text = "جارٍ التحقق من الكود..."
        executor.execute {
            try {
                val expiry = ActivationManager.activate(this, code)
                runOnUiThread {
                    if (!isFinishing) {
                        Toast.makeText(this, "تم تفعيل التطبيق بنجاح", Toast.LENGTH_LONG).show()
                        status.text = "ينتهي الاشتراك: $expiry"
                        openProxy()
                    }
                }
            } catch (e: Exception) {
                runOnUiThread {
                    if (!isFinishing) {
                        status.text = e.message ?: "تعذر التفعيل. تحقق من الاتصال والكود."
                        setControls(true)
                    }
                }
            }
        }
    }

    private fun openProxy() {
        startActivity(android.content.Intent(this, MainActivity::class.java))
        finish()
    }

    override fun onDestroy() {
        executor.shutdownNow()
        super.onDestroy()
    }
}
