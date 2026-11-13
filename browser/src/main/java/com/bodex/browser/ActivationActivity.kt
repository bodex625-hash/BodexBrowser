package com.bodex.browser

import android.app.Activity
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Bundle
import android.view.Gravity
import android.view.View
import android.view.WindowManager
import android.view.inputmethod.EditorInfo
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
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

    private fun dp(value: Int): Int =
        (value * resources.displayMetrics.density + 0.5f).toInt()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.statusBarColor = black
        window.navigationBarColor = black
        window.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE)
        buildScreen()
        status.text = "جارٍ التحقق من الاشتراك..."
        setControls(false)
        executor.execute {
            val state = ActivationManager.check(this)
            runOnUiThread { if (!isFinishing) handleState(state) }
        }
    }

    private fun buildScreen() {
        val content = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
            setPadding(dp(16), dp(18), dp(16), dp(18))
            setBackgroundColor(black)
        }
        val logo = TextView(this).apply {
            text = "HB"
            textSize = 38f
            typeface = Typeface.DEFAULT_BOLD
            setTextColor(accent)
            gravity = Gravity.CENTER
            includeFontPadding = false
        }
        val title = TextView(this).apply {
            text = "HB Browser"
            textSize = 26f
            typeface = Typeface.DEFAULT_BOLD
            setTextColor(Color.WHITE)
            gravity = Gravity.CENTER
            includeFontPadding = false
        }
        val subtitle = TextView(this).apply {
            text = "كود واحد يفعّل HB Browser و H.B Proxy"
            textSize = 15f
            setTextColor(muted)
            gravity = Gravity.CENTER
            setPadding(0, dp(10), 0, dp(22))
        }
        codeInput = EditText(this).apply {
            hint = "أدخل كود الاشتراك"
            textSize = 17f
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            setSingleLine(true)
            maxLines = 1
            setHorizontallyScrolling(true)
            textDirection = View.TEXT_DIRECTION_LTR
            textAlignment = View.TEXT_ALIGNMENT_CENTER
            inputType = android.text.InputType.TYPE_CLASS_TEXT or
                android.text.InputType.TYPE_TEXT_FLAG_CAP_CHARACTERS
            imeOptions = EditorInfo.IME_ACTION_DONE
            setTextColor(Color.WHITE)
            setHintTextColor(muted)
            gravity = Gravity.CENTER_VERTICAL or Gravity.CENTER_HORIZONTAL
            setPadding(dp(12), 0, dp(12), 0)
            includeFontPadding = false
            background = rounded(panel, accent, dp(1))
        }
        activateButton = Button(this).apply {
            text = "تفعيل الاشتراك"
            textSize = 17f
            isAllCaps = false
            gravity = Gravity.CENTER
            includeFontPadding = false
            typeface = Typeface.DEFAULT_BOLD
            setTextColor(black)
            background = rounded(accent, accent, 0)
            backgroundTintList = null
            stateListAnimator = null
            minHeight = 0
            minimumHeight = 0
            minWidth = 0
            minimumWidth = 0
            setPadding(dp(12), 0, dp(12), 0)
            elevation = 0f
            setOnClickListener { activate() }
        }
        status = TextView(this).apply {
            textSize = 13f
            setTextColor(muted)
            gravity = Gravity.CENTER
            setPadding(dp(8), dp(14), dp(8), 0)
        }

        content.addView(logo, LinearLayout.LayoutParams(-1, dp(56)))
        content.addView(title, LinearLayout.LayoutParams(-1, dp(38)))
        content.addView(subtitle, LinearLayout.LayoutParams(-1, -2))
        content.addView(codeInput, LinearLayout.LayoutParams(-1, dp(58)))
        content.addView(activateButton, LinearLayout.LayoutParams(-1, dp(54)).apply {
            topMargin = dp(12)
        })
        content.addView(status, LinearLayout.LayoutParams(-1, -2))

        val scroll = ScrollView(this).apply {
            isFillViewport = true
            clipToPadding = false
            setBackgroundColor(black)
            addView(content, ScrollView.LayoutParams(-1, -2))
        }
        setContentView(scroll)
    }

    private fun rounded(color: Int, stroke: Int, strokeWidth: Int): GradientDrawable =
        GradientDrawable().apply {
            setColor(color)
            cornerRadius = dp(12).toFloat()
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