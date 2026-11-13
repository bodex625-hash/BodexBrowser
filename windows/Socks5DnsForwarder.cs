using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HBProxy.Windows;

/// <summary>
/// Local UDP DNS endpoint. Every upstream lookup is carried as DNS-over-TCP
/// inside an authenticated SOCKS5 CONNECT tunnel; no direct DNS fallback exists.
/// </summary>
public sealed class Socks5DnsForwarder : IAsyncDisposable
{
    private const int MaxDnsMessage = 4096;
    private readonly Socks5Profile _profile;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _concurrency = new(32, 32);
    private UdpClient? _ipv4;
    private UdpClient? _ipv6;
    private Task? _ipv4Loop;
    private Task? _ipv6Loop;
    private bool _started;

    public Socks5DnsForwarder(Socks5Profile profile) => _profile = profile;

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
            throw;
        }
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
                var response = await QueryViaSocksAsync(query, token);
                if (response.Length >= 12 && response.Length <= MaxDnsMessage)
                    await listener.SendAsync(response, client, token);
            }
            finally { _concurrency.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            // Fail closed: never retry this DNS query against a direct resolver.
        }
    }

    private async Task<byte[]> QueryViaSocksAsync(byte[] query, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Parse(_profile.ServerAddress), _profile.Port, timeout.Token);
        await using var stream = client.GetStream();

        bool hasCredentials = !string.IsNullOrEmpty(_profile.Username);
        await stream.WriteAsync(hasCredentials
            ? new byte[] { 5, 2, 0, 2 }
            : new byte[] { 5, 1, 0 }, timeout.Token);
        var greeting = new byte[2];
        await ReadExactlyAsync(stream, greeting, timeout.Token);
        if (greeting[0] != 5 || greeting[1] == 0xFF)
            throw new IOException("SOCKS5 server rejected DNS forwarding authentication methods.");

        if (greeting[1] == 2)
        {
            if (!hasCredentials) throw new IOException("SOCKS5 unexpectedly requires credentials.");
            var user = Encoding.UTF8.GetBytes(_profile.Username!);
            var pass = Encoding.UTF8.GetBytes(_profile.Password!);
            if (user.Length is < 1 or > 255 || pass.Length > 255)
                throw new IOException("SOCKS5 credentials have an invalid encoded length.");
            var auth = new byte[3 + user.Length + pass.Length];
            auth[0] = 1; auth[1] = (byte)user.Length;
            user.CopyTo(auth, 2);
            auth[2 + user.Length] = (byte)pass.Length;
            pass.CopyTo(auth, 3 + user.Length);
            await stream.WriteAsync(auth, timeout.Token);
            await ReadExactlyAsync(stream, greeting, timeout.Token);
            if (greeting[0] != 1 || greeting[1] != 0)
                throw new IOException("SOCKS5 username/password authentication failed for DNS forwarding.");
        }
        else if (greeting[1] != 0)
            throw new IOException("SOCKS5 selected an unsupported authentication method.");

        // DNS-over-TCP to Cloudflare's resolver, carried inside SOCKS5 CONNECT.
        byte[] connect = { 5, 1, 0, 1, 1, 1, 1, 1, 0, 53 };
        await stream.WriteAsync(connect, timeout.Token);
        var reply = new byte[4];
        await ReadExactlyAsync(stream, reply, timeout.Token);
        if (reply[0] != 5 || reply[1] != 0)
            throw new IOException("SOCKS5 could not establish the DNS-over-TCP connection.");
        int addressLength = reply[3] switch
        {
            1 => 4,
            4 => 16,
            3 => await ReadDomainLengthAsync(stream, timeout.Token),
            _ => throw new IOException("SOCKS5 returned an invalid address type.")
        };
        var bound = new byte[addressLength + 2];
        await ReadExactlyAsync(stream, bound, timeout.Token);

        var prefix = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(prefix, checked((ushort)query.Length));
        await stream.WriteAsync(prefix, timeout.Token);
        await stream.WriteAsync(query, timeout.Token);

        await ReadExactlyAsync(stream, prefix, timeout.Token);
        int responseLength = BinaryPrimitives.ReadUInt16BigEndian(prefix);
        if (responseLength is < 12 or > MaxDnsMessage)
            throw new IOException("The DNS-over-TCP response length is invalid.");
        var response = new byte[responseLength];
        await ReadExactlyAsync(stream, response, timeout.Token);
        return response;
    }

    private static async Task<int> ReadDomainLengthAsync(NetworkStream stream, CancellationToken token)
    {
        var length = new byte[1];
        await ReadExactlyAsync(stream, length, token);
        return length[0];
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], token);
            if (read == 0) throw new EndOfStreamException("SOCKS5/DNS connection closed unexpectedly.");
            offset += read;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_started && _ipv4 is null && _ipv6 is null) return;
        _stop.Cancel();
        _ipv4?.Dispose();
        _ipv6?.Dispose();
        var loops = new[] { _ipv4Loop, _ipv6Loop }.Where(t => t is not null).Cast<Task>();
        try { await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        _concurrency.Dispose();
        _stop.Dispose();
        _started = false;
    }
}
