using System.Net;
using System.Net.Sockets;

namespace HBProxy.Windows;

/// <summary>
/// Read-only description of the original default routes and the route
/// identities that a future tunnel transaction may install on HBProxy.
/// A missing IPv6 default route is represented explicitly so the later
/// firewall transaction can block IPv6 egress rather than silently leak it.
/// This type never mutates the Windows routing table.
/// </summary>
public sealed record DefaultRoutePlan(
    WindowsRouteEntry OriginalIpv4,
    WindowsRouteEntry? OriginalIpv6,
    string TunnelIpv4Prefix,
    string TunnelIpv6Prefix)
{
    public bool MustBlockIpv6OutsideTunnel => OriginalIpv6 is null;

    public static DefaultRoutePlan Create(WindowsRouteSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var ipv4 = SelectUniqueDefault(snapshot, AddressFamily.InterNetwork, required: true)!;
        var ipv6 = SelectUniqueDefault(snapshot, AddressFamily.InterNetworkV6, required: false);

        return new DefaultRoutePlan(
            ipv4,
            ipv6,
            "0.0.0.0/0",
            "::/0");
    }

    private static WindowsRouteEntry? SelectUniqueDefault(
        WindowsRouteSnapshot snapshot,
        AddressFamily family,
        bool required)
    {
        var expectedPrefix = family == AddressFamily.InterNetwork ? "0.0.0.0/0" : "::/0";
        var candidates = snapshot.DefaultRoutes
            .Where(route => string.Equals(
                route.DestinationPrefix, expectedPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(route => route.TotalMetric)
            .ToArray();

        if (candidates.Length == 0)
        {
            if (required)
                throw new InvalidOperationException(
                    $"No active {family} default route exists; refusing to construct a full-tunnel plan.");
            return null;
        }

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
