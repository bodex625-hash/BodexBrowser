using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace HBProxy.Windows;

/// <summary>
/// Performs DNS-over-HTTPS, TCP, TLS and public-egress checks after full-tunnel
/// routes are installed. Both DNS and the final HTTPS request use the tunnel;
/// neither relies on the Windows system resolver or system proxy.
/// </summary>
public static class TunnelConnectivityVerifier
{
    private const string Host = "api.ipify.org";
    private static readonly IPAddress DohAddress = IPAddress.Parse("1.1.1.1");
    private const string DohHost = "cloudflare-dns.com";

    public static async Task<IPAddress> VerifyHttpsEgressAsync(
        CancellationToken cancellationToken = default)
    {
        var addresses = await ResolveThroughTunnelAsync(cancellationToken);
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
                    $"TCP connection to {Host}:443 failed for DoH-resolved addresses [{string.Join(", ", attempted)}]. {Describe(lastError)}",
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


    /// <summary>
    /// Verifies the same DNS and HTTPS path used by ordinary Windows applications.
    /// Unlike the DoH check above, this deliberately uses the Windows resolver.
    /// </summary>
    public static async Task<IPAddress> VerifyWindowsConnectivityAsync(
        CancellationToken cancellationToken = default)
    {
        IPAddress[] resolved;
        try
        {
            resolved = await Dns.GetHostAddressesAsync(Host, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Windows DNS resolution failed for {Host}. The tunnel's DNS path is not working for normal applications: {Describe(ex)}", ex);
        }

        if (resolved.Length == 0)
            throw new InvalidOperationException(
                $"Windows DNS returned no addresses for {Host}; normal application traffic cannot resolve websites.");

        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.Zero
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            using var response = await client.GetAsync(
                $"https://{Host}/", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Windows-resolver HTTPS test reached {Host}, but returned HTTP {(int)response.StatusCode}.");

            var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            if (!IPAddress.TryParse(body, out var address) || !IsPublicAddress(address))
                throw new InvalidOperationException(
                    "Windows-resolver HTTPS test returned an invalid or non-public egress address.");

            return address;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Normal Windows DNS/HTTPS connectivity test failed for {Host}: {Describe(ex)}", ex);
        }
    }

    private static async Task<IPAddress[]> ResolveThroughTunnelAsync(CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(DohAddress, 443), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://{DohHost}/dns-query?name={Uri.EscapeDataString(Host)}&type=A");
        request.Headers.Accept.ParseAdd("application/dns-json");

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Tunnel DNS-over-HTTPS request to Cloudflare (1.1.1.1) failed: {Describe(ex)}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Tunnel DNS-over-HTTPS returned HTTP {(int)response.StatusCode}.");

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            if (!root.TryGetProperty("Status", out var status) || status.GetInt32() != 0)
                throw new InvalidOperationException(
                    $"Tunnel DNS-over-HTTPS could not resolve {Host}; DNS status was " +
                    (root.TryGetProperty("Status", out status) ? status.ToString() : "missing") + ".");

            if (!root.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"Tunnel DNS-over-HTTPS returned no A records for {Host}.");

            var addresses = answers.EnumerateArray()
                .Where(answer => answer.TryGetProperty("type", out var type) && type.GetInt32() == 1)
                .Select(answer => answer.TryGetProperty("data", out var data) ? data.GetString() : null)
                .Where(value => IPAddress.TryParse(value, out _))
                .Select(value => IPAddress.Parse(value!))
                .ToArray();

            if (addresses.Length == 0)
                throw new InvalidOperationException($"Tunnel DNS-over-HTTPS returned no valid IPv4 addresses for {Host}.");

            return addresses;
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
