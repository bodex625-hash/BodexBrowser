using System.Net;
using System.Net.Sockets;

namespace HBProxy.Windows;

/// <summary>
/// Read-only description of the original default routes and the route
/// identities that a future tunnel transaction may install on HBProxy.
/// It never mutates the Windows routing table.
/// </summary>
public sealed record DefaultRoutePlan(
    WindowsRouteEntry OriginalIpv4,
    WindowsRouteEntry OriginalIpv6,
    string TunnelIpv4Prefix,
    string TunnelIpv6Prefix)
{
    public static DefaultRoutePlan Create(WindowsRouteSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var ipv4 = SelectUniqueDefault(snapshot, AddressFamily.InterNetwork);
        var ipv6 = SelectUniqueDefault(snapshot, AddressFamily.InterNetworkV6);

        return new DefaultRoutePlan(
            ipv4,
            ipv6,
            "0.0.0.0/0",
            "::/0");
    }

    private static WindowsRouteEntry SelectUniqueDefault(
        WindowsRouteSnapshot snapshot,
        AddressFamily family)
    {
        var expectedPrefix = family == AddressFamily.InterNetwork ? "0.0.0.0/0" : "::/0";
        var candidates = snapshot.DefaultRoutes
            .Where(route => string.Equals(
                route.DestinationPrefix, expectedPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(route => route.TotalMetric)
            .ToArray();

        if (candidates.Length == 0)
            throw new InvalidOperationException(
                $"No active {family} default route exists; refusing to construct a full-tunnel plan.");

        if (candidates.Length > 1 &&
            candidates[0].TotalMetric == candidates[1].TotalMetric &&
            (candidates[0].InterfaceIndex != candidates[1].InterfaceIndex ||
             !string.Equals(candidates[0].NextHop, candidates[1].NextHop, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"The {family} default route is ambiguous; refusing to construct a full-tunnel plan.");
        }

        if (!IPAddress.TryParse(candidates[0].NextHop, out var nextHop) ||
            nextHop.AddressFamily != family)
        {
            throw new InvalidOperationException(
                $"The selected {family} default route has an invalid next hop.");
        }

        return candidates[0];
    }
}
