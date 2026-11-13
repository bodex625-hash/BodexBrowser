using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace HBProxy.Windows;

/// <summary>
/// Performs staged DNS, TCP, TLS and public-egress checks after full-tunnel
/// routes are installed. The system proxy is disabled deliberately.
/// </summary>
public static class TunnelConnectivityVerifier
{
    private const string Host = "api.ipify.org";

    public static async Task<IPAddress> VerifyHttpsEgressAsync(
        CancellationToken cancellationToken = default)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(Host, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Tunnel check failed at DNS resolution for {Host}: {Describe(ex)}", ex);
        }

        if (addresses.Length == 0)
            throw new InvalidOperationException(
                $"Tunnel check failed at DNS resolution: {Host} returned no IP addresses.");

        var attempted = new List<string>();
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectCallback = async (context, token) =>
            {
                Exception? lastError = null;
                foreach (var address in addresses)
                {
                    token.ThrowIfCancellationRequested();
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        attempted.Add(address.ToString());
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                    {
                        socket.Dispose();
                        lastError = ex;
                        if (ex is OperationCanceledException)
                            throw;
                    }
                }

                throw new IOException(
                    $"TCP connection to {Host}:443 failed for resolved addresses [{string.Join(", ", attempted)}]. {Describe(lastError)}",
                    lastError);
            }
        };

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{Host}/");
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Tunnel HTTPS check timed out while connecting to {Host}:443. " +
                "Check SOCKS5 server reachability, credentials, UDP support and tunnel routing.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Tunnel HTTPS/TLS check failed for {Host}:443: {Describe(ex)}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Tunnel HTTPS check reached {Host}, but the server returned HTTP {(int)response.StatusCode}.");

            var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            if (!IPAddress.TryParse(body, out var publicAddress) || !IsPublicAddress(publicAddress))
                throw new InvalidOperationException(
                    "The HTTPS egress check returned an invalid, private, or reserved IP address.");

            return publicAddress;
        }
    }

    private static string Describe(Exception? exception)
    {
        if (exception is null)
            return "No lower-level error details were provided.";

        var socket = exception as SocketException ?? exception.InnerException as SocketException;
        return socket is null
            ? $"{exception.GetType().Name}: {exception.Message}"
            : $"{exception.GetType().Name}: {exception.Message} (SocketError={socket.SocketErrorCode}, NativeError={socket.NativeErrorCode})";
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
