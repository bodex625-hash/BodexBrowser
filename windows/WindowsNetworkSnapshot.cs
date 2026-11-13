namespace HBProxy.Windows;

/// <summary>
/// Captures the route table and per-interface DNS assignments before any
/// tunnel routing changes. This is a read-only baseline for transactional
/// network setup and rollback.
/// </summary>
public sealed record WindowsNetworkSnapshot(
    WindowsRouteSnapshot Routes,
    WindowsDnsSnapshot Dns)
{
    public static async Task<WindowsNetworkSnapshot> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        var routes = await WindowsRouteSnapshot.CaptureAsync(cancellationToken);
        var dns = await WindowsDnsSnapshot.CaptureAsync(cancellationToken);
        return new WindowsNetworkSnapshot(routes, dns);
    }
}
