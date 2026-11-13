using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Net.Sockets;
using System.Globalization;

namespace HBProxy.Windows;

/// <summary>
/// Adds a fail-closed outbound policy to every currently active non-loopback
/// interface except the HBProxy tunnel. Public destinations are blocked except
/// the exact SOCKS server address; direct DNS (TCP/UDP 53) is blocked too.
/// Rules are owned by a unique group and only that group is removed.
/// </summary>
public sealed class WindowsFirewallKillSwitchLease : IAsyncDisposable
{
    private readonly string _group;
    private bool _released;

    private WindowsFirewallKillSwitchLease(string group) => _group = group;

    public static async Task<WindowsFirewallKillSwitchLease> AcquireAsync(
        IPAddress upstreamServer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upstreamServer);

        var aliases = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        !string.Equals(n.Name, "HBProxy", StringComparison.OrdinalIgnoreCase))
            .Select(n => n.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (aliases.Length == 0)
            throw new InvalidOperationException("No active network interface was found for the kill switch.");

        var v4Excluded = new List<string>
        {
            "0.0.0.0/8", "10.0.0.0/8", "127.0.0.0/8", "169.254.0.0/16",
            "172.16.0.0/12", "192.168.0.0/16", "224.0.0.0/4", "240.0.0.0/4"
        };
        var v6Excluded = new List<string>
        {
            "::/128", "::1/128", "fc00::/7", "fe80::/10", "ff00::/8"
        };
        if (upstreamServer.AddressFamily == AddressFamily.InterNetwork)
            v4Excluded.Add(upstreamServer + "/32");
        else if (upstreamServer.AddressFamily == AddressFamily.InterNetworkV6)
            v6Excluded.Add(upstreamServer + "/128");
        else
            throw new InvalidOperationException("The SOCKS server address family is unsupported.");

        var v4Blocked = ComplementCidr(32, v4Excluded);
        var v6Blocked = ComplementCidr(128, v6Excluded);
        var dnsV4Blocked = ComplementCidr(32, upstreamServer.AddressFamily == AddressFamily.InterNetwork
            ? new[] { upstreamServer + "/32" } : Array.Empty<string>());
        var dnsV6Blocked = ComplementCidr(128, upstreamServer.AddressFamily == AddressFamily.InterNetworkV6
            ? new[] { upstreamServer + "/128" } : Array.Empty<string>());

        var group = "Bodex.HBProxy.KillSwitch." + Guid.NewGuid().ToString("N");
        var script = new System.Text.StringBuilder();
        script.Append("$ErrorActionPreference='Stop'; ");
        script.Append("$group=").Append(Quote(group)).Append("; ");
        script.Append("$ifaces=").Append(PsArray(aliases)).Append("; ");
        script.Append("$expected=0; ");
        script.Append("try { foreach($iface in $ifaces) { ");
        script.Append("$expected += 6; ");
        AppendRule(script, "Block public IPv4 outside tunnel", v4Blocked, "Any", null);
        AppendRule(script, "Block public IPv6 outside tunnel", v6Blocked, "Any", null);
        AppendRule(script, "Block direct IPv4 DNS UDP", dnsV4Blocked, "UDP", 53);
        AppendRule(script, "Block direct IPv4 DNS TCP", dnsV4Blocked, "TCP", 53);
        AppendRule(script, "Block direct IPv6 DNS UDP", dnsV6Blocked, "UDP", 53);
        AppendRule(script, "Block direct IPv6 DNS TCP", dnsV6Blocked, "TCP", 53);
        script.Append("} ");
        script.Append("$rules=@(Get-NetFirewallRule -PolicyStore PersistentStore -Group $group -ErrorAction Stop); ");
        script.Append("if($rules.Count -ne $expected) { throw 'The kill-switch rules could not be verified.' }; ");
        script.Append("Write-Output 'OK' ");
        script.Append("} catch { ");
        script.Append("Get-NetFirewallRule -PolicyStore PersistentStore -Group $group -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue; ");
        script.Append("throw }");

        try
        {
            await RunPowerShellAsync(script.ToString(), cancellationToken);
            return new WindowsFirewallKillSwitchLease(group);
        }
        catch (Exception setupError)
        {
            try
            {
                var cleanup = "$ErrorActionPreference='Stop'; " +
                    "$group=" + Quote(group) + "; " +
                    "$rules=@(Get-NetFirewallRule -PolicyStore PersistentStore -Group $group -ErrorAction SilentlyContinue); " +
                    "if($rules.Count -gt 0) { $rules | Remove-NetFirewallRule -ErrorAction Stop }; " +
                    "$left=@(Get-NetFirewallRule -PolicyStore PersistentStore -Group $group -ErrorAction SilentlyContinue); " +
                    "if($left.Count -ne 0) { throw 'Partial kill-switch rules remain after rollback.' }; " +
                    "Write-Output 'OK'";
                await RunPowerShellAsync(cleanup, CancellationToken.None);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(
                    "Kill-switch setup failed and its partial rules could not be fully rolled back.",
                    setupError, cleanupError);
            }

            throw;
        }
    }

    private static void AppendRule(
        System.Text.StringBuilder script,
        string name,
        IReadOnlyList<string> remoteAddresses,
        string protocol,
        int? remotePort)
    {
        script.Append("New-NetFirewallRule -PolicyStore PersistentStore ")
            .Append("-DisplayName ").Append(Quote("Bodex HBProxy " + name)).Append(' ')
            .Append("-Group $group -Direction Outbound -Action Block -Enabled True ")
            .Append("-Profile Any -InterfaceAlias $iface ")
            .Append("-RemoteAddress ").Append(PsArray(remoteAddresses)).Append(' ')
            .Append("-Protocol ").Append(Quote(protocol)).Append(' ');
        if (remotePort.HasValue)
            script.Append("-RemotePort ").Append(remotePort.Value.ToString(CultureInfo.InvariantCulture)).Append(' ');
        script.Append("-ErrorAction Stop | Out-Null; ");
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        if (_released)
            return;

        var script = "$ErrorActionPreference='Stop'; " +
            "$group=" + Quote(_group) + "; " +
            "$rules=@(Get-NetFirewallRule -PolicyStore PersistentStore -Group $group -ErrorAction SilentlyContinue); " +
            "if($rules.Count -gt 0) { $rules | Remove-NetFirewallRule -ErrorAction Stop }; " +
            "$left=@(Get-NetFirewallRule -PolicyStore PersistentStore -Group $group -ErrorAction SilentlyContinue); " +
            "if($left.Count -ne 0) { throw 'The owned kill-switch rules remain after cleanup.' }; " +
            "Write-Output 'OK'";

        await RunPowerShellAsync(script, cancellationToken);
        _released = true;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string PsArray(IEnumerable<string> values) =>
        "@(" + string.Join(",", values.Select(Quote)) + ")";

    private static IReadOnlyList<string> ComplementCidr(int bits, IEnumerable<string> excludedCidrs)
    {
        var max = (BigInteger.One << bits) - BigInteger.One;
        var intervals = new List<(BigInteger Start, BigInteger End)>();

        foreach (var cidr in excludedCidrs)
        {
            var parts = cidr.Split('/', 2);
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) ||
                !int.TryParse(parts[1], out var prefix) ||
                address.GetAddressBytes().Length * 8 != bits || prefix < 0 || prefix > bits)
                throw new InvalidOperationException("An invalid address range was supplied to the kill switch.");

            var value = new BigInteger(address.GetAddressBytes(), isUnsigned: true, isBigEndian: true);
            var hostBits = bits - prefix;
            var start = (value >> hostBits) << hostBits;
            var end = start + (BigInteger.One << hostBits) - BigInteger.One;
            intervals.Add((start, end));
        }

        intervals.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(BigInteger Start, BigInteger End)>();
        foreach (var interval in intervals)
        {
            if (merged.Count == 0 || interval.Start > merged[^1].End + BigInteger.One)
                merged.Add(interval);
            else if (interval.End > merged[^1].End)
                merged[^1] = (merged[^1].Start, interval.End);
        }

        var result = new List<string>();
        var cursor = BigInteger.Zero;
        foreach (var interval in merged)
        {
            if (cursor < interval.Start)
                AddRangeAsCidrs(cursor, interval.Start - BigInteger.One, bits, result);
            cursor = BigInteger.Max(cursor, interval.End + BigInteger.One);
        }
        if (cursor <= max)
            AddRangeAsCidrs(cursor, max, bits, result);

        return result.AsReadOnly();
    }

    private static void AddRangeAsCidrs(
        BigInteger start,
        BigInteger end,
        int bits,
        ICollection<string> output)
    {
        while (start <= end)
        {
            var alignmentBits = start.IsZero ? bits : TrailingZeroBits(start, bits);
            var remaining = end - start + BigInteger.One;
            var capacityBits = FloorLog2(remaining);
            var hostBits = Math.Min(alignmentBits, capacityBits);
            var prefix = bits - hostBits;
            output.Add(ToAddress(start, bits) + "/" + prefix.ToString(CultureInfo.InvariantCulture));
            start += BigInteger.One << hostBits;
        }
    }

    private static int TrailingZeroBits(BigInteger value, int maxBits)
    {
        var count = 0;
        while (count < maxBits && (value & (BigInteger.One << count)).IsZero)
            count++;
        return count;
    }

    private static int FloorLog2(BigInteger value)
    {
        var count = -1;
        while (value > BigInteger.Zero)
        {
            value >>= 1;
            count++;
        }
        return count;
    }

    private static string ToAddress(BigInteger value, int bits)
    {
        var length = bits / 8;
        var raw = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var bytes = new byte[length];
        Buffer.BlockCopy(raw, Math.Max(0, raw.Length - length), bytes,
            Math.Max(0, length - raw.Length), Math.Min(length, raw.Length));
        return new IPAddress(bytes).ToString();
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
            throw new InvalidOperationException("Could not start PowerShell for firewall isolation.");

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
                "Windows kill-switch setup failed: " +
                (string.IsNullOrWhiteSpace(detail) ? "PowerShell returned an unexpected result." : detail));
        }
    }

    public async ValueTask DisposeAsync() => await ReleaseAsync();
}
