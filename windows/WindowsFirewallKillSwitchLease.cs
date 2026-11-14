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
    private readonly IPAddress _upstreamServer;
    private readonly string[] _aliases;
    private readonly HashSet<string> _udpRelayEndpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IPAddress> _udpRelayAddresses = new();
    private bool _released;

    private WindowsFirewallKillSwitchLease(string group, IPAddress upstreamServer, string[] aliases)
    {
        _group = group;
        _upstreamServer = upstreamServer;
        _aliases = aliases;
    }

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
        // Windows Defender Firewall accepts the special "Any" token more
        // reliably than an all-address CIDR (0.0.0.0/0 or ::/0) for port rules.
        // When the SOCKS endpoint is IPv4, all IPv6 DNS must be blocked (and
        // vice versa); when it shares the family, exempt only the SOCKS host.
        // Keep the local DNS forwarder reachable while blocking every external
        // DNS destination. The loopback exclusions are essential: without them,
        // the DNS-specific rules also block Windows queries to 127.0.0.1 / ::1.
        var dnsV4Exemptions = new List<string> { "127.0.0.0/8" };
        var dnsV6Exemptions = new List<string> { "::1/128" };
        if (upstreamServer.AddressFamily == AddressFamily.InterNetwork)
            dnsV4Exemptions.Add(upstreamServer + "/32");
        else
            dnsV6Exemptions.Add(upstreamServer + "/128");
        var dnsV4Blocked = ComplementCidr(32, dnsV4Exemptions);
        var dnsV6Blocked = ComplementCidr(128, dnsV6Exemptions);

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
            return new WindowsFirewallKillSwitchLease(group, upstreamServer, aliases);
        }
        catch (Exception setupError)
        {
            try
            {
                var cleanup = "$ErrorActionPreference='Stop'; " +
                    "$group=" + Quote(group) + "; " +
                    "$rules=@(Get-NetFirewallRule -PolicyStore PersistentStore -Group $group -ErrorAction SilentlyContinue); " +
                    "foreach($rule in $rules) { Remove-NetFirewallRule -PolicyStore PersistentStore -Name $rule.Name -ErrorAction SilentlyContinue }; " +
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
        script.Append("try { New-NetFirewallRule -PolicyStore PersistentStore ")
            .Append("-DisplayName ").Append(Quote("Bodex HBProxy " + name)).Append(' ')
            .Append("-Group $group -Direction Outbound -Action Block -Enabled True ")
            .Append("-Profile Any -InterfaceAlias $iface ")
            .Append("-RemoteAddress ").Append(PsArray(remoteAddresses)).Append(' ')
            .Append("-Protocol ").Append(Quote(protocol)).Append(' ');
        if (remotePort.HasValue)
            script.Append("-RemotePort ").Append(remotePort.Value.ToString(CultureInfo.InvariantCulture)).Append(' ');
        script.Append("-ErrorAction Stop | Out-Null } catch { ");
        script.Append("throw ('HBPROXY_RULE_FAILURE name=' + ").Append(Quote(name))
            .Append(" + ' interface=' + $iface + ' protocol=").Append(protocol)
            .Append(" remotePort=").Append(remotePort?.ToString(CultureInfo.InvariantCulture) ?? "none")
            .Append(" addressCount=").Append(remoteAddresses.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" error=' + $_.Exception.Message + ' category=' + $_.CategoryInfo.Category + ' id=' + $_.FullyQualifiedErrorId) ");
        script.Append("}; ");
    }

    // The native relay thread waits for this update before it starts forwarding.
    // The general kill switch remains protocol-agnostic. Only the negotiated UDP
    // endpoint is exempted; TCP and all other UDP ports on that relay host stay blocked.
    public async Task AddUdpRelayExemptionAsync(IPAddress relayAddress, int relayPort, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relayAddress);
        if (_released) throw new InvalidOperationException("The kill-switch lease has already been released.");
        if (relayPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(relayPort));
        if (relayAddress.Equals(_upstreamServer)) return;
        if (relayAddress.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            throw new InvalidOperationException("The SOCKS5 UDP relay address family is unsupported.");

        var endpointKey = relayAddress + ":" + relayPort.ToString(CultureInfo.InvariantCulture);
        if (_udpRelayEndpoints.Contains(endpointKey)) return;
        var relayAddresses = _udpRelayAddresses.Append(relayAddress).Distinct().ToArray();

        var v4Excluded = new List<string>
        {
            "0.0.0.0/8", "10.0.0.0/8", "127.0.0.0/8", "169.254.0.0/16",
            "172.16.0.0/12", "192.168.0.0/16", "224.0.0.0/4", "240.0.0.0/4"
        };
        var v6Excluded = new List<string> { "::/128", "::1/128", "fc00::/7", "fe80::/10", "ff00::/8" };
        var dnsV4Exemptions = new List<string> { "127.0.0.0/8" };
        var dnsV6Exemptions = new List<string> { "::1/128" };
        if (_upstreamServer.AddressFamily == AddressFamily.InterNetwork)
        {
            v4Excluded.Add(_upstreamServer + "/32");
            dnsV4Exemptions.Add(_upstreamServer + "/32");
        }
        else
        {
            v6Excluded.Add(_upstreamServer + "/128");
            dnsV6Exemptions.Add(_upstreamServer + "/128");
        }
        foreach (var address in relayAddresses)
        {
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                v4Excluded.Add(address + "/32");
                dnsV4Exemptions.Add(address + "/32");
            }
            else
            {
                v6Excluded.Add(address + "/128");
                dnsV6Exemptions.Add(address + "/128");
            }
        }

        var udpOtherPorts = new List<string>();
        if (relayPort > 1) udpOtherPorts.Add("1-" + (relayPort - 1).ToString(CultureInfo.InvariantCulture));
        if (relayPort < 65535) udpOtherPorts.Add((relayPort + 1).ToString(CultureInfo.InvariantCulture) + "-65535");
        var relayText = relayAddress.ToString();
        var tcpRuleName = "Bodex HBProxy Block TCP to UDP relay " + relayText;
        var udpRuleName = "Bodex HBProxy Block other UDP ports to relay " + relayText + ":" + relayPort.ToString(CultureInfo.InvariantCulture);
        var script = new System.Text.StringBuilder();
        script.Append("$ErrorActionPreference='Stop'; $group=").Append(Quote(_group)).Append("; ");
        script.Append("$ifaces=").Append(PsArray(_aliases)).Append("; ");
        script.Append("$relay=").Append(Quote(relayText)).Append("; ");
        script.Append("$tcpNameBase=").Append(Quote(tcpRuleName)).Append("; ");
        script.Append("$udpNameBase=").Append(Quote(udpRuleName)).Append("; ");
        script.Append("$udpPorts=").Append(PsArray(udpOtherPorts)).Append("; ");
        script.Append("foreach($iface in $ifaces) { $tcpName=$tcpNameBase + ' [' + $iface + ']'; $udpName=$udpNameBase + ' [' + $iface + ']'; ");
        script.Append("if(@(Get-NetFirewallRule -PolicyStore PersistentStore -Group $group -DisplayName $tcpName -ErrorAction SilentlyContinue).Count -eq 0) { ");
        script.Append("New-NetFirewallRule -PolicyStore PersistentStore -DisplayName $tcpName -Group $group -Direction Outbound -Action Block -Enabled True -Profile Any -InterfaceAlias $iface -RemoteAddress $relay -Protocol TCP -ErrorAction Stop | Out-Null }; ");
        script.Append("if(@(Get-NetFirewallRule -PolicyStore PersistentStore -Group $group -DisplayName $udpName -ErrorAction SilentlyContinue).Count -eq 0) { ");
        script.Append("New-NetFirewallRule -PolicyStore PersistentStore -DisplayName $udpName -Group $group -Direction Outbound -Action Block -Enabled True -Profile Any -InterfaceAlias $iface -RemoteAddress $relay -Protocol UDP -RemotePort $udpPorts -ErrorAction Stop | Out-Null }; ");
        script.Append("}; ");
        AppendAddressUpdate(script, "Block public IPv4 outside tunnel", ComplementCidr(32, v4Excluded), _aliases.Length);
        AppendAddressUpdate(script, "Block public IPv6 outside tunnel", ComplementCidr(128, v6Excluded), _aliases.Length);
        AppendAddressUpdate(script, "Block direct IPv4 DNS UDP", ComplementCidr(32, dnsV4Exemptions), _aliases.Length);
        AppendAddressUpdate(script, "Block direct IPv4 DNS TCP", ComplementCidr(32, dnsV4Exemptions), _aliases.Length);
        AppendAddressUpdate(script, "Block direct IPv6 DNS UDP", ComplementCidr(128, dnsV6Exemptions), _aliases.Length);
        AppendAddressUpdate(script, "Block direct IPv6 DNS TCP", ComplementCidr(128, dnsV6Exemptions), _aliases.Length);
        script.Append("$tcpRules=@(Get-NetFirewallRule -PolicyStore PersistentStore -Group $group | Where-Object { $_.DisplayName -like ($tcpNameBase + ' *') }); ");
        script.Append("$udpRules=@(Get-NetFirewallRule -PolicyStore PersistentStore -Group $group | Where-Object { $_.DisplayName -like ($udpNameBase + ' *') }); ");
        script.Append("if($tcpRules.Count -ne $ifaces.Count -or $udpRules.Count -ne $ifaces.Count) { throw 'UDP relay endpoint firewall rules could not be verified.' }; ");
        script.Append("Write-Output 'OK'");
        await RunPowerShellAsync(script.ToString(), cancellationToken);
        _udpRelayEndpoints.Add(endpointKey);
        if (!_udpRelayAddresses.Contains(relayAddress)) _udpRelayAddresses.Add(relayAddress);
    }

    private static void AppendAddressUpdate(System.Text.StringBuilder script, string displayName, IReadOnlyList<string> remoteAddresses, int expectedCount)
    {
        script.Append("$matches=@(Get-NetFirewallRule -PolicyStore PersistentStore -Group $group -ErrorAction Stop | Where-Object { $_.DisplayName -eq ")
            .Append(Quote("Bodex HBProxy " + displayName)).Append(" }); ");
        script.Append("if($matches.Count -ne ").Append(expectedCount.ToString(CultureInfo.InvariantCulture))
            .Append(") { throw ").Append(Quote("Could not locate every firewall rule: " + displayName)).Append(" }; ");
        script.Append("$matches | Get-NetFirewallAddressFilter | Set-NetFirewallAddressFilter -RemoteAddress ")
            .Append(PsArray(remoteAddresses)).Append(" -ErrorAction Stop; ");
    }
    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        if (_released)
            return;

        var script = "$ErrorActionPreference='Stop'; " +
            "$group=" + Quote(_group) + "; " +
            "$rules=@(Get-NetFirewallRule -PolicyStore PersistentStore -Group $group -ErrorAction SilentlyContinue); " +
            "foreach($rule in $rules) { Remove-NetFirewallRule -PolicyStore PersistentStore -Name $rule.Name -ErrorAction SilentlyContinue }; " +
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
