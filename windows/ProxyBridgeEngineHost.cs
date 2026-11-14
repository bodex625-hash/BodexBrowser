using System.Runtime.InteropServices;
using System.Text;
using System.Net;
using System.Text.RegularExpressions;

namespace HBProxy.Windows;

/// <summary>
/// Hosts the pinned ProxyBridge native engine in-process. The engine intercepts
/// outbound Windows TCP/UDP flows with WinDivert and relays matching flows over SOCKS5.
/// </summary>
public sealed class ProxyBridgeEngineHost : IAsyncDisposable
{
    private const int ProxyTypeSocks5 = 1;
    private const int RuleProtocolTcp = 0;
    private const int RuleProtocolUdp = 1;
    private const int RuleProtocolBoth = 2;
    private const int RuleActionProxy = 0;
    private const int RuleActionBlock = 2;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint AddProxyConfigDelegate(
        int type,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string host,
        ushort port,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string username,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string password,
        int sendDomainToProxy);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint AddRuleDelegate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string processName,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string targetHosts,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string targetPorts,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string targetDomains,
        int protocol,
        int action,
        uint proxyConfigId,
        int localhostViaProxy);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int TestProxyConfigDelegate(
        uint configId,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string targetHost,
        ushort targetPort,
        ProxyTestLogCallback callback,
        IntPtr user);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ProxyTestLogCallback(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string line,
        IntPtr user);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeLogCallback([MarshalAs(UnmanagedType.LPUTF8Str)] string message);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetLogCallbackDelegate(NativeLogCallback? callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int StartDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int StopDelegate();

    private IntPtr _library;
    private IntPtr _winDivertLibrary;
    private AddProxyConfigDelegate? _addProxyConfig;
    private AddRuleDelegate? _addRule;
    private TestProxyConfigDelegate? _testProxy;
    private StartDelegate? _start;
    private StopDelegate? _stop;
    private SetLogCallbackDelegate? _setLogCallback;
    private bool _running;
    private bool _udpEnabled;
    private string _lastDiagnostics = "";

    public bool IsRunning => _running;
    public bool UdpEnabled => _udpEnabled;
    public string LastDiagnostics => _lastDiagnostics;

    public Task StartAsync(Socks5Profile profile) =>
        Task.Run(() => Start(profile, null, null));

    // Runs after the SOCKS5 reachability/authentication test and rule setup,
    // but immediately before WinDivert starts intercepting packets.
    public Task StartAsync(Socks5Profile profile, Action beforeNativeStart) =>
        Task.Run(() => Start(profile, beforeNativeStart, null));

    public Task StartAsync(Socks5Profile profile, Action beforeNativeStart, Action<IPAddress, int> onUdpRelayDiscovered) =>
        Task.Run(() => Start(profile, beforeNativeStart, onUdpRelayDiscovered));

    public Task StopAsync() =>
        Task.Run(Stop);

    private void Start(Socks5Profile profile, Action? beforeNativeStart, Action<IPAddress, int>? onUdpRelayDiscovered)
    {
        if (_running)
            throw new InvalidOperationException("ProxyBridge is already running.");

        profile.Validate();

        var log = new StringBuilder();
        bool startAttempted = false;
        int udpRelayFirewallReady = 0;
        NativeLogCallback nativeLog = message =>
        {
            try
            {
                if (!_udpEnabled || !profile.EnableUdp) return;
                var match = Regex.Match(message ?? "", @"UDP relay at (?<ip>\d{1,3}(?:\.\d{1,3}){3}):(?<port>\d+)", RegexOptions.CultureInvariant);
                if (!match.Success) return;
                var relayIp = IPAddress.Parse(match.Groups["ip"].Value);
                var relayPort = int.Parse(match.Groups["port"].Value, System.Globalization.CultureInfo.InvariantCulture);
                onUdpRelayDiscovered?.Invoke(relayIp, relayPort);
                if (onUdpRelayDiscovered != null) Interlocked.Exchange(ref udpRelayFirewallReady, 1);
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref udpRelayFirewallReady, 0);
                lock (log) log.AppendLine("UDP relay firewall exemption failed: " + ex.Message);
            }
        };
        ProxyTestLogCallback callback = (line, _) =>
        {
            lock (log)
                log.AppendLine(line);
        };

        try
        {
            LoadEngine();
            uint configId = _addProxyConfig!(
                ProxyTypeSocks5,
                profile.ServerAddress,
                checked((ushort)profile.Port),
                profile.Username ?? "",
                profile.Password ?? "",
                1);

            if (configId == 0)
                throw new InvalidOperationException("ProxyBridge could not register the SOCKS5 server.");

            // This performs a real SOCKS5 CONNECT/authentication test and separately
            // probes UDP ASSOCIATE. UDP is enabled only when the server grants it.
            int testResult = _testProxy!(configId, "example.com", 80, callback, IntPtr.Zero);
            lock (log)
                _lastDiagnostics = log.ToString();

            if (testResult != 0)
                throw new InvalidOperationException(
                    "The SOCKS5 connection test failed. Check the server, port, credentials and internet access.\n\n" +
                    _lastDiagnostics);

            _udpEnabled = profile.EnableUdp &&
                _lastDiagnostics.Contains("UDP payload probe passed", StringComparison.OrdinalIgnoreCase);

            if (_udpEnabled)
            {
                uint ruleId = _addRule!(
                    "*", "*", "*", "*",
                    RuleProtocolBoth,
                    RuleActionProxy,
                    configId,
                    0); // keep loopback/localhost traffic local

                if (ruleId == 0)
                    throw new InvalidOperationException("ProxyBridge could not create the default all-applications TCP/UDP proxy rule.");
            }
            else
            {
                // Keep loopback UDP available for the local DNS forwarder. A global
                // ProxyBridge UDP BLOCK rule also blocks Windows DNS requests to
                // 127.0.0.1/::1 before they can reach our local listener. Public UDP
                // remains fail-closed because the Windows firewall lease blocks all
                // public destinations outside the SOCKS upstream endpoint.
                uint tcpProxyRule = _addRule!(
                    "*", "*", "*", "*",
                    RuleProtocolTcp,
                    RuleActionProxy,
                    configId,
                    0);

                if (tcpProxyRule == 0)
                    throw new InvalidOperationException("ProxyBridge could not create the default TCP proxy rule.");
            }

            // The firewall lease is installed here, after the proxy test has
            // completed its local DNS lookup, but before any app packets are captured.
            beforeNativeStart?.Invoke();

            // The native relay thread reports its negotiated UDP endpoint synchronously.
            // Its callback updates the UDP-only firewall exclusion before packet workers start.
            _setLogCallback!(nativeLog);

            // Mark the native start as attempted before crossing the unmanaged boundary.
            // A native start routine can partially initialize worker threads and then
            // report failure (or throw through the interop boundary); the catch path
            // must still call ProxyBridge_Stop to unwind that partial initialization.
            startAttempted = true;
            if (_start!() == 0)
                throw new InvalidOperationException(
                    "ProxyBridge could not start WinDivert. Run H.B Proxy as Administrator and check that the Windows driver is present.");

            _running = true;
            _udpEnabled = _udpEnabled && Volatile.Read(ref udpRelayFirewallReady) == 1;
            if (profile.EnableUdp && !_udpEnabled)
                lock (log) _lastDiagnostics += Environment.NewLine + "UDP data path is fail-closed: relay endpoint was not confirmed in the firewall.";
        }
        catch
        {
            bool stopped = true;
            if (startAttempted)
            {
                // ProxyBridge_Stop returns FALSE when the native engine is
                // already stopped; either a TRUE result or a clean FALSE result
                // means the DLL is no longer executing its interception threads.
                try { _stop?.Invoke(); }
                catch { stopped = false; }
            }

            // If native cleanup cannot be confirmed, keep the DLL loaded and let
            // the caller retry StopAsync instead of unloading a possibly live engine.
            _running = startAttempted && !stopped;
            if (!_running)
                ReleaseEngine();
            throw;
        }
        finally
        {
            GC.KeepAlive(callback);
            GC.KeepAlive(nativeLog);
        }
    }

    private void LoadEngine()
    {
        if (_library != IntPtr.Zero)
            return;

        string directory = AppContext.BaseDirectory;
        string corePath = Path.Combine(directory, "ProxyBridgeCore.dll");
        string divertDll = Path.Combine(directory, "WinDivert.dll");
        string divertDriver = Path.Combine(directory, "WinDivert64.sys");

        if (!File.Exists(corePath) || !File.Exists(divertDll) || !File.Exists(divertDriver))
            throw new FileNotFoundException(
                "The bundled ProxyBridge engine is incomplete. ProxyBridgeCore.dll, WinDivert.dll and WinDivert64.sys must be beside HBProxy.exe.");

        // Load the adjacent WinDivert DLL explicitly before the core so the native
        // dependency is resolved from the verified application directory.
        _winDivertLibrary = NativeLibrary.Load(divertDll);
        _library = NativeLibrary.Load(corePath);
        _addProxyConfig = Load<AddProxyConfigDelegate>("ProxyBridge_AddProxyConfig");
        _addRule = Load<AddRuleDelegate>("ProxyBridge_AddRule");
        _testProxy = Load<TestProxyConfigDelegate>("ProxyBridge_TestProxyConfigEx");
        _start = Load<StartDelegate>("ProxyBridge_Start");
        _stop = Load<StopDelegate>("ProxyBridge_Stop");
        _setLogCallback = Load<SetLogCallbackDelegate>("ProxyBridge_SetLogCallback");
    }

    private T Load<T>(string export) where T : Delegate
    {
        IntPtr address = NativeLibrary.GetExport(_library, export);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private void Stop()
    {
        if (!_running)
        {
            ReleaseEngine();
            return;
        }

        // The native API returns FALSE if it has already stopped (for example,
        // after an unexpected engine exit). That is a stopped state, not a reason
        // to keep the DLL loaded forever.
        _stop?.Invoke();

        _running = false;
        _udpEnabled = false;
        ReleaseEngine();
    }

    private void ReleaseEngine()
    {
        _addProxyConfig = null;
        _addRule = null;
        _testProxy = null;
        _start = null;
        try { _setLogCallback?.Invoke(null); } catch { }
        _setLogCallback = null;
        _stop = null;

        if (_library != IntPtr.Zero)
        {
            NativeLibrary.Free(_library);
            _library = IntPtr.Zero;
        }
        if (_winDivertLibrary != IntPtr.Zero)
        {
            NativeLibrary.Free(_winDivertLibrary);
            _winDivertLibrary = IntPtr.Zero;
        }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}
