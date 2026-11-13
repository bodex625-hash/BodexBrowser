package com.bodex.browser

import android.app.Activity
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Bundle
import android.view.Gravity
import android.view.inputmethod.EditorInfo
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import java.util.concurrent.Executors

class ActivationActivity : Activity() {
    private val accent = Color.rgb(32, 200, 245)
    private val black = Color.rgb(9, 16, 29)
    private val panel = Color.rgb(20, 34, 56)
    private val muted = Color.rgb(159, 177, 200)
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
            setPadding(24, 24, 24, 24)
            setBackgroundColor(black)
        }
        val logo = TextView(this).apply {
            text = "HB"
            textSize = 42f
            typeface = Typeface.DEFAULT_BOLD
            setTextColor(accent)
            gravity = Gravity.CENTER
        }
        val title = TextView(this).apply {
            text = "HB Browser"
            textSize = 26f
            typeface = Typeface.DEFAULT_BOLD
            setTextColor(Color.WHITE)
            gravity = Gravity.CENTER
        }
        val subtitle = TextView(this).apply {
            text = "كود واحد يفعّل HB Browser و H.B Proxy"
            textSize = 15f
            setTextColor(muted)
            gravity = Gravity.CENTER
            setPadding(0, 10, 0, 26)
        }
        codeInput = EditText(this).apply {
            hint = "أدخل كود الاشتراك"
            textSize = 18f
            setSingleLine(true)
            inputType = android.text.InputType.TYPE_CLASS_TEXT or
                android.text.InputType.TYPE_TEXT_FLAG_CAP_CHARACTERS
            imeOptions = EditorInfo.IME_ACTION_DONE
            setTextColor(Color.WHITE)
            setHintTextColor(muted)
            gravity = Gravity.CENTER
            setPadding(18, 14, 18, 14)
            background = rounded(panel, accent, 2)
        }
        activateButton = Button(this).apply {
            text = "تفعيل الاشتراك"
            textSize = 16f
            isAllCaps = false
            typeface = Typeface.DEFAULT_BOLD
            setTextColor(black)
            background = rounded(accent, accent, 0)
            backgroundTintList = null
            stateListAnimator = null
            minHeight = 0
            minimumHeight = 0
            setPadding(18, 12, 18, 12)
            setOnClickListener { activate() }
        }
        status = TextView(this).apply {
            textSize = 13f
            setTextColor(muted)
            gravity = Gravity.CENTER
            setPadding(8, 20, 8, 0)
        }

        root.addView(logo, LinearLayout.LayoutParams(-1, 68))
        root.addView(title, LinearLayout.LayoutParams(-1, -2))
        root.addView(subtitle, LinearLayout.LayoutParams(-1, -2))
        root.addView(codeInput, LinearLayout.LayoutParams(-1, 62))
        root.addView(activateButton, LinearLayout.LayoutParams(-1, 56).apply {
            topMargin = 16
        })
        root.addView(status, LinearLayout.LayoutParams(-1, -2).apply {
            topMargin = 2
        })
        setContentView(root)
    }

    private fun rounded(color: Int, stroke: Int, strokeWidth: Int): GradientDrawable =
        GradientDrawable().apply {
            setColor(color)
            cornerRadius = 16f
            if (strokeWidth > 0) setStroke(strokeWidth, stroke)
        }

    private fun setControls(enabled: Boolean) {
        codeInput.isEnabled = enabled
        activateButton.isEnabled = enabled
        activateButton.alpha = if (enabled) 1f else 0.55f
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
                        Toast.makeText(this, "تم تفعيل الاشتراك للتطبيقين بنجاح", Toast.LENGTH_LONG).show()
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
        startActivity(android.content.Intent(this, BrowserActivity::class.java))
        finish()
    }

    override fun onDestroy() {
        executor.shutdownNow()
        super.onDestroy()
    }
}