pluginManagement {
    repositories {
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
        maven("https://maven.cefrium.com") {
            content { includeGroup("com.cefrium") }
        }
    }
}
rootProject.name = "BodexBrowser"
include(":app", ":browser")
