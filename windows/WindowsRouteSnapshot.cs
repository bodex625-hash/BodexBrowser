using System.Diagnostics;
using System.Text.Json;

namespace HBProxy.Windows;

/// <summary>
/// Read-only capture of the currently active IPv4 and IPv6 default routes.
/// This class does not add, change, or remove routes.
/// </summary>
public sealed class WindowsRouteSnapshot
{
    public IReadOnlyList<DefaultRouteEntry> DefaultRoutes { get; }

    private WindowsRouteSnapshot(IReadOnlyList<DefaultRouteEntry> routes)
    {
        DefaultRoutes = routes;
    }

    public static async Task<WindowsRouteSnapshot> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        const string script = "$ErrorActionPreference='Stop'; " +
            "$routes = @(Get-NetRoute -PolicyStore ActiveStore | " +
            "Where-Object { $_.DestinationPrefix -eq '0.0.0.0/0' -or $_.DestinationPrefix -eq '::/0' } | " +
            "Select-Object DestinationPrefix,InterfaceIndex,NextHop,RouteMetric,InterfaceAlias,Protocol,State); " +
            "ConvertTo-Json -InputObject $routes -Compress";

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
            throw new InvalidOperationException("Could not start PowerShell to inspect the active route table.");

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                "Windows route inspection failed: " + (string.IsNullOrWhiteSpace(error) ? "PowerShell returned an error." : error.Trim()));

        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(output) ? "[]" : output);
        var routes = new List<DefaultRouteEntry>();
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
            AddRoute(root, routes);
        else if (root.ValueKind == JsonValueKind.Array)
            foreach (var item in root.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object)
                    AddRoute(item, routes);

        if (routes.Count == 0)
            throw new InvalidOperationException("No active IPv4 or IPv6 default route was found; refusing to prepare tunnel routing.");

        return new WindowsRouteSnapshot(routes.AsReadOnly());
    }

    private static void AddRoute(JsonElement item, ICollection<DefaultRouteEntry> routes)
    {
        var prefix = GetString(item, "DestinationPrefix");
        var nextHop = GetString(item, "NextHop");
        var alias = GetString(item, "InterfaceAlias");
        var protocol = GetString(item, "Protocol");
        var state = GetString(item, "State");
        var interfaceIndex = GetInt32(item, "InterfaceIndex");
        var metric = GetInt32(item, "RouteMetric");

        if (prefix is not ("0.0.0.0/0" or "::/0") || interfaceIndex <= 0 || metric < 0)
            throw new InvalidOperationException("Windows returned an incomplete default-route record.");

        routes.Add(new DefaultRouteEntry(prefix, interfaceIndex, nextHop, metric, alias, protocol, state));
    }

    private static string GetString(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
            throw new InvalidOperationException("Windows route data is missing " + name + ".");
        return value.ValueKind == JsonValueKind.Null ? string.Empty : value.ToString();
    }

    private static int GetInt32(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || !value.TryGetInt32(out var result))
            throw new InvalidOperationException("Windows route data has an invalid " + name + ".");
        return result;
    }
}

public sealed record DefaultRouteEntry(
    string DestinationPrefix,
    int InterfaceIndex,
    string NextHop,
    int RouteMetric,
    string InterfaceAlias,
    string Protocol,
    string State);
