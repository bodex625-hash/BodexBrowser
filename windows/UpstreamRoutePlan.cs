using System.Net;
using System.Net.Sockets;

namespace HBProxy.Windows;

/// <summary>
/// A read-only plan for keeping the SOCKS5 control connection on its
/// pre-tunnel physical route. This type never changes Windows networking.
/// </summary>
public sealed record UpstreamRoutePlan(
    IPAddress ServerAddress,
    string DestinationPrefix,
    int InterfaceIndex,
    string InterfaceAlias,
    IPAddress NextHop,
    int RouteMetric,
    int InterfaceMetric,
    bool RequiresHostRoute)
{
    public static UpstreamRoutePlan Create(
        WindowsRouteSnapshot snapshot,
        IPAddress serverAddress)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(serverAddress);

        var selected = snapshot.FindRouteTo(serverAddress);
        if (!IPAddress.TryParse(selected.NextHop, out var nextHop))
            throw new InvalidOperationException("The selected route has an invalid next-hop address.");

        if (nextHop.AddressFamily != serverAddress.AddressFamily)
            throw new InvalidOperationException("The selected route's next hop uses a different IP address family.");

        var maxPrefix = serverAddress.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var hostPrefix = serverAddress + "/" + maxPrefix;

        return new UpstreamRoutePlan(
            serverAddress,
            hostPrefix,
            selected.InterfaceIndex,
            selected.InterfaceAlias,
            nextHop,
            selected.RouteMetric,
            selected.InterfaceMetric,
            selected.PrefixLength < maxPrefix);
    }
}
