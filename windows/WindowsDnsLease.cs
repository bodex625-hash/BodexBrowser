using System.Diagnostics;
using System.Text.Json;

namespace HBProxy.Windows;

/// <summary>
/// Temporarily points active Windows network adapters at the local DNS forwarder.
/// The exact pre-session DNS configuration is persisted before any change so a
/// forced process termination can be recovered on the next launch.
/// </summary>
public sealed class WindowsDnsLease : IAsyncDisposable
{
    private sealed record AdapterDns(int InterfaceIndex, string InterfaceAlias, string[] ServerAddresses);
    private static readonly string LeasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bodex", "HBProxy", "dns-lease.json");

    private readonly AdapterDns[] _snapshot;
    private bool _released;

    private WindowsDnsLease(AdapterDns[] snapshot) => _snapshot = snapshot;

    public static async Task<WindowsDnsLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LeasePath)!);
        if (File.Exists(LeasePath))
            throw new InvalidOperationException("A previous DNS lease is still recorded. Restart H.B Proxy to run network recovery before reconnecting.");

        var captured = await WindowsDnsSnapshot.CaptureAsync(cancellationToken);
        var snapshot = captured.Entries
            .GroupBy(e => new { e.InterfaceIndex, e.InterfaceAlias })
            .Select(group => new AdapterDns(
                group.Key.InterfaceIndex,
                group.Key.InterfaceAlias,
                group.SelectMany(e => e.ServerAddresses).Select(a => a.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()))
            .ToArray();

        if (snapshot.Length == 0)
            throw new InvalidOperationException("Windows returned no adapter DNS settings; refusing to change DNS.");

        var json = JsonSerializer.Serialize(snapshot);
        var tempPath = LeasePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, LeasePath, overwrite: true);

        try
        {
            await RunPowerShellAsync(BuildAcquireScript(snapshot), cancellationToken);
            return new WindowsDnsLease(snapshot);
        }
        catch (Exception setupError)
        {
            try
            {
                await RestoreSnapshotAsync(snapshot, CancellationToken.None);
                if (File.Exists(LeasePath)) File.Delete(LeasePath);
            }
            catch (Exception restoreError)
            {
                throw new AggregateException(
                    "DNS setup failed and the original Windows DNS configuration could not be restored. The recovery snapshot was retained.",
                    setupError, restoreError);
            }
            throw;
        }
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        if (_released) return;
        await RestoreSnapshotAsync(_snapshot, cancellationToken);
        if (File.Exists(LeasePath)) File.Delete(LeasePath);
        _released = true;
    }

    public static async Task RecoverAbandonedAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(LeasePath)) return;
        AdapterDns[]? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<AdapterDns[]>(await File.ReadAllTextAsync(LeasePath, cancellationToken));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("The saved DNS recovery file is unreadable. It was retained; refusing to continue.", ex);
        }

        if (snapshot is null || snapshot.Length == 0)
            throw new InvalidOperationException("The saved DNS recovery file is empty or invalid. It was retained; refusing to continue.");

        await RestoreSnapshotAsync(snapshot, cancellationToken);
        File.Delete(LeasePath);
    }

    private static string BuildAcquireScript(AdapterDns[] snapshot)
    {
        var commands = new List<string>
        {
            "$ErrorActionPreference='Stop';",
            "$changed=0;"
        };
        foreach (var item in snapshot)
        {
            if (item.InterfaceAlias.Equals("HBProxy", StringComparison.OrdinalIgnoreCase) ||
                item.InterfaceAlias.StartsWith("Loopback", StringComparison.OrdinalIgnoreCase))
                continue;

            var index = item.InterfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var alias = Quote(item.InterfaceAlias);
            commands.Add(
                "$a=Get-NetAdapter -InterfaceIndex " + index + " -ErrorAction SilentlyContinue; " +
                "if($null -ne $a -and $a.Name -eq " + alias + " -and $a.Status -eq 'Up') { " +
                "Set-DnsClientServerAddress -InterfaceIndex " + index + " -ServerAddresses @('127.0.0.1','::1') -ErrorAction Stop; " +
                "$changed++ }");
        }
        commands.Add("if($changed -lt 1) { throw 'No active network adapter could be assigned to the local DNS forwarder.' };");
        commands.Add("Write-Output 'OK'");
        return string.Join(" ", commands);
    }

    private static Task RestoreSnapshotAsync(AdapterDns[] snapshot, CancellationToken cancellationToken)
    {
        var commands = new List<string> { "$ErrorActionPreference='Stop';" };
        foreach (var item in snapshot)
        {
            var index = item.InterfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var alias = Quote(item.InterfaceAlias);
            commands.Add(
                "$a=Get-NetAdapter -InterfaceIndex " + index + " -ErrorAction SilentlyContinue; " +
                "if($null -ne $a) { " +
                "if($a.Name -ne " + alias + ") { throw 'Adapter identity changed for interface index " + index + ".' }; " +
                (item.ServerAddresses.Length == 0
                    ? "Set-DnsClientServerAddress -InterfaceIndex " + index + " -ResetServerAddresses -ErrorAction Stop; "
                    : "Set-DnsClientServerAddress -InterfaceIndex " + index + " -ServerAddresses @(" +
                      string.Join(",", item.ServerAddresses.Select(Quote)) + ") -ErrorAction Stop; ") +
                "}");
        }
        commands.Add("Write-Output 'OK'");
        return RunPowerShellAsync(string.Join(" ", commands), cancellationToken);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

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
            throw new InvalidOperationException("Could not start PowerShell for Windows DNS configuration.");

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(25), cancellationToken);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
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
            throw new InvalidOperationException("Windows DNS configuration failed: " +
                (string.IsNullOrWhiteSpace(detail) ? "PowerShell returned an unexpected result." : detail));
        }
    }

    public async ValueTask DisposeAsync() => await ReleaseAsync();
}
