using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetworkAccelerator.Core.Services;

public static class LanAddressLookup
{
    public static string GetPrimaryIPv4()
    {
        try
        {
            string? fallback = null;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (nic.Name.Contains("apphub-tun", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(address.Address)) continue;
                    var text = address.Address.ToString();
                    if (IsPrivate(address.Address)) return text;
                    fallback ??= text;
                }
            }
            return fallback ?? "—";
        }
        catch (NetworkInformationException)
        {
            return "—";
        }
    }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return false;
        if (bytes[0] == 10) return true;
        if (bytes[0] == 192 && bytes[1] == 168) return true;
        return bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31;
    }
}
