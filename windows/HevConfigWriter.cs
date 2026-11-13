using System.Net;
using System.Text;

namespace HBProxy.Windows;

/// <summary>
/// Builds the HEV SOCKS5 tunnel configuration in memory.
/// The returned text contains credentials when authentication is enabled;
/// callers must only write it to a directory protected for the current user
/// and must remove the file during cleanup.
/// </summary>
public static class HevConfigWriter
{
    // Keep this GUID stable so Windows can identify the adapter across sessions.
    public static readonly Guid AdapterGuid = new("9b3e1b80-5d6c-4f9a-8b24-2e6f7c1a903d");

    public static string Build(Socks5Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();

        var address = IPAddress.Parse(profile.ServerAddress).ToString();
        var udpMode = profile.EnableUdp ? "udp" : "tcp";
        var builder = new StringBuilder();

        builder.AppendLine("tunnel:");
        builder.AppendLine("  name: HBProxy");
        builder.AppendLine($"  guid: '{AdapterGuid:D}'");
        builder.AppendLine("  mtu: 1400");
        builder.AppendLine("  multi-queue: false");
        builder.AppendLine("  ipv4: 198.18.0.1");
        builder.AppendLine("  ipv6: 'fc00::1'");
        builder.AppendLine("  icmp: 'off'");
        builder.AppendLine();
        builder.AppendLine("socks5:");
        builder.AppendLine($"  address: '{YamlSingleQuoted(address)}'");
        builder.AppendLine($"  port: {profile.Port}");
        builder.AppendLine($"  udp: '{udpMode}'");

        if (profile.Username is not null && profile.Password is not null)
        {
            builder.AppendLine($"  username: '{YamlSingleQuoted(profile.Username)}'");
            builder.AppendLine($"  password: '{YamlSingleQuoted(profile.Password)}'");
        }

        builder.AppendLine();
        builder.AppendLine("misc:");
        builder.AppendLine("  log-file: null");
        builder.AppendLine("  log-level: warn");
        return builder.ToString();
    }

    private static string YamlSingleQuoted(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
