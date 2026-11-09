# Bodex Browser — build from an Android phone

This project is an Android-native Kotlin/Jetpack Compose starter. It is **not yet a functioning VPN or production browser**: the VPN service is only a safe scaffold, and the browser UI is a placeholder.

## Phone-only cloud build (GitHub Actions)

1. Download and extract `BodexBrowser-starter.zip` on your Android phone.
2. In a mobile browser, sign in to GitHub and create a new repository named `BodexBrowser`.
3. Upload the extracted project files and folders to the repository root. Make sure `.github/workflows/android.yml` is included. GitHub's web upload flow may be easier in your browser's desktop-site mode.
4. Commit the files to the `main` branch.
5. Open the repository's **Actions** tab. Select **Build Bodex Browser APK**, then **Run workflow** if it did not start automatically.
6. Wait for the workflow to finish successfully. Open that run, scroll to **Artifacts**, and download `BodexBrowser-debug-apk`.
7. Extract the artifact ZIP on your phone. Install `app-debug.apk`. Android may ask you to allow installs from that browser or file manager.

## Important limitations

- The current APK is only a UI starter. It does not route phone traffic through a proxy.
- Do not enter real proxy credentials into this starter; persistent encrypted storage has not been implemented.
- A debug APK is for testing, not a signed production release.
- Before enabling full-device proxying, implement and test a TUN-to-proxy engine, DNS and IPv6 handling, UDP behavior, upstream socket protection, foreground VPN notification, reconnection, and fail-closed leak protection.
