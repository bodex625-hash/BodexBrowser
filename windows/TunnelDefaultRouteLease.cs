using System.Diagnostics;
using System.Globalization;

namespace HBProxy.Windows;

/// <summary>
/// Transactional owner for the two full-tunnel default routes.
/// Original physical routes are never removed or edited. Only the exact
/// ActiveStore routes created here are removed during rollback/shutdown.
/// </summary>
public sealed class TunnelDefaultRouteLease : IAsyncDisposable
{
    private readonly int _interfaceIndex;
    private readonly int _routeMetric;
    private bool _released;

    private TunnelDefaultRouteLease(int interfaceIndex, int routeMetric)
    {
        _interfaceIndex = interfaceIndex;
        _routeMetric = routeMetric;
    }

    public static async Task<TunnelDefaultRouteLease> AcquireAsync(
        int interfaceIndex,
        CancellationToken cancellationToken = default)
    {
        if (interfaceIndex <= 0)
            throw new ArgumentOutOfRangeException(nameof(interfaceIndex));

        // Route metric is intentionally fixed and low. The PowerShell
        // transaction verifies the resulting combined metric beats every
        // pre-existing default route before it reports success.
        const int metric = 1;
        var script =
            "$ErrorActionPreference='Stop'; " +
            "$ifIndex=" + interfaceIndex.ToString(CultureInfo.InvariantCulture) + "; " +
            "$metric=" + metric.ToString(CultureInfo.InvariantCulture) + "; " +
            "$v4='0.0.0.0/0'; $v6='::/0'; " +
            "$created4=$false; $created6=$false; " +
            "try { " +
            "$allInterfaces=@(Get-NetIPInterface -PolicyStore ActiveStore -ErrorAction Stop); " +
            "$interfaces=@($allInterfaces | Where-Object {$_.InterfaceIndex -eq $ifIndex}); " +
            "$i4=@($interfaces | Where-Object {$_.AddressFamily -eq 'IPv4'}); " +
            "$i6=@($interfaces | Where-Object {$_.AddressFamily -eq 'IPv6'}); " +
            "if ($i4.Count -ne 1 -or $i6.Count -ne 1) { throw 'The HBProxy interface is missing an IP family.' }; " +
            "$old4=@(Get-NetRoute -PolicyStore ActiveStore -DestinationPrefix $v4 -ErrorAction SilentlyContinue); " +
            "$old6=@(Get-NetRoute -PolicyStore ActiveStore -DestinationPrefix $v6 -ErrorAction SilentlyContinue); " +
            "$best4=2147483647; foreach($r in $old4) { $m=@($interfaces | Where-Object {$_.InterfaceIndex -eq $r.InterfaceIndex -and $_.AddressFamily -eq 'IPv4'} | Select-Object -First 1); if($m.Count -eq 0) {$im=0} else {$im=[int]$m[0].InterfaceMetric}; $t=[int]$r.RouteMetric+$im; if($t -lt $best4){$best4=$t} }; " +
            "$best6=2147483647; foreach($r in $old6) { $m=@(Get-NetIPInterface -InterfaceIndex $r.InterfaceIndex -AddressFamily IPv6 -ErrorAction SilentlyContinue | Select-Object -First 1); if($m.Count -eq 0) {$im=0} else {$im=[int]$m[0].InterfaceMetric}; $t=[int]$r.RouteMetric+$im; if($t -lt $best6){$best6=$t} }; " +
            "$new4=$metric+[int]$i4[0].InterfaceMetric; $new6=$metric+[int]$i6[0].InterfaceMetric; " +
            "if($new4 -ge $best4 -or $new6 -ge $best6) { throw 'HBProxy cannot win both default-route metrics safely.' }; " +
            "if(@($old4 | Where-Object {$_.InterfaceIndex -eq $ifIndex}).Count -gt 0 -or @($old6 | Where-Object {$_.InterfaceIndex -eq $ifIndex}).Count -gt 0) { throw 'An HBProxy default route already exists; refusing to claim it.' }; " +
            "New-NetRoute -PolicyStore ActiveStore -DestinationPrefix $v4 -InterfaceIndex $ifIndex -NextHop '0.0.0.0' -RouteMetric $metric -ErrorAction Stop | Out-Null; $created4=$true; " +
            "New-NetRoute -PolicyStore ActiveStore -DestinationPrefix $v6 -InterfaceIndex $ifIndex -NextHop '::' -RouteMetric $metric -ErrorAction Stop | Out-Null; $created6=$true; " +
            "$check4=@(Get-NetRoute -PolicyStore ActiveStore -DestinationPrefix $v4 -ErrorAction Stop | Where-Object {$_.InterfaceIndex -eq $ifIndex -and $_.NextHop -eq '0.0.0.0' -and $_.RouteMetric -eq $metric}); " +
            "$check6=@(Get-NetRoute -PolicyStore ActiveStore -DestinationPrefix $v6 -ErrorAction Stop | Where-Object {$_.InterfaceIndex -eq $ifIndex -and $_.NextHop -eq '::' -and $_.RouteMetric -eq $metric}); " +
            "if($check4.Count -ne 1 -or $check6.Count -ne 1) { throw 'Tunnel default routes could not be verified uniquely.' }; " +
            "Write-Output 'OK' " +
            "} catch { " +
            "if($created6) { Get-NetRoute -PolicyStore ActiveStore -DestinationPrefix $v6 -ErrorAction SilentlyContinue | Where-Object {$_.InterfaceIndex -eq $ifIndex -and $_.NextHop -eq '::' -and $_.RouteMetric -eq $metric} | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue }; " +
            "if($created4) { Get-NetRoute -PolicyStore ActiveStore -DestinationPrefix $v4 -ErrorAction SilentlyContinue | Where-Object {$_.InterfaceIndex -eq $ifIndex -and $_.NextHop -eq '0.0.0.0' -and $_.RouteMetric -eq $metric} | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue }; " +
            "throw }";

        await RunPowerShellAsync(script, cancellationToken);
        return new TunnelDefaultRouteLease(interfaceIndex, metric);
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        if (_released)
            return;

        var script =
            "$ErrorActionPreference='Stop'; " +
            "$ifIndex=" + _interfaceIndex.ToString(CultureInfo.InvariantCulture) + "; " +
            "$metric=" + _routeMetric.ToString(CultureInfo.InvariantCulture) + "; " +
            "$v4=@(Get-NetRoute -PolicyStore ActiveStore -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Where-Object {$_.InterfaceIndex -eq $ifIndex -and $_.NextHop -eq '0.0.0.0' -and $_.RouteMetric -eq $metric}); " +
            "$v6=@(Get-NetRoute -PolicyStore ActiveStore -DestinationPrefix '::/0' -ErrorAction SilentlyContinue | Where-Object {$_.InterfaceIndex -eq $ifIndex -and $_.NextHop -eq '::' -and $_.RouteMetric -eq $metric}); " +
            "if($v4.Count -gt 1 -or $v6.Count -gt 1) { throw 'Duplicate routes match the owned tunnel route identity; refusing cleanup.' }; " +
            "if($v6.Count -eq 1) { $v6[0] | Remove-NetRoute -Confirm:$false -ErrorAction Stop }; " +
            "if($v4.Count -eq 1) { $v4[0] | Remove-NetRoute -Confirm:$false -ErrorAction Stop }; " +
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
            throw new InvalidOperationException("Could not start PowerShell for tunnel route setup.");

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
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
                "Full-tunnel route transaction failed: " +
                (string.IsNullOrWhiteSpace(detail) ? "PowerShell returned an unexpected result." : detail));
        }
    }

    public async ValueTask DisposeAsync() => await ReleaseAsync();
}
