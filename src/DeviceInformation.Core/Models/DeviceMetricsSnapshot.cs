namespace DeviceInformation.Core.Models;

public sealed record DeviceMetricsSnapshot(
    DateTimeOffset Timestamp,
    double CpuUsage,
    double? CpuTemperature,
    double? GpuUsage,
    double? GpuTemperature,
    long UsedMemoryBytes,
    long TotalMemoryBytes,
    double DiskUsage,
    long DownloadBytesPerSecond,
    long UploadBytesPerSecond);
