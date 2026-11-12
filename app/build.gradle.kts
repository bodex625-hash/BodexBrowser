plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
    id("com.cefrium") version "0.19.0"
}

android {
    namespace = "com.bodex.browser"
    compileSdk = 37

    defaultConfig {
        applicationId = "com.bodex.browser"
        minSdk = 29
        targetSdk = 35
        versionCode = 4
        versionName = "1.0.0-integrated"
        buildConfigField("String", "HB_LICENSE_API", "\"https://hb-browser-license.bodex625.workers.dev/api\"")
        ndk { abiFilters += setOf("arm64-v8a") }
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
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
    implementation("com.cefrium:cefrium-sdk:0.19.0")
    implementation("androidx.appcompat:appcompat:1.7.0")
    implementation("androidx.mediarouter:mediarouter:1.7.0")
    implementation("androidx.core:core:1.16.0")
    implementation("androidx.browser:browser:1.8.0")

    debugImplementation("androidx.compose.ui:ui-tooling")
}
