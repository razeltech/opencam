using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OpenCam.Core.Networking;

public sealed record NetworkAdapterInfo(
    string Id,
    string Name,
    string Description,
    string IPv4Address,
    NetworkInterfaceType InterfaceType,
    bool IsRecommended);

public static class LanNetworkService
{
    public static IReadOnlyList<NetworkAdapterInfo> GetActiveIPv4Adapters()
    {
        var results = new List<NetworkAdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            var props = nic.GetIPProperties();
            var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork);

            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = ua.Address.ToString();
                if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue;

                var desc = nic.Description;
                var isVirtual = desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                    || desc.Contains("WSL", StringComparison.OrdinalIgnoreCase)
                    || desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                    || desc.Contains("VMware", StringComparison.OrdinalIgnoreCase)
                    || desc.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
                    || desc.Contains("TAP-", StringComparison.OrdinalIgnoreCase)
                    || desc.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
                    || desc.Contains("Tailscale", StringComparison.OrdinalIgnoreCase)
                    || nic.Name.Contains("vgate", StringComparison.OrdinalIgnoreCase)
                    || nic.Name.Contains("WSL", StringComparison.OrdinalIgnoreCase);

                var isWifiOrEth = nic.NetworkInterfaceType is NetworkInterfaceType.Wireless80211
                    or NetworkInterfaceType.Ethernet
                    or NetworkInterfaceType.GigabitEthernet;

                var recommended = isWifiOrEth && hasGateway && !isVirtual;

                results.Add(new NetworkAdapterInfo(
                    nic.Id,
                    nic.Name,
                    nic.Description,
                    ip,
                    nic.NetworkInterfaceType,
                    recommended));
            }
        }

        return results
            .OrderByDescending(a => a.IsRecommended)
            .ThenByDescending(a => a.InterfaceType == NetworkInterfaceType.Wireless80211)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static NetworkAdapterInfo? SelectPreferredAdapter(string? preferredAdapterId = null)
    {
        var adapters = GetActiveIPv4Adapters();
        if (adapters.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(preferredAdapterId))
        {
            var match = adapters.FirstOrDefault(a =>
                string.Equals(a.Id, preferredAdapterId, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        return adapters[0];
    }

    public static int FindAvailableTcpPort(int preferredPort = 8443, int maxAttempts = 50)
    {
        if (preferredPort is < 1024 or > 65535) preferredPort = 8443;

        for (var offset = 0; offset < maxAttempts; offset++)
        {
            var candidate = preferredPort + offset;
            if (candidate > 65535) break;
            if (IsTcpPortAvailable(candidate)) return candidate;
        }

        using var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static bool IsTcpPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
