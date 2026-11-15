plugins {
    id("com.android.application")
    id("com.cefrium") version "0.19.0"
}

android {
    namespace = "com.bodex.browser"
    compileSdk = 37

    defaultConfig {
        applicationId = "com.hb.browser"
        minSdk = 29
        targetSdk = 35
        versionCode = 6
        versionName = "1.1.1"
        buildConfigField("String", "HB_LICENSE_API", "\"https://hb-browser-license.bodex625.workers.dev/api\"")
        ndk { abiFilters += setOf("arm64-v8a") }
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

    buildFeatures { buildConfig = true }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

dependencies {
    implementation("com.cefrium:cefrium-sdk:0.19.0")
    implementation("androidx.appcompat:appcompat:1.7.0")
    implementation("androidx.mediarouter:mediarouter:1.7.0")
    implementation("androidx.core:core:1.16.0")
    implementation("androidx.browser:browser:1.8.0")
}
