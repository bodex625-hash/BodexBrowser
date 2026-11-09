# Bodex Browser — Android starter

An Android-native Kotlin + Jetpack Compose starter for a proxy browser.

## Current state
- Dark Arabic-first dashboard UI.
- SOCKS5 / HTTP profile form and basic field validation.
- Android `VpnService` declaration and a deliberately non-functional service scaffold.
- Browser address-bar UI placeholder.

**Important:** this is not yet a working VPN or browser build. The connect and open-site controls are intentionally disabled. No traffic is intercepted, forwarded, or claimed to be protected. The TUN-to-proxy engine, WebView, encrypted credential storage, DNS handling, IPv6 policy, UDP behavior, leak protection, and device testing still need implementation.

## Build
1. Install Android Studio with Android SDK Platform 35 and JDK 17.
2. Open this folder as a project.
3. Let Android Studio sync Gradle.
4. Run the `app` configuration on an Android device or emulator.

A Gradle wrapper is not included in this starter; Android Studio can import the project and configure Gradle.

## Planned implementation phases
1. Integrate and audit a TUN-to-proxy engine supporting SOCKS5 and HTTP CONNECT on Android.
2. Implement `VpnService` lifecycle, `protect()` for upstream sockets, foreground notification, routing, DNS, IPv4/IPv6, and fail-closed behavior.
3. Add encrypted proxy profiles and connection tests.
4. Add WebView browser with tabs, history, bookmarks, and safe URL handling.
5. Test on real devices and networks, including Wi-Fi/mobile handoff and proxy authentication.
6. Build and sign release APK.

## Technical references
- Android VPN API: https://developer.android.com/reference/android/net/VpnService
- Android VPN guide: https://developer.android.com/develop/connectivity/vpn
- tun2proxy project: https://github.com/tun2proxy/tun2proxy
