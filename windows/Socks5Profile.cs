using System.Net;
using System.Security;

namespace HBProxy.Windows;

public sealed record Socks5Profile(
    string ServerAddress,
    int Port,
    string? Username,
    string? Password,
    bool EnableUdp = true)
{
    public void Validate()
    {
        if (!IPAddress.TryParse(ServerAddress, out _))
            throw new ArgumentException("For the first Windows release, enter the SOCKS5 server as an IP address. This is required to create a reliable upstream bypass route.", nameof(ServerAddress));

        if (Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(Port), "SOCKS5 port must be between 1 and 65535.");

        if ((Username is null) != (Password is null))
            throw new ArgumentException("Enter both SOCKS5 username and password, or leave both empty.");

        if (Username is { Length: > 255 } || Password is { Length: > 255 })
            throw new ArgumentException("SOCKS5 credentials cannot exceed 255 characters.");

        if (Username?.Any(char.IsControl) == true || Password?.Any(char.IsControl) == true)
            throw new ArgumentException("SOCKS5 credentials cannot contain control characters.");
    }
}
