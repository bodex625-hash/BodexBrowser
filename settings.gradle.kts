pluginManagement {
    repositories {
        maven("https://maven.cefrium.com")
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
        maven("https://maven.cefrium.com")
    }
}

rootProject.name = "BodexBrowser"
include(":app")
