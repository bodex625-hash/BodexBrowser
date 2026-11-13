# H.B Proxy for Windows x64

## Requirements
- Windows 10/11 x64.
- Administrator approval at launch (required for route and firewall protection).
- A SOCKS5 server reachable by a literal IPv4 or IPv6 address.
- SOCKS5 UDP ASSOCIATE support. UDP is required for protected DNS.

## Build output
The Windows workflow publishes a self-contained application and packages the pinned HEV tunnel engine with its matching Wintun DLL. Download the `hb-proxy-windows-x64-shell` artifact from the latest successful **Windows desktop build** workflow run, then extract the archive.

## Use
1. Launch `HBProxy.exe` and approve the Windows elevation prompt.
2. Enter the SOCKS5 server IP, port, and optional username/password.
3. Select **Save Settings** if you want to retain the profile.
4. Select **Connect**. The client installs its upstream bypass route, applies the outbound firewall kill switch, configures DNS on the HBProxy adapter, starts HEV, installs IPv4/IPv6 tunnel default routes, and performs an HTTPS egress check.
5. The UI shows **Connected** only after the HTTPS check returns a valid public egress IP.
6. Select **Disconnect** to stop HEV and remove only the routes and firewall rules owned by this session.

Credentials are stored with Windows DPAPI. The temporary HEV configuration is written under the current user's LocalAppData folder with a restricted ACL and deleted during cleanup.

## Recovery
If Windows or the application is forcibly terminated during a session, relaunch H.B Proxy. Startup recovery stops leftover HEV processes, restores the HBProxy DNS assignment, removes HBProxy default routes, deletes the temporary configuration, and removes stale H.B Proxy kill-switch firewall rules. If recovery cannot verify cleanup, the application refuses to continue.

## Verification status
CI confirms the Windows x64 application compiles and packages with the pinned HEV/Wintun engine. A successful CI build is not a substitute for a live test with the user's SOCKS5 endpoint; the first connection must pass the in-app HTTPS egress check before it is marked connected.
