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
        if (!IPAddress.TryParse(body, out var publicAddress) || !IsPublicAddress(publicAddress))
            throw new InvalidOperationException(
                "The HTTPS egress check returned an invalid, private, or reserved IP address.");

        return publicAddress;
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return !(bytes[0] == 0 ||
                     bytes[0] == 10 ||
                     bytes[0] == 127 ||
                     (bytes[0] == 169 && bytes[1] == 254) ||
                     (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                     (bytes[0] == 192 && bytes[1] == 168) ||
                     (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
                     (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) ||
                     (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2) ||
                     (bytes[0] == 198 && bytes[1] is 18 or 19) ||
                     (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) ||
                     (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) ||
                     bytes[0] >= 224);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return !(address.Equals(IPAddress.IPv6Any) ||
                     address.Equals(IPAddress.IPv6Loopback) ||
                     address.IsIPv6LinkLocal ||
                     address.IsIPv6SiteLocal ||
                     address.IsIPv6Multicast ||
                     (bytes[0] & 0xFE) == 0xFC ||
                     (bytes[0] == 0x20 && bytes[1] == 0x01 &&
                      bytes[2] == 0x0D && bytes[3] == 0xB8));
        }

        return false;
    }
}
