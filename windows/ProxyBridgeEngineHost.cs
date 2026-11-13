using System.Runtime.InteropServices;
using System.Text;

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
    private delegate int StartDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int StopDelegate();

    private IntPtr _library;
    private AddProxyConfigDelegate? _addProxyConfig;
    private AddRuleDelegate? _addRule;
    private TestProxyConfigDelegate? _testProxy;
    private StartDelegate? _start;
    private StopDelegate? _stop;
    private bool _running;
    private bool _udpEnabled;
    private string _lastDiagnostics = "";

    public bool IsRunning => _running;
    public bool UdpEnabled => _udpEnabled;
    public string LastDiagnostics => _lastDiagnostics;

    public Task StartAsync(Socks5Profile profile) =>
        Task.Run(() => Start(profile));

    public Task StopAsync() =>
        Task.Run(Stop);

    private void Start(Socks5Profile profile)
    {
        if (_running)
            throw new InvalidOperationException("ProxyBridge is already running.");

        profile.Validate();
        LoadEngine();

        var log = new StringBuilder();
        ProxyTestLogCallback callback = (line, _) =>
        {
            lock (log)
                log.AppendLine(line);
        };

        try
        {
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
                _lastDiagnostics.Contains("UDP is supported by this proxy", StringComparison.OrdinalIgnoreCase);

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
                // Fail closed for UDP when the upstream cannot relay it (or the user
                // disabled it). Never silently send unsupported UDP outside SOCKS5.
                uint udpBlockRule = _addRule!(
                    "*", "*", "*", "*",
                    RuleProtocolUdp,
                    RuleActionBlock,
                    configId,
                    0);
                uint tcpProxyRule = _addRule!(
                    "*", "*", "*", "*",
                    RuleProtocolTcp,
                    RuleActionProxy,
                    configId,
                    0);

                if (udpBlockRule == 0 || tcpProxyRule == 0)
                    throw new InvalidOperationException("ProxyBridge could not create the default fail-closed TCP/UDP rules.");
            }

            if (_start!() == 0)
                throw new InvalidOperationException(
                    "ProxyBridge could not start WinDivert. Run H.B Proxy as Administrator and check that the Windows driver is present.");

            _running = true;
        }
        catch
        {
            if (_running)
            {
                try { _stop?.Invoke(); } catch { }
                _running = false;
            }
            ReleaseEngine();
            throw;
        }
        finally
        {
            GC.KeepAlive(callback);
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

        _library = NativeLibrary.Load(corePath);
        _addProxyConfig = Load<AddProxyConfigDelegate>("ProxyBridge_AddProxyConfig");
        _addRule = Load<AddRuleDelegate>("ProxyBridge_AddRule");
        _testProxy = Load<TestProxyConfigDelegate>("ProxyBridge_TestProxyConfigEx");
        _start = Load<StartDelegate>("ProxyBridge_Start");
        _stop = Load<StopDelegate>("ProxyBridge_Stop");
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

        if (_stop?.Invoke() == 0)
            throw new InvalidOperationException("ProxyBridge did not confirm that the interception engine stopped.");

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
        _stop = null;

        if (_library != IntPtr.Zero)
        {
            NativeLibrary.Free(_library);
            _library = IntPtr.Zero;
        }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}
