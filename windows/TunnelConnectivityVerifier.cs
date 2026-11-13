using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace HBProxy.Windows;

/// <summary>
/// Performs a real TLS request after the full-tunnel routes are installed.
/// The system proxy is disabled so success depends on Windows routing through
/// the HBProxy interface rather than an unrelated configured HTTP proxy.
/// </summary>
public static class TunnelConnectivityVerifier
{
    public static async Task<IPAddress> VerifyHttpsEgressAsync(
        CancellationToken cancellationToken = default)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.Zero
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.ipify.org/");
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        if (!IPAddress.TryParse(body, out var publicAddress) ||
            IPAddress.IsLoopback(publicAddress) ||
            publicAddress.Equals(IPAddress.Any) ||
            publicAddress.Equals(IPAddress.IPv6Any) ||
            publicAddress.IsIPv6LinkLocal)
        {
            throw new InvalidOperationException(
                "The HTTPS egress check returned an invalid or non-public IP address.");
        }

        if (publicAddress.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            throw new InvalidOperationException("The HTTPS egress check returned an unsupported address family.");

        return publicAddress;
    }
}
