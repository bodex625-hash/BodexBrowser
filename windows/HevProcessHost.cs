using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security;
using System.Text;

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
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HevProcessHost()
    {
        _runtimeDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bodex", "HBProxy", "runtime");
    }

    public bool IsRunning => _process is { HasExited: false };

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

            Directory.CreateDirectory(_runtimeDirectory);
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
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }

        if (_configPath is not null)
        {
            try { File.Delete(_configPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _configPath = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }
}
