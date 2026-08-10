using DeviceInformation.Core.Models;
using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace DeviceInformation.Core.Services;

public sealed partial class DeviceInformationService : IDeviceInformationService
{
    private readonly object _sampleLock = new();
    private CpuTimes _previousCpuTimes;
    private NetworkTotals _previousNetworkTotals;
    private DateTimeOffset _previousNetworkTimestamp;
    private DateTimeOffset _lastGpuQuery;
    private (double? Usage, double? Temperature) _cachedGpuMetrics;
    private DeviceDetails? _details;
    private IntPtr _gpuQuery;
    private IntPtr _gpuCounter;

    public DeviceInformationService()
    {
        _previousCpuTimes = ReadWindowsCpuTimes();
        _previousNetworkTotals = ReadNetworkTotals();
        _previousNetworkTimestamp = DateTimeOffset.UtcNow;
        if (OperatingSystem.IsWindows()) InitializeWindowsGpuCounter();
    }

    public async Task<DeviceDetails> GetDetailsAsync(CancellationToken cancellationToken = default)
    {
        if (_details is not null) return _details;

        var totalMemory = ReadMemory().Total;
        var processor = await GetProcessorNameAsync(cancellationToken).ConfigureAwait(false);
        var graphics = await GetGraphicsNameAsync(cancellationToken).ConfigureAwait(false);
        var systemDrive = GetSystemDrive();
        _details = new DeviceDetails(
            Environment.MachineName,
            await GetDeviceModelAsync(cancellationToken).ConfigureAwait(false),
            RuntimeInformation.OSDescription.Trim(),
            RuntimeInformation.OSArchitecture.ToString(),
            processor,
            graphics,
            totalMemory,
            systemDrive.Name);
        return _details;
    }

    public async Task<DeviceMetricsSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var memory = ReadMemory();
        var network = ReadNetworkRate(now);
        var gpu = await ReadGpuMetricsAsync(now, cancellationToken).ConfigureAwait(false);
        var drive = GetSystemDrive();
        var diskUsage = drive.TotalSize <= 0
            ? 0
            : (drive.TotalSize - drive.AvailableFreeSpace) * 100d / drive.TotalSize;

