# H.B Proxy for Windows (x64)

This is the Windows companion project for the existing H.B Proxy Android application.
It is intentionally separate from the Android modules and does not add a browser.

## Target
- Windows 11 x64
- HEV SOCKS5 tunnel engine (Windows build, not the Android AAR)
- Wintun virtual network adapter
- SOCKS5 TCP and UDP forwarding
- Full-tunnel IPv4 routing with an explicit bypass route for the upstream SOCKS5 server
- DNS routing through the tunnel and fail-closed disconnect/cleanup behavior
- Dark navy / cyan H.B Proxy interface and eagle-shield brand

## Important implementation constraints
1. Build `hev-socks5-tunnel.exe` for Windows x64 from the upstream source using its documented MSVC + Clang toolchain.
2. Ship the matching signed x64 `wintun.dll` beside the executable, following Wintun's redistribution terms.
3. Run elevated when installing the adapter and changing routes.
4. Before installing a default route, resolve the SOCKS5 endpoint and add a host route to it through the original physical gateway. Otherwise the tunnel can route its own upstream connection into itself.
5. Save the original default route and DNS configuration, and restore them on disconnect, crash recovery, or failed startup. Do not report Connected until the tunnel process is alive, routes are installed, and a real HTTPS/IP test succeeds.
6. Keep IPv6 from bypassing the tunnel: either tunnel IPv6 correctly or explicitly block/disable IPv6 routing while connected, then restore the previous state on disconnect.
7. UDP is enabled only when the SOCKS5 server supports UDP ASSOCIATE. A successful TCP handshake alone does not prove UDP works.

## Subscription compatibility
The Android apps use the existing license API and an Android-specific device identifier derived from Android ID. Windows has a different device identity. Reusing the same activation code on Windows requires the licensing service to explicitly support a Windows device identity and the intended device-seat policy. Do not copy Android's device identifier or claim cross-platform activation works until the server-side behavior is confirmed.

## Delivery gates
- Build the x64 HEV executable and verify its SHA-256.
- Package the matching Wintun x64 DLL and license notices.
- Build the Windows UI installer/package.
- Test install/uninstall, elevation, connect/disconnect, TCP, UDP, DNS, upstream route bypass, IPv6 leak behavior, and recovery after process/network failure on a real Windows 11 x64 machine.
- Do not publish a release as stable until these checks pass.
