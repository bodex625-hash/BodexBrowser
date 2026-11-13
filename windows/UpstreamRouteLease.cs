using System.Diagnostics;
using System.Net;
using System.Security;

namespace HBProxy.Windows;

/// <summary>
/// Owns only the exact host route it creates for the upstream SOCKS endpoint.
/// It does not alter default routes, DNS, or firewall policy.
/// </summary>
public sealed class UpstreamRouteLease : IAsyncDisposable
{
    private readonly string _destinationPrefix;
    private readonly int _interfaceIndex;
    private readonly string _nextHop;
    private readonly int _routeMetric;
    private bool _released;

    private UpstreamRouteLease(
        string destinationPrefix,
        int interfaceIndex,
        string nextHop,
        int routeMetric)
    {
        _destinationPrefix = destinationPrefix;
        _interfaceIndex = interfaceIndex;
        _nextHop = nextHop;
        _routeMetric = routeMetric;
    }

    public static async Task<UpstreamRouteLease?> AcquireAsync(
        UpstreamRoutePlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.RequiresHostRoute)
            return null;

        var prefix = plan.DestinationPrefix;
        var nextHop = plan.NextHop.ToString();
        var interfaceIndex = plan.InterfaceIndex;

        // IPAddress.ToString() and integer formatting produce constrained
        // literals; no user-supplied PowerShell source is accepted here.
        var script = "$ErrorActionPreference='Stop'; " +
            "$prefix='" + prefix + "'; " +
            "$ifIndex=" + interfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + "; " +
            "$nextHop='" + nextHop + "'; " +
            "$metric=-1; " +
            "try { " +
            "$existing=@(Get-NetRoute -PolicyStore ActiveStore -DestinationPrefix $prefix -ErrorAction SilentlyContinue); " +
            "if ($existing.Count -gt 0) { throw 'A host route appeared after the snapshot; refusing to replace it.' }; " +
            "$used=@(Get-NetRoute -PolicyStore ActiveStore -DestinationPrefix $prefix -ErrorAction SilentlyContinue | Select-Object -ExpandProperty RouteMetric); " +
            "$metric=1; while (($used -contains $metric) -and $metric -lt 65535) { $metric++ }; " +
            "if ($used -contains $metric) { throw 'No free route metric is available for the upstream host route.' }; " +
            "New-NetRoute -PolicyStore ActiveStore -DestinationPrefix $prefix -InterfaceIndex $ifIndex -NextHop $nextHop -RouteMetric $metric -ErrorAction Stop | Out-Null; " +
            "$created=@(Get-NetRoute -PolicyStore ActiveStore -DestinationPrefix $prefix -ErrorAction Stop | Where-Object { $_.InterfaceIndex -eq $ifIndex -and $_.NextHop -eq $nextHop -and $_.RouteMetric -eq $metric }); " +
            "if ($created.Count -ne 1) { throw 'The upstream host route could not be verified uniquely.' }; " +
            "Write-Output $metric " +
            "} catch { " +
            "if ($metric -gt 0) { Get-NetRoute -PolicyStore ActiveStore -DestinationPrefix $prefix -ErrorAction SilentlyContinue | Where-Object { $_.InterfaceIndex -eq $ifIndex -and $_.NextHop -eq $nextHop -and $_.RouteMetric -eq $metric } | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue }; " +
            "throw " +
            "}";

        var output = await RunPowerShellAsync(script, cancellationToken);
        if (!int.TryParse(output.Trim(), out var routeMetric) || routeMetric is < 1 or > 65535)
            throw new InvalidOperationException("Windows did not return a valid metric for the created upstream route.");

        return new UpstreamRouteLease(prefix, interfaceIndex, nextHop, routeMetric);
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        if (_released)
            return;

        var script = "$ErrorActionPreference='Stop'; " +
            "$prefix='" + _destinationPrefix + "'; " +
            "$ifIndex=" + _interfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + "; " +
            "$nextHop='" + _nextHop + "'; " +
            "$metric=" + _routeMetric.ToString(System.Globalization.CultureInfo.InvariantCulture) + "; " +
            "$matches=@(Get-NetRoute -PolicyStore ActiveStore -DestinationPrefix $prefix -ErrorAction SilentlyContinue | " +
            "Where-Object { $_.InterfaceIndex -eq $ifIndex -and $_.NextHop -eq $nextHop -and $_.RouteMetric -eq $metric }); " +
            "if ($matches.Count -gt 1) { throw 'More than one route matches the owned upstream route identity; refusing cleanup.' }; " +
            "if ($matches.Count -eq 1) { $matches[0] | Remove-NetRoute -Confirm:$false -ErrorAction Stop }; " +
            "Write-Output 'OK'";

        await RunPowerShellAsync(script, cancellationToken);
        _released = true;
    }

    private static async Task<string> RunPowerShellAsync(
        string script,
        CancellationToken cancellationToken)
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
            throw new InvalidOperationException("Could not start PowerShell for the upstream route operation.");

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
            catch
            {
                // Preserve the original timeout or cancellation exception.
            }

            throw;
        }

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error)
                ? "PowerShell returned an error."
                : error.Trim();
            throw new InvalidOperationException("Upstream route operation failed: " + detail);
        }

        return output;
    }

    public async ValueTask DisposeAsync() => await ReleaseAsync();
}
