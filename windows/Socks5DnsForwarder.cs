using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HBProxy.Windows;

/// <summary>
/// Local UDP DNS endpoint. Every query is sent as DNS-over-HTTPS through an
/// authenticated SOCKS5 CONNECT tunnel. There is no direct-DNS fallback.
/// </summary>
public sealed class Socks5DnsForwarder : IAsyncDisposable
{
    private const int MaxDnsMessage = 4096;
    private const string DohHost = "cloudflare-dns.com";
    private readonly Socks5Profile _profile;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _concurrency = new(32, 32);
    private readonly HttpClient _httpClient;
    private UdpClient? _ipv4;
    private UdpClient? _ipv6;
    private Task? _ipv4Loop;
    private Task? _ipv6Loop;
    private bool _started;
    private Exception? _lastForwardingError;

    public Socks5DnsForwarder(Socks5Profile profile)
    {
        _profile = profile;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = ConnectThroughSocksAsync
        };
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
    }

    public Task StartAsync()
    {
        if (_started) throw new InvalidOperationException("DNS forwarder is already running.");
        try
        {
            _ipv4 = new UdpClient(AddressFamily.InterNetwork);
            _ipv4.Client.Bind(new IPEndPoint(IPAddress.Loopback, 53));
            _ipv6 = new UdpClient(AddressFamily.InterNetworkV6);
            _ipv6.Client.DualMode = false;
            _ipv6.Client.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 53));
            _ipv4Loop = ReceiveLoopAsync(_ipv4, _stop.Token);
            _ipv6Loop = ReceiveLoopAsync(_ipv6, _stop.Token);
            _started = true;
            return Task.CompletedTask;
        }
        catch
        {
            _ipv4?.Dispose(); _ipv4 = null;
            _ipv6?.Dispose(); _ipv6 = null;
            _httpClient.Dispose();
            throw;
        }
    }

    public async Task VerifyAsync(CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _lastForwardingError, null);
        var queryName = "hbproxy-" + Guid.NewGuid().ToString("N") + ".example.com";
        var query = BuildDnsQuery(queryName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var client = new UdpClient(AddressFamily.InterNetwork);
        await client.SendAsync(query, new IPEndPoint(IPAddress.Loopback, 53), timeout.Token);
        UdpReceiveResult result;
        try
        {
            result = await client.ReceiveAsync(timeout.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            var forwardingError = Volatile.Read(ref _lastForwardingError);
            if (forwardingError is not null)
                throw new InvalidOperationException(
                    "The local DNS forwarder did not receive a response because its SOCKS5/DoH request failed: " + forwardingError.Message,
                    forwardingError);
            throw new TimeoutException(
                "The local DNS forwarder received no response within 15 seconds. No upstream SOCKS5/DoH error was captured.", ex);
        }
        if (!IPAddress.IsLoopback(result.RemoteEndPoint.Address) ||
            result.Buffer.Length < 12 ||
            BinaryPrimitives.ReadUInt16BigEndian(result.Buffer.AsSpan(0, 2)) !=
                BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(0, 2)))
            throw new InvalidOperationException("The local DNS forwarder returned an invalid response.");

        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(result.Buffer.AsSpan(2, 2));
        int responseCode = flags & 0x000F;
        if ((flags & 0x8000) == 0 || (responseCode != 0 && responseCode != 3))
            throw new InvalidOperationException("The DNS-over-HTTPS tunnel did not return a valid DNS answer (RCODE=" + responseCode + ").");
    }

    private static byte[] BuildDnsQuery(string domain)
    {
        var labels = domain.Split('.');
        var encoded = labels.Select(Encoding.ASCII.GetBytes).ToArray();
        if (encoded.Any(label => label.Length is < 1 or > 63))
            throw new ArgumentException("Invalid DNS test name.", nameof(domain));

        int nameLength = encoded.Sum(label => label.Length + 1) + 1;
        var packet = new byte[12 + nameLength + 4];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2),
            (ushort)System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, 65536));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), 1);

        int offset = 12;
        foreach (var label in encoded)
        {
            packet[offset++] = (byte)label.Length;
            label.CopyTo(packet, offset);
            offset += label.Length;
        }
        packet[offset++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset + 2, 2), 1);
        return packet;
    }

    private async Task ReceiveLoopAsync(UdpClient listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try { packet = await listener.ReceiveAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (token.IsCancellationRequested) { break; }
            catch (SocketException) when (token.IsCancellationRequested) { break; }

            if (packet.Buffer.Length < 12 || packet.Buffer.Length > MaxDnsMessage)
                continue;

            _ = HandlePacketAsync(listener, packet.Buffer, packet.RemoteEndPoint, token);
        }
    }

    private async Task HandlePacketAsync(UdpClient listener, byte[] query, IPEndPoint client, CancellationToken token)
    {
        try
        {
            await _concurrency.WaitAsync(token);
            try
            {
                var response = await QueryViaDohAsync(query, token);
                if (response.Length >= 12 && response.Length <= MaxDnsMessage)
                    await listener.SendAsync(response, client, token);
            }
            finally { _concurrency.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Preserve the upstream failure for VerifyAsync diagnostics. Fail closed:
            // never retry a failed DoH lookup against a direct resolver.
            Volatile.Write(ref _lastForwardingError, ex);
        }
    }

    private async Task<byte[]> QueryViaDohAsync(byte[] query, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://" + DohHost + "/dns-query")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(query)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/dns-message");
        request.Headers.Accept.ParseAdd("application/dns-message");

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new IOException("DNS-over-HTTPS returned HTTP " + (int)response.StatusCode + ".");
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType,
            "application/dns-message", StringComparison.OrdinalIgnoreCase))
            throw new IOException("DNS-over-HTTPS returned an unexpected content type.");

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length is < 12 or > MaxDnsMessage)
            throw new IOException("DNS-over-HTTPS returned an invalid DNS message length.");
        return bytes;
    }

    private async ValueTask<Stream> ConnectThroughSocksAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var proxyAddress = IPAddress.Parse(_profile.ServerAddress);
        var socket = new Socket(proxyAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        NetworkStream? stream = null;
        try
        {
            await socket.ConnectAsync(new IPEndPoint(proxyAddress, _profile.Port), cancellationToken);
            stream = new NetworkStream(socket, ownsSocket: true);

            bool hasCredentials = !string.IsNullOrEmpty(_profile.Username);
            await stream.WriteAsync(hasCredentials
                ? new byte[] { 5, 2, 0, 2 }
                : new byte[] { 5, 1, 0 }, cancellationToken);

            var greeting = new byte[2];
            await ReadExactlyAsync(stream, greeting, cancellationToken);
            if (greeting[0] != 5 || greeting[1] == 0xFF)
                throw new IOException("SOCKS5 server rejected DNS-forwarding authentication methods.");

            if (greeting[1] == 2)
            {
                if (!hasCredentials) throw new IOException("SOCKS5 unexpectedly requires credentials.");
                var user = Encoding.UTF8.GetBytes(_profile.Username!);
                var pass = Encoding.UTF8.GetBytes(_profile.Password!);
                if (user.Length is < 1 or > 255 || pass.Length > 255)
                    throw new IOException("SOCKS5 credentials have an invalid encoded length.");

                var auth = new byte[3 + user.Length + pass.Length];
                auth[0] = 1;
                auth[1] = (byte)user.Length;
                user.CopyTo(auth, 2);
                auth[2 + user.Length] = (byte)pass.Length;
                pass.CopyTo(auth, 3 + user.Length);
                await stream.WriteAsync(auth, cancellationToken);
                await ReadExactlyAsync(stream, greeting, cancellationToken);
                if (greeting[0] != 1 || greeting[1] != 0)
                    throw new IOException("SOCKS5 username/password authentication failed for DNS forwarding.");
            }
            else if (greeting[1] != 0)
                throw new IOException("SOCKS5 selected an unsupported authentication method.");

            var host = Encoding.ASCII.GetBytes(context.DnsEndPoint.Host);
            if (host.Length is < 1 or > 255)
                throw new IOException("The DNS-over-HTTPS host name has an invalid length.");

            var connect = new byte[7 + host.Length];
            connect[0] = 5; connect[1] = 1; connect[2] = 0; connect[3] = 3;
            connect[4] = (byte)host.Length;
            host.CopyTo(connect, 5);
            connect[^2] = (byte)(context.DnsEndPoint.Port >> 8);
            connect[^1] = (byte)context.DnsEndPoint.Port;
            await stream.WriteAsync(connect, cancellationToken);

            var reply = new byte[4];
            await ReadExactlyAsync(stream, reply, cancellationToken);
            if (reply[0] != 5 || reply[1] != 0)
                throw new IOException("SOCKS5 could not establish the DNS-over-HTTPS tunnel.");

            int addressLength = reply[3] switch
            {
                1 => 4,
                4 => 16,
                3 => await ReadDomainLengthAsync(stream, cancellationToken),
                _ => throw new IOException("SOCKS5 returned an invalid bound-address type.")
            };
            var bound = new byte[addressLength + 2];
            await ReadExactlyAsync(stream, bound, cancellationToken);
            return stream;
        }
        catch
        {
            if (stream is not null) await stream.DisposeAsync();
            else socket.Dispose();
            throw;
        }
    }

    private static async Task<int> ReadDomainLengthAsync(Stream stream, CancellationToken token)
    {
        var length = new byte[1];
        await ReadExactlyAsync(stream, length, token);
        return length[0];
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], token);
            if (read == 0) throw new EndOfStreamException("SOCKS5 connection closed unexpectedly.");
            offset += read;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_started && _ipv4 is null && _ipv6 is null)
        {
            _httpClient.Dispose();
            return;
        }
        _stop.Cancel();
        _ipv4?.Dispose();
        _ipv6?.Dispose();
        var loops = new[] { _ipv4Loop, _ipv6Loop }.Where(t => t is not null).Cast<Task>();
        try { await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        _httpClient.Dispose();
        _concurrency.Dispose();
        _stop.Dispose();
        _started = false;
    }
}
