using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace HBProxy.Windows;

/// <summary>
/// Read-only capture of the active route table and interface metrics.
/// No route, DNS, adapter, or firewall settings are modified.
/// </summary>
public sealed class WindowsRouteSnapshot
{
    public IReadOnlyList<WindowsRouteEntry> Routes { get; }

    public IReadOnlyList<WindowsRouteEntry> DefaultRoutes =>
        Routes.Where(route => route.PrefixLength == 0).ToArray();

    private WindowsRouteSnapshot(IReadOnlyList<WindowsRouteEntry> routes)
    {
        Routes = routes;
    }

    public static async Task<WindowsRouteSnapshot> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        const string script = "$ErrorActionPreference='Stop'; " +
            "$metrics=@{}; " +
            "Get-NetIPInterface -PolicyStore ActiveStore | ForEach-Object { " +
            "$metrics[($_.InterfaceIndex.ToString() + '|' + $_.AddressFamily.ToString())]=[int]$_.InterfaceMetric }; " +
            "$routes=@(Get-NetRoute -PolicyStore ActiveStore | ForEach-Object { " +
            "$family=if ($_.DestinationPrefix.Contains(':')) {'IPv6'} else {'IPv4'}; " +
            "$key=($_.InterfaceIndex.ToString() + '|' + $family); " +
            "$im=if ($metrics.ContainsKey($key)) {$metrics[$key]} else {-1}; " +
            "[pscustomobject]@{DestinationPrefix=$_.DestinationPrefix;InterfaceIndex=$_.InterfaceIndex;" +
            "NextHop=$_.NextHop;RouteMetric=$_.RouteMetric;InterfaceMetric=$im;" +
            "InterfaceAlias=$_.InterfaceAlias;Protocol=$_.Protocol;State=$_.State} }); " +
            "ConvertTo-Json -InputObject $routes -Compress";

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
            throw new InvalidOperationException("Could not start PowerShell to inspect the active route table.");

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
                    process.Kill(entireProcessTree: true);
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
            throw new InvalidOperationException(
                "Windows route inspection failed: " +
                (string.IsNullOrWhiteSpace(error) ? "PowerShell returned an error." : error.Trim()));

        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(output) ? "[]" : output);
        var routes = new List<WindowsRouteEntry>();
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Object)
            AddRoute(root, routes);
        else if (root.ValueKind == JsonValueKind.Array)
            foreach (var item in root.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object)
                    AddRoute(item, routes);

        if (routes.Count == 0)
            throw new InvalidOperationException("The active Windows route table is empty; refusing to prepare tunnel routing.");

        return new WindowsRouteSnapshot(routes.AsReadOnly());
    }

    /// <summary>
    /// Selects the most-specific active route to the SOCKS endpoint.
    /// Equal-prefix candidates are resolved by combined route/interface metric.
    /// </summary>
    public WindowsRouteEntry FindRouteTo(IPAddress destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var candidates = Routes
            .Where(route => route.Contains(destination))
            .OrderByDescending(route => route.PrefixLength)
            .ThenBy(route => route.TotalMetric)
            .ToArray();

        if (candidates.Length == 0)
            throw new InvalidOperationException("No active route matches the SOCKS5 server address.");

        if (candidates.Length > 1 &&
            candidates[0].PrefixLength == candidates[1].PrefixLength &&
            candidates[0].TotalMetric == candidates[1].TotalMetric &&
            (candidates[0].InterfaceIndex != candidates[1].InterfaceIndex ||
             !string.Equals(candidates[0].NextHop, candidates[1].NextHop, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The route to the SOCKS5 server is ambiguous; refusing to change routing.");
        }

        return candidates[0];
    }

    private static void AddRoute(JsonElement item, ICollection<WindowsRouteEntry> routes)
    {
        var prefix = GetString(item, "DestinationPrefix");
        var nextHop = GetString(item, "NextHop");
        var alias = GetString(item, "InterfaceAlias");
        var protocol = GetString(item, "Protocol");
        var state = GetString(item, "State");
        var interfaceIndex = GetInt32(item, "InterfaceIndex");
        var routeMetric = GetInt32(item, "RouteMetric");
        var interfaceMetric = GetInt32(item, "InterfaceMetric");

        if (!WindowsRouteEntry.TryParsePrefix(prefix, out var network, out var prefixLength) ||
            interfaceIndex <= 0 || routeMetric < 0 || interfaceMetric < 0 ||
            string.IsNullOrWhiteSpace(nextHop) || string.IsNullOrWhiteSpace(alias))
        {
            throw new InvalidOperationException("Windows returned an incomplete active-route record.");
        }

        routes.Add(new WindowsRouteEntry(
            prefix, network, prefixLength, interfaceIndex, nextHop,
            routeMetric, interfaceMetric, alias, protocol, state));
    }

    private static string GetString(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
            throw new InvalidOperationException("Windows route data is missing " + name + ".");
        return value.ValueKind == JsonValueKind.Null ? string.Empty : value.ToString();
    }

    private static int GetInt32(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || !value.TryGetInt32(out var result))
            throw new InvalidOperationException("Windows route data has an invalid " + name + ".");
        return result;
    }
}

public sealed class WindowsRouteEntry
{
    private readonly byte[] _networkBytes;

    public string DestinationPrefix { get; }
    public int PrefixLength { get; }
    public int InterfaceIndex { get; }
    public string NextHop { get; }
    public int RouteMetric { get; }
    public int InterfaceMetric { get; }
    public int TotalMetric => checked(RouteMetric + InterfaceMetric);
    public string InterfaceAlias { get; }
    public string Protocol { get; }
    public string State { get; }

    internal WindowsRouteEntry(
        string destinationPrefix,
        byte[] networkBytes,
        int prefixLength,
        int interfaceIndex,
        string nextHop,
        int routeMetric,
        int interfaceMetric,
        string interfaceAlias,
        string protocol,
        string state)
    {
        DestinationPrefix = destinationPrefix;
        _networkBytes = networkBytes;
        PrefixLength = prefixLength;
        InterfaceIndex = interfaceIndex;
        NextHop = nextHop;
        RouteMetric = routeMetric;
        InterfaceMetric = interfaceMetric;
        InterfaceAlias = interfaceAlias;
        Protocol = protocol;
        State = state;
    }

    internal bool Contains(IPAddress address)
    {
        var addressBytes = address.GetAddressBytes();
        if (addressBytes.Length != _networkBytes.Length)
            return false;

        var wholeBytes = PrefixLength / 8;
        var remainingBits = PrefixLength % 8;

        for (var i = 0; i < wholeBytes; i++)
            if (addressBytes[i] != _networkBytes[i])
                return false;

        if (remainingBits == 0)
            return true;

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (addressBytes[wholeBytes] & mask) == (_networkBytes[wholeBytes] & mask);
    }

    internal static bool TryParsePrefix(string value, out byte[] network, out int prefixLength)
    {
        network = Array.Empty<byte>();
        prefixLength = -1;

        var parts = value.Split('/', 2);
        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var address) ||
            !int.TryParse(parts[1], out prefixLength))
            return false;

        network = address.GetAddressBytes();
        var maxBits = network.Length * 8;
        if (prefixLength < 0 || prefixLength > maxBits)
            return false;

        var wholeBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        if (remainingBits > 0)
        {
            network[wholeBytes] &= (byte)(0xFF << (8 - remainingBits));
            wholeBytes++;
        }

        for (var i = wholeBytes; i < network.Length; i++)
            network[i] = 0;

        return true;
    }
}
