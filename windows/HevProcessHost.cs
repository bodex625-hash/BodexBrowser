using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;

namespace HBProxy.Windows;

/// <summary>
/// Owns the HEV process and its private runtime configuration.
/// This class deliberately does not alter Windows routes or DNS.
/// </summary>
public sealed class HevProcessHost : IAsyncDisposable
{
    private readonly string _runtimeDirectory;
    private Process? _process;
    private string? _configPath;
    private UpstreamRouteLease? _upstreamRouteLease;
    private WindowsNetworkSnapshot? _networkSnapshot;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HevProcessHost()
    {
        _runtimeDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bodex", "HBProxy", "runtime");
    }

    public bool IsRunning => _process is { HasExited: false };

    private void RestrictRuntimeDirectory()
    {
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Could not determine the current Windows user SID.");

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(currentUser);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));

        new DirectoryInfo(_runtimeDirectory).SetAccessControl(security);
    }

    public async Task StartAsync(Socks5Profile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning)
                throw new InvalidOperationException("The HEV tunnel process is already running.");

            var executable = Path.Combine(AppContext.BaseDirectory, "hev-socks5-tunnel.exe");
            var wintun = Path.Combine(AppContext.BaseDirectory, "wintun.dll");
            if (!File.Exists(executable) || !File.Exists(wintun))
                throw new FileNotFoundException("The HEV engine and matching x64 wintun.dll must be installed beside HBProxy.exe.");

            // Preserve a direct physical path to the SOCKS5 server before HEV
            // installs its tunnel routes. Only create a host route when the
            // pre-tunnel route table does not already contain one.
            if (!IPAddress.TryParse(profile.ServerAddress, out var serverAddress))
                throw new InvalidOperationException("The SOCKS5 server address is not a valid IP literal.");
            _networkSnapshot = await WindowsNetworkSnapshot.CaptureAsync(cancellationToken);
            var routePlan = UpstreamRoutePlan.Create(_networkSnapshot.Routes, serverAddress);
            _upstreamRouteLease = await UpstreamRouteLease.AcquireAsync(routePlan, cancellationToken);

            Directory.CreateDirectory(_runtimeDirectory);
            RestrictRuntimeDirectory();
            _configPath = Path.Combine(_runtimeDirectory, "hev-runtime.yml");
            await File.WriteAllTextAsync(_configPath, HevConfigWriter.Build(profile), new UTF8Encoding(false), cancellationToken);

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(_configPath);

            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, e) => { /* Never write tunnel output: it may contain sensitive diagnostics. */ };
            _process.ErrorDataReceived += (_, e) => { /* Never write tunnel output: it may contain sensitive diagnostics. */ };

            if (!_process.Start())
                throw new InvalidOperationException("Windows could not start the HEV tunnel process.");

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_process.HasExited)
                    throw new InvalidOperationException($"HEV exited during startup (code {_process.ExitCode}).");

                if (NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                    string.Equals(n.Name, "HBProxy", StringComparison.OrdinalIgnoreCase) &&
                    n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                    return;

                await Task.Delay(250, cancellationToken);
            }

            throw new TimeoutException("HEV did not create the HBProxy network interface within 15 seconds.");
        }
        catch
        {
            await StopCoreAsync();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        if (_process is not null)
        {
            // Never discard the process handle or its credential-bearing config
            // until Windows confirms that the tunnel process has exited.
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);

            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (!_process.HasExited)
                throw new TimeoutException("HEV is still running; its runtime configuration was retained for safe recovery.");

            _process.Dispose();
            _process = null;
        }

        // Release only the exact host route created by this process, and only
        // after HEV has definitely exited. If cleanup fails, retain the lease
        // and runtime config so a later StopAsync can retry safely.
        if (_upstreamRouteLease is not null)
        {
            await _upstreamRouteLease.ReleaseAsync();
            _upstreamRouteLease = null;
        }

        if (_configPath is not null)
        {
            try
            {
                File.Delete(_configPath);
                _configPath = null;
            }
            catch (FileNotFoundException)
            {
                _configPath = null;
            }
            // On any other deletion failure retain the path so cleanup can be retried.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }
}
