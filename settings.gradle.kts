pluginManagement {
    repositories {
        maven("https://codeberg.org/api/packages/cefrium/maven") {
            content { includeGroup("com.cefrium") }
        }
        maven("https://maven.cefrium.com") {
            content { includeGroup("com.cefrium") }
        }
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
        maven("https://codeberg.org/api/packages/cefrium/maven") {
            content { includeGroup("com.cefrium") }
        }
        maven("https://maven.cefrium.com") {
            content { includeGroup("com.cefrium") }
        }
    }
}

rootProject.name = "BodexBrowser"
include(":app")
