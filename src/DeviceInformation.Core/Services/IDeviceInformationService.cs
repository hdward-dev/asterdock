using DeviceInformation.Core.Models;

namespace DeviceInformation.Core.Services;

public interface IDeviceInformationService : IDisposable
{
    Task<DeviceDetails> GetDetailsAsync(CancellationToken cancellationToken = default);
    Task<DeviceMetricsSnapshot> CaptureAsync(CancellationToken cancellationToken = default);
}
