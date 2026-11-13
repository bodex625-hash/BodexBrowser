using System.Diagnostics;
using System.Globalization;

namespace HBProxy.Windows;

/// <summary>
/// Routes Windows DNS queries to public resolvers through the HBProxy default
/// routes. Only the dedicated tunnel adapter is changed; its DNS setting is
/// reset after the tunnel process and routes are stopped.
/// </summary>
public sealed class TunnelDnsLease : IAsyncDisposable
{
    private readonly int _interfaceIndex;
    private bool _released;

    private TunnelDnsLease(int interfaceIndex) => _interfaceIndex = interfaceIndex;

    public static async Task<TunnelDnsLease> AcquireAsync(
        int interfaceIndex,
        CancellationToken cancellationToken = default)
    {
        if (interfaceIndex <= 0)
            throw new ArgumentOutOfRangeException(nameof(interfaceIndex));

        var script = "$ErrorActionPreference='Stop'; " +
            "$idx=" + interfaceIndex.ToString(CultureInfo.InvariantCulture) + "; " +
            "$adapter=@(Get-NetAdapter -InterfaceIndex $idx -ErrorAction Stop); " +
            "if($adapter.Count -ne 1 -or $adapter[0].Name -ne 'HBProxy') { throw 'DNS can only be assigned to the dedicated HBProxy adapter.' }; " +
            "$dns=@('1.1.1.1','1.0.0.1','2606:4700:4700::1111','2606:4700:4700::1001'); " +
            "Set-DnsClientServerAddress -InterfaceIndex $idx -ServerAddresses $dns -ErrorAction Stop; " +
            "$check=@(Get-DnsClientServerAddress -InterfaceIndex $idx -ErrorAction Stop | ForEach-Object {$_.ServerAddresses} | Where-Object {$_ -in $dns}); " +
            "if($check.Count -lt 4) { throw 'The tunnel DNS resolver addresses could not be verified.' }; " +
            "Write-Output 'OK'";

        try
        {
            await RunPowerShellAsync(script, cancellationToken);
            return new TunnelDnsLease(interfaceIndex);
        }
        catch (Exception setupError)
        {
            try
            {
                var rollback = "$ErrorActionPreference='Stop'; " +
                    "$idx=" + interfaceIndex.ToString(CultureInfo.InvariantCulture) + "; " +
                    "$adapter=@(Get-NetAdapter -InterfaceIndex $idx -ErrorAction SilentlyContinue); " +
                    "if($adapter.Count -gt 0 -and $adapter[0].Name -eq 'HBProxy') { Set-DnsClientServerAddress -InterfaceIndex $idx -ResetServerAddresses -ErrorAction Stop }; " +
                    "Write-Output 'OK'";
                await RunPowerShellAsync(rollback, CancellationToken.None);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(
                    "Tunnel DNS setup failed and its partial assignment could not be restored.",
                    setupError, cleanupError);
            }

            throw;
        }
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        if (_released)
            return;

        var script = "$ErrorActionPreference='Stop'; " +
            "$idx=" + _interfaceIndex.ToString(CultureInfo.InvariantCulture) + "; " +
            "$adapter=@(Get-NetAdapter -InterfaceIndex $idx -ErrorAction SilentlyContinue); " +
            "if($adapter.Count -gt 0 -and $adapter[0].Name -eq 'HBProxy') { Set-DnsClientServerAddress -InterfaceIndex $idx -ResetServerAddresses -ErrorAction Stop }; " +
            "Write-Output 'OK'";

        await RunPowerShellAsync(script, cancellationToken);
        _released = true;
    }

    private static async Task RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("RemoteSigned");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("Could not start PowerShell for tunnel DNS configuration.");

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch { }
            throw;
        }

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0 || !string.Equals(output.Trim(), "OK", StringComparison.Ordinal))
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
            throw new InvalidOperationException(
                "Tunnel DNS configuration failed: " +
                (string.IsNullOrWhiteSpace(detail) ? "PowerShell returned an unexpected result." : detail));
        }
    }

    public async ValueTask DisposeAsync() => await ReleaseAsync();
}
