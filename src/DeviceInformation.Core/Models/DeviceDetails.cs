namespace DeviceInformation.Core.Models;

public sealed record DeviceDetails(
    string DeviceName,
    string DeviceModel,
    string OperatingSystem,
    string Architecture,
    string ProcessorName,
    string GraphicsName,
    long TotalMemoryBytes,
    string SystemDrive);
