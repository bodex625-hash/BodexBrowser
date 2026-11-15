plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.plugin.compose")
}

android {
    namespace = "com.bodex.browser"
    compileSdk = 37

    defaultConfig {
        applicationId = "com.bodex.browser"
        minSdk = 29
        targetSdk = 35
        versionCode = 21
        versionName = "0.3.1"
    }

    // Optional stable signing shared by HB Browser and H.B Proxy.
    // The keystore is injected by GitHub Actions from repository secrets; never commit it.
    val sharedStoreFile = rootProject.file("shared-signing.jks")
    val sharedStorePassword = providers.environmentVariable("HB_STORE_PASSWORD").orNull
    val sharedKeyAlias = providers.environmentVariable("HB_KEY_ALIAS").orNull
    val sharedKeyPassword = providers.environmentVariable("HB_KEY_PASSWORD").orNull
    val hasSharedSigning = sharedStoreFile.exists() &&
        !sharedStorePassword.isNullOrBlank() &&
        !sharedKeyAlias.isNullOrBlank() &&
        !sharedKeyPassword.isNullOrBlank()

    if (hasSharedSigning) {
        signingConfigs.create("shared") {
            storeFile = sharedStoreFile
            storePassword = sharedStorePassword
            keyAlias = sharedKeyAlias
            keyPassword = sharedKeyPassword
        }
        buildTypes.getByName("debug") {
            signingConfig = signingConfigs.getByName("shared")
        }
    }

    buildFeatures { compose = true }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

dependencies {
    implementation(platform("androidx.compose:compose-bom:2024.12.01"))
    implementation("androidx.activity:activity-compose:1.10.0")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.material:material-icons-extended")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.7")
    implementation(files("libs/hev-socks5-tunnel.aar"))
    debugImplementation("androidx.compose.ui:ui-tooling")
}