        return new DeviceMetricsSnapshot(
            now,
            await ReadCpuUsageAsync(cancellationToken).ConfigureAwait(false),
            ReadCpuTemperature(),
            gpu.Usage,
            gpu.Temperature,
            memory.Used,
            memory.Total,
            Math.Clamp(diskUsage, 0, 100),
            network.DownloadPerSecond,
            network.UploadPerSecond);
    }

    public void Dispose()
    {
        if (_gpuQuery == IntPtr.Zero) return;
        PdhCloseQuery(_gpuQuery);
        _gpuQuery = IntPtr.Zero;
        _gpuCounter = IntPtr.Zero;
    }

    private async Task<double> ReadCpuUsageAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            lock (_sampleLock)
            {
                var current = ReadWindowsCpuTimes();
                var idle = current.Idle - _previousCpuTimes.Idle;
                var total = current.Kernel + current.User - _previousCpuTimes.Kernel - _previousCpuTimes.User;
                _previousCpuTimes = current;
                if (total == 0) return 0;
                return Math.Clamp((total - idle) * 100d / total, 0, 100);
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            var output = await RunCommandAsync("/usr/bin/top", ["-l", "1", "-n", "0"], cancellationToken)
                .ConfigureAwait(false);
            var match = IdleCpuRegex().Match(output);
            if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var idle))
                return Math.Clamp(100 - idle, 0, 100);
        }

        if (OperatingSystem.IsLinux()) return ReadLinuxCpuUsage();
        return 0;
    }

    private double ReadLinuxCpuUsage()
    {
        try
        {
            var values = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Skip(1).Select(value => ulong.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            if (values.Length < 4) return 0;
            var totalTicks = values.Aggregate(0UL, static (total, value) => total + value);
            var current = new CpuTimes(values[3] + (values.Length > 4 ? values[4] : 0), totalTicks, 0);
            lock (_sampleLock)
            {
                var idle = current.Idle - _previousCpuTimes.Idle;
                var total = current.Kernel - _previousCpuTimes.Kernel;
                _previousCpuTimes = current;
                return total == 0 ? 0 : Math.Clamp((total - idle) * 100d / total, 0, 100);
            }
        }
        catch
        {
            return 0;
        }
    }

    private static (long Used, long Total) ReadMemory()
    {
        if (OperatingSystem.IsWindows()) return ReadWindowsMemory();
        if (OperatingSystem.IsLinux()) return ReadLinuxMemory();
        if (OperatingSystem.IsMacOS()) return ReadMacMemory();
        return (0, 0);
    }

    [SupportedOSPlatform("windows")]
    private static (long Used, long Total) ReadWindowsMemory()
    {
        var status = new MemoryStatusEx();
        return GlobalMemoryStatusEx(ref status)
            ? ((long)(status.TotalPhysical - status.AvailablePhysical), (long)status.TotalPhysical)
            : (0, 0);
    }

    private static (long Used, long Total) ReadLinuxMemory()
    {
        try
        {
            var values = File.ReadLines("/proc/meminfo")
                .Select(line => line.Split(':', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => ParseKilobytes(parts[1]), StringComparer.Ordinal);
            var total = values.GetValueOrDefault("MemTotal") * 1024;
            var available = values.GetValueOrDefault("MemAvailable") * 1024;
            return (Math.Max(0, total - available), total);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static (long Used, long Total) ReadMacMemory()
    {
        try
        {
            var totalText = RunCommand("/usr/sbin/sysctl", ["-n", "hw.memsize"]);
            var vmText = RunCommand("/usr/bin/vm_stat", []);
            if (!long.TryParse(totalText.Trim(), CultureInfo.InvariantCulture, out var total)) return (0, 0);
            var pageSizeMatch = PageSizeRegex().Match(vmText);
            var pageSize = pageSizeMatch.Success
                ? long.Parse(pageSizeMatch.Groups[1].Value, CultureInfo.InvariantCulture)
                : 4096;
            var freePages = ReadVmStatPages(vmText, "Pages free")
                + ReadVmStatPages(vmText, "Pages inactive")
                + ReadVmStatPages(vmText, "Pages speculative");
            return (Math.Clamp(total - freePages * pageSize, 0, total), total);
        }
        catch
        {
            return (0, 0);
        }
    }

    private (long DownloadPerSecond, long UploadPerSecond) ReadNetworkRate(DateTimeOffset now)
    {
        var current = ReadNetworkTotals();
        lock (_sampleLock)
        {
            var seconds = Math.Max(0.001, (now - _previousNetworkTimestamp).TotalSeconds);
            var download = current.Received >= _previousNetworkTotals.Received
                ? (long)((current.Received - _previousNetworkTotals.Received) / seconds)
                : 0;
            var upload = current.Sent >= _previousNetworkTotals.Sent
                ? (long)((current.Sent - _previousNetworkTotals.Sent) / seconds)
                : 0;
            _previousNetworkTotals = current;
            _previousNetworkTimestamp = now;
            return (download, upload);
        }
    }

    private static NetworkTotals ReadNetworkTotals()
    {
        long received = 0;
        long sent = 0;
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    adapter.OperationalStatus != OperationalStatus.Up) continue;
                var statistics = adapter.GetIPStatistics();
                received += statistics.BytesReceived;
                sent += statistics.BytesSent;
            }
        }
        catch
        {
            // A disconnected adapter may disappear while statistics are being read.
        }
        return new NetworkTotals(received, sent);
    }

    private async Task<(double? Usage, double? Temperature)> ReadGpuMetricsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var windowsUsage = OperatingSystem.IsWindows() ? ReadWindowsGpuUsage() : null;
        if (now - _lastGpuQuery < TimeSpan.FromSeconds(3))
            return (windowsUsage ?? _cachedGpuMetrics.Usage, _cachedGpuMetrics.Temperature);
        _lastGpuQuery = now;

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return _cachedGpuMetrics;
        if (_details?.GraphicsName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) != true)
            return (windowsUsage ?? _cachedGpuMetrics.Usage, _cachedGpuMetrics.Temperature);
        var output = await RunCommandAsync(
            "nvidia-smi",
            ["--query-gpu=utilization.gpu,temperature.gpu", "--format=csv,noheader,nounits"],
            cancellationToken).ConfigureAwait(false);
        var firstLine = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var values = firstLine?.Split(',', StringSplitOptions.TrimEntries);
        if (values is { Length: >= 2 } &&
            double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var usage) &&
            double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature))
            _cachedGpuMetrics = (Math.Clamp(usage, 0, 100), temperature);
        return (windowsUsage ?? _cachedGpuMetrics.Usage, _cachedGpuMetrics.Temperature);
    }

    [SupportedOSPlatform("windows")]
    private void InitializeWindowsGpuCounter()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _gpuQuery) != ErrorSuccess) return;
        if (PdhAddEnglishCounter(_gpuQuery, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpuCounter) == ErrorSuccess)
        {
            PdhCollectQueryData(_gpuQuery);
            return;
        }

        PdhCloseQuery(_gpuQuery);
        _gpuQuery = IntPtr.Zero;
        _gpuCounter = IntPtr.Zero;
    }

    [SupportedOSPlatform("windows")]
    private double? ReadWindowsGpuUsage()
    {
        if (_gpuQuery == IntPtr.Zero || _gpuCounter == IntPtr.Zero || PdhCollectQueryData(_gpuQuery) != ErrorSuccess)
            return null;

        uint bufferSize = 0;
        uint itemCount = 0;
        var status = PdhGetFormattedCounterArray(
            _gpuCounter, PdhFormatDouble, ref bufferSize, ref itemCount, IntPtr.Zero);
        if (status != PdhMoreData || bufferSize == 0 || itemCount == 0) return null;

        var buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            status = PdhGetFormattedCounterArray(
                _gpuCounter, PdhFormatDouble, ref bufferSize, ref itemCount, buffer);
            if (status != ErrorSuccess) return null;

            var itemSize = Marshal.SizeOf<PdhFormattedCounterValueItem>();
            var usageByEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < itemCount; index++)
            {
                var itemPointer = IntPtr.Add(buffer, checked((int)index * itemSize));
                var item = Marshal.PtrToStructure<PdhFormattedCounterValueItem>(itemPointer);
                if (item.Value.Status != ErrorSuccess || double.IsNaN(item.Value.DoubleValue)) continue;
                var instanceName = Marshal.PtrToStringUni(item.Name) ?? string.Empty;
                var engineMarker = instanceName.LastIndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
                var engineName = engineMarker >= 0 ? instanceName[engineMarker..] : instanceName;
                usageByEngine[engineName] = usageByEngine.GetValueOrDefault(engineName) + item.Value.DoubleValue;
            }

            return usageByEngine.Count == 0 ? null : Math.Clamp(usageByEngine.Values.Max(), 0, 100);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static double? ReadCpuTemperature()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            foreach (var path in Directory.EnumerateFiles("/sys/class/thermal", "temp", SearchOption.AllDirectories))
            {
                if (double.TryParse(File.ReadAllText(path), CultureInfo.InvariantCulture, out var raw))
                    return raw > 1000 ? raw / 1000 : raw;
            }
        }
        catch
        {
            // Temperature sensors are optional and may require elevated permissions.
        }
        return null;
    }

    private static async Task<string> GetProcessorNameAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows()) return GetWindowsRegistryString(
            @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "未知处理器");
        if (OperatingSystem.IsMacOS())
            return (await RunCommandAsync("/usr/sbin/sysctl", ["-n", "machdep.cpu.brand_string"], cancellationToken)
                .ConfigureAwait(false)).Trim() is { Length: > 0 } macCpu ? macCpu : "Apple Silicon";
        if (OperatingSystem.IsLinux())
        {
            var line = File.ReadLines("/proc/cpuinfo").FirstOrDefault(item => item.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
            return line?.Split(':', 2).ElementAtOrDefault(1)?.Trim() ?? RuntimeInformation.ProcessArchitecture.ToString();
        }
        return RuntimeInformation.ProcessArchitecture.ToString();
    }

    private static async Task<string> GetGraphicsNameAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows()) return GetWindowsGraphicsName();
        if (OperatingSystem.IsMacOS())
        {
            var output = await RunCommandAsync("/usr/sbin/system_profiler", ["SPDisplaysDataType"], cancellationToken)
                .ConfigureAwait(false);
            var match = ChipsetModelRegex().Match(output);
            return match.Success ? match.Groups[1].Value.Trim() : "系统图形处理器";
        }
        if (OperatingSystem.IsLinux())
        {
            var output = await RunCommandAsync("lspci", [], cancellationToken).ConfigureAwait(false);
            return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.Contains("VGA", StringComparison.OrdinalIgnoreCase) ||
                                        line.Contains("3D controller", StringComparison.OrdinalIgnoreCase))
                ?.Split(':', 3).LastOrDefault()?.Trim() ?? "系统图形处理器";
        }
        return "系统图形处理器";
    }

    private static async Task<string> GetDeviceModelAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows()) return GetWindowsRegistryString(
            @"HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName", "Windows 设备");
        if (OperatingSystem.IsMacOS())
        {
            var model = await RunCommandAsync("/usr/sbin/sysctl", ["-n", "hw.model"], cancellationToken)
                .ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(model) ? "Mac" : model.Trim();
        }
        return Environment.MachineName;
    }

    [SupportedOSPlatform("windows")]
    private static string GetWindowsRegistryString(string subkey, string valueName, string fallback)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subkey);
            return key?.GetValue(valueName)?.ToString()?.Trim() is { Length: > 0 } value ? value : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string GetWindowsGraphicsName()
    {
        const string displayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(displayClass);
            if (root is null) return "系统图形处理器";
            foreach (var subkeyName in root.GetSubKeyNames().Order())
            {
                using var adapter = root.OpenSubKey(subkeyName);
                var description = adapter?.GetValue("DriverDesc")?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(description)) return description;
            }
        }
        catch
        {
            // Some display adapter registry entries are access restricted.
        }
        return "系统图形处理器";
    }

    private static DriveInfo GetSystemDrive()
    {
        var root = OperatingSystem.IsWindows()
            ? Path.GetPathRoot(Environment.SystemDirectory)
            : Path.DirectorySeparatorChar.ToString();
        try
        {
            return new DriveInfo(string.IsNullOrWhiteSpace(root) ? Path.DirectorySeparatorChar.ToString() : root);
        }
        catch
        {
            return DriveInfo.GetDrives().First(drive => drive.IsReady);
        }
    }

    private static long ParseKilobytes(string value)
    {
        var token = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return long.TryParse(token, CultureInfo.InvariantCulture, out var result) ? result : 0;
    }

    private static long ReadVmStatPages(string output, string key)
    {
        var line = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(item => item.StartsWith(key, StringComparison.Ordinal));
        if (line is null) return 0;
        var value = line.Split(':', 2).ElementAtOrDefault(1)?.Trim().TrimEnd('.');
        return long.TryParse(value, CultureInfo.InvariantCulture, out var pages) ? pages : 0;
    }

    private static string RunCommand(string fileName, IReadOnlyList<string> arguments)
        => RunCommandAsync(fileName, arguments, CancellationToken.None).GetAwaiter().GetResult();

    private static async Task<string> RunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            if (process is null) return string.Empty;
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await errorTask.ConfigureAwait(false);
            return process.ExitCode == 0 ? await outputTask.ConfigureAwait(false) : string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static CpuTimes ReadWindowsCpuTimes()
    {
        if (!OperatingSystem.IsWindows() || !GetSystemTimes(out var idle, out var kernel, out var user)) return default;
        return new CpuTimes(ToUInt64(idle), ToUInt64(kernel), ToUInt64(user));
    }

    private static ulong ToUInt64(FileTime value) => ((ulong)value.HighDateTime << 32) | value.LowDateTime;

    [GeneratedRegex(@"([0-9]+(?:\.[0-9]+)?)%\s+idle", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IdleCpuRegex();

    [GeneratedRegex(@"page size of\s+([0-9]+) bytes", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PageSizeRegex();

    [GeneratedRegex(@"Chipset Model:\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChipsetModelRegex();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(
        IntPtr counter,
        uint format,
        ref uint bufferSize,
        ref uint itemCount,
        IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;

        public MemoryStatusEx() => Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedCounterValue
    {
        public uint Status;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedCounterValueItem
    {
        public IntPtr Name;
        public PdhFormattedCounterValue Value;
    }

    private const uint ErrorSuccess = 0;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhFormatDouble = 0x00000200;

    private readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);
    private readonly record struct NetworkTotals(long Received, long Sent);
}
