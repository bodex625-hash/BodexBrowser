using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace HBProxy.Windows;

/// <summary>
/// Read-only snapshot of DNS client server assignments per interface and address family.
/// Windows can return placeholder records for adapters that have no usable interface
/// identity; PowerShell filters those out and normalizes the .NET address-family enum.
/// </summary>
public sealed class WindowsDnsSnapshot
{
    public IReadOnlyList<WindowsDnsEntry> Entries { get; }

    private WindowsDnsSnapshot(IReadOnlyList<WindowsDnsEntry> entries)
    {
        Entries = entries;
    }

    public static async Task<WindowsDnsSnapshot> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        const string script = "$ErrorActionPreference='Stop'; " +
            "$items=@(Get-DnsClientServerAddress | ForEach-Object { " +
            "$familyName=$_.AddressFamily.ToString(); " +
            "$family=if ($familyName -in @('InterNetwork','IPv4','2')) {'IPv4'} " +
            "elseif ($familyName -in @('InterNetworkV6','IPv6','23')) {'IPv6'} else {$null}; " +
            "$index=0; [void][int]::TryParse([string]$_.InterfaceIndex,[ref]$index); " +
            "$alias=[string]$_.InterfaceAlias; " +
            "if ($index -gt 0 -and -not [string]::IsNullOrWhiteSpace($alias) -and $null -ne $family) { " +
            "[pscustomobject]@{InterfaceIndex=$index;InterfaceAlias=$alias;" +
            "AddressFamily=$family;ServerAddresses=@($_.ServerAddresses | Where-Object { " +
            "$null -ne $_ -and -not [string]::IsNullOrWhiteSpace([string]$_) } | ForEach-Object { [string]$_ })} " +
            "} }); ConvertTo-Json -InputObject $items -Compress";

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("RemoteSigned");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("Could not start PowerShell to inspect Windows DNS settings.");

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch
            {
                // Preserve the original timeout or cancellation exception.
            }

            throw;
        }

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                "Windows DNS inspection failed: " +
                (string.IsNullOrWhiteSpace(error) ? "PowerShell returned an error." : error.Trim()));

        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(output) ? "[]" : output);
        var entries = new List<WindowsDnsEntry>();
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Object)
            AddEntry(root, entries);
        else if (root.ValueKind == JsonValueKind.Array)
            foreach (var item in root.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object)
                    AddEntry(item, entries);

        if (entries.Count == 0)
            throw new InvalidOperationException("Windows returned no usable DNS interface records; refusing to prepare DNS changes.");

        return new WindowsDnsSnapshot(entries.AsReadOnly());
    }

    private static void AddEntry(JsonElement item, ICollection<WindowsDnsEntry> entries)
    {
        var interfaceIndex = GetInt32(item, "InterfaceIndex");
        var alias = GetString(item, "InterfaceAlias");
        var family = GetString(item, "AddressFamily");

        if (interfaceIndex <= 0 || string.IsNullOrWhiteSpace(alias) ||
            (family != "IPv4" && family != "IPv6"))
            throw new InvalidOperationException("Windows returned an incomplete usable DNS interface record.");

        if (!item.TryGetProperty("ServerAddresses", out var servers) ||
            servers.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
            throw new InvalidOperationException("Windows returned invalid DNS server data.");

        var addresses = new List<IPAddress>();
        if (servers.ValueKind == JsonValueKind.Array)
        {
            foreach (var server in servers.EnumerateArray())
            {
                var text = server.ToString();
                if (!IPAddress.TryParse(text, out var address))
                    throw new InvalidOperationException("Windows returned an invalid DNS server address.");
                if ((family == "IPv4") != (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
                    throw new InvalidOperationException("Windows returned a DNS server with the wrong address family.");
                addresses.Add(address);
            }
        }

        entries.Add(new WindowsDnsEntry(interfaceIndex, alias, family, addresses.AsReadOnly()));
    }

    private static string GetString(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
            throw new InvalidOperationException("Windows DNS data is missing " + name + ".");
        return value.ValueKind == JsonValueKind.Null ? string.Empty : value.ToString();
    }

    private static int GetInt32(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || !value.TryGetInt32(out var result))
            throw new InvalidOperationException("Windows DNS data has an invalid " + name + ".");
        return result;
    }
}

public sealed record WindowsDnsEntry(
    int InterfaceIndex,
    string InterfaceAlias,
    string AddressFamily,
    IReadOnlyList<IPAddress> ServerAddresses);
