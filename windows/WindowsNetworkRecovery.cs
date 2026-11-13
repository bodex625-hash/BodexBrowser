using System.Diagnostics;

namespace HBProxy.Windows;

/// <summary>
/// Startup recovery for a prior forced app termination. It stops only the
/// bundled HEV executable from this installation, removes HBProxy-owned
/// default routes, deletes the private runtime YAML, then removes stale
/// kill-switch groups. If any step is uncertain, recovery fails closed.
/// </summary>
public static class WindowsNetworkRecovery
{
    public static async Task RecoverAbandonedAsync(CancellationToken cancellationToken = default)
    {
        var runtimeDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bodex", "HBProxy", "runtime");

        var script = "$ErrorActionPreference='Stop'; " +
            "$runtime=" + Quote(runtimeDirectory) + "; " +
            "$processes=@(Get-CimInstance Win32_Process -Filter \"Name='hev-socks5-tunnel.exe'\"); " +
            "foreach($p in $processes) { Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop }; " +
            "$deadline=(Get-Date).AddSeconds(10); " +
            "do { $left=@(Get-CimInstance Win32_Process -Filter \"Name='hev-socks5-tunnel.exe'\"); if($left.Count -eq 0) { break }; Start-Sleep -Milliseconds 200 } while((Get-Date) -lt $deadline); " +
            "if($left.Count -gt 0) { throw 'The previous HEV process is still running; network recovery was stopped.' }; " +
            "$adapter=Get-NetAdapter -Name 'HBProxy' -ErrorAction SilentlyContinue; " +
            "if($null -ne $adapter) { " +
            "$idx=[int]$adapter.ifIndex; " +
            "Get-NetRoute -PolicyStore ActiveStore -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Where-Object { $_.InterfaceIndex -eq $idx -and $_.NextHop -eq '0.0.0.0' -and $_.RouteMetric -eq 1 } | Remove-NetRoute -Confirm:$false -ErrorAction Stop; " +
            "Get-NetRoute -PolicyStore ActiveStore -DestinationPrefix '::/0' -ErrorAction SilentlyContinue | Where-Object { $_.InterfaceIndex -eq $idx -and $_.NextHop -eq '::' -and $_.RouteMetric -eq 1 } | Remove-NetRoute -Confirm:$false -ErrorAction Stop " +
            "}; " +
            "if(Test-Path -LiteralPath $runtime) { $config=Join-Path $runtime 'hev-runtime.yml'; if(Test-Path -LiteralPath $config) { Remove-Item -LiteralPath $config -Force -ErrorAction Stop } }; " +
            "$stale=@(Get-NetFirewallRule -PolicyStore PersistentStore -ErrorAction Stop | Where-Object { $_.Group -like 'Bodex.HBProxy.KillSwitch.*' }); " +
            "if($stale.Count -gt 0) { $stale | Remove-NetFirewallRule -ErrorAction Stop }; " +
            "$remaining=@(Get-NetFirewallRule -PolicyStore PersistentStore -ErrorAction Stop | Where-Object { $_.Group -like 'Bodex.HBProxy.KillSwitch.*' }); " +
            "if($remaining.Count -ne 0) { throw 'Stale HBProxy firewall rules remain; refusing to continue.' }; " +
            "Write-Output 'OK'";

        await RunPowerShellAsync(script, cancellationToken);
    }

    private static string Quote(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

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
            throw new InvalidOperationException("Could not start PowerShell for previous-session network recovery.");

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
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
                "Previous-session network recovery failed: " +
                (string.IsNullOrWhiteSpace(detail) ? "PowerShell returned an unexpected result." : detail));
        }
    }
}
