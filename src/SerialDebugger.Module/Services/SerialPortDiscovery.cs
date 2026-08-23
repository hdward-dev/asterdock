using System.IO.Ports;
using System.Management;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace SerialDebugger.Module.Services;

internal static partial class SerialPortDiscovery
{
    public static IReadOnlyList<SerialPortDevice> GetConnectedPorts()
    {
        string[] registeredPorts;
        try
        {
            registeredPorts = SerialPort.GetPortNames();
        }
        catch
        {
            return [];
        }

        if (!OperatingSystem.IsWindows())
            return registeredPorts
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => new SerialPortDevice(name, name))
                .ToList();

        return GetPresentWindowsHardwarePorts(registeredPorts);
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<SerialPortDevice> GetPresentWindowsHardwarePorts(IEnumerable<string> registeredPorts)
    {
        var registered = registeredPorts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (registered.Count == 0) return [];

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Present, ConfigManagerErrorCode " +
                "FROM Win32_PnPEntity WHERE PNPClass = 'Ports'");
            using var devices = searcher.Get();
            var result = new Dictionary<string, SerialPortDevice>(StringComparer.OrdinalIgnoreCase);

            foreach (ManagementObject device in devices)
            {
                var isPresent = device["Present"] is true;
                var errorCode = device["ConfigManagerErrorCode"] is null
                    ? uint.MaxValue
                    : Convert.ToUInt32(device["ConfigManagerErrorCode"]);
                if (!isPresent || errorCode != 0) continue;

                var name = device["Name"] as string ?? string.Empty;
                var match = WindowsPortNameRegex().Match(name);
                if (!match.Success || !registered.Contains(match.Groups[1].Value)) continue;

                var portName = match.Groups[1].Value.ToUpperInvariant();
                var deviceName = name[..match.Index].Trim();
                if (deviceName.StartsWith("USB-SERIAL ", StringComparison.OrdinalIgnoreCase))
                    deviceName = deviceName["USB-SERIAL ".Length..].Trim();
                if (string.IsNullOrWhiteSpace(deviceName)) deviceName = "串口设备";

                result[portName] = new SerialPortDevice(portName, $"{deviceName}({portName})");
            }

            return result.Values.OrderBy(device => device.PortName, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            // 严格模式：无法确认设备当前在场时，不向用户提供可能失效的端口。
            return [];
        }
    }

    [GeneratedRegex(@"\((COM\d+)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPortNameRegex();
}

public sealed record SerialPortDevice(string PortName, string DisplayName);
