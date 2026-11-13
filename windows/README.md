# H.B Proxy for Windows x64

H.B Proxy uses the pinned ProxyBridge native interception engine (MIT) and WinDivert to route Windows application traffic through the configured SOCKS5 server. It does not use the previous HEV/Wintun full-device tunnel.

## Requirements
- Windows 10/11 x64.
- Administrator approval at launch (required by WinDivert).
- A reachable SOCKS5 server with valid credentials, if authentication is required.

## Default routing policy
- A catch-all `*` rule routes all matching Windows processes and destinations through the configured SOCKS5 server.
- Loopback/localhost, broadcast, multicast, link-local and DHCP traffic retain the engine's built-in direct handling.
- The engine excludes the configured upstream proxy endpoint from interception to prevent a proxy loop.
- SOCKS5 remote DNS is requested so destination hostnames can be resolved by the proxy where the application/engine has the hostname.
- UDP ASSOCIATE is tested before starting. UDP is routed through SOCKS5 only when the server grants UDP ASSOCIATE and the UDP option is selected.
- If UDP is unavailable or disabled, UDP traffic is blocked rather than silently sent directly. This is fail-closed; applications that require UDP may not work in TCP-only mode.

## DNS and application compatibility
ProxyBridge learns hostname-to-IP mappings from ordinary, unencrypted DNS responses. Browser Secure DNS / DoH can prevent that hostname learning, although the browser's HTTPS connection itself still matches the catch-all TCP proxy rule. When UDP is unavailable, standard UDP DNS is blocked to avoid a direct DNS leak; applications using DoH or TCP DNS can continue if their resolver supports it. This can affect Windows applications that depend exclusively on ordinary UDP DNS.

Most TCP applications are expected to work without per-application configuration. Some games, anti-cheat systems, kernel-level network components, raw-IP protocols, and applications that depend on UDP may be incompatible. No packet interception tool can guarantee compatibility with every Windows program.

## Build output
The Windows workflow checks out ProxyBridge at a pinned commit, verifies the WinDivert 2.2.2-A archive SHA-256, builds the native engine, publishes H.B Proxy, and bundles:
- `ProxyBridgeCore.dll`
- `WinDivert.dll`
- `WinDivert64.sys`

Third-party license notices are included in `THIRD-PARTY-NOTICES`.

## Use
1. Launch `HBProxy.exe` and approve the Windows elevation prompt.
2. Enter the SOCKS5 server IP, port, and optional username/password.
3. Select **Save Settings** if you want to retain the profile.
4. Select **Connect**. H.B Proxy tests SOCKS5 connectivity and authentication, probes UDP ASSOCIATE, then starts the all-applications interception rule.
5. The status reports whether TCP + UDP or TCP-only fail-closed routing is active.
6. Select **Disconnect** to stop the native interception engine.

Credentials continue to be stored with Windows DPAPI. The native engine receives them in process memory only; H.B Proxy does not write a plaintext ProxyBridge profile to disk.

## Recovery
The existing startup recovery remains enabled to remove stale routes/firewall rules left by previous H.B Proxy HEV/Wintun sessions before the new interception engine starts.

## Verification status
A successful CI build proves compilation and packaging only. It does not prove that every application works with a particular SOCKS5 provider. The first live test must verify ordinary browser access, remote public IP, DNS behavior, UDP behavior (if supported), and clean disconnect on the target Windows machine.
