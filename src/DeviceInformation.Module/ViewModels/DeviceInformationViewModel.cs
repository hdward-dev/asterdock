using AsterDock.Contracts;
using Avalonia.Threading;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace DeviceInformation.Module.ViewModels;

public sealed class DeviceInformationViewModel : INotifyPropertyChanged, IDisposable
{
    private const int HistoryLength = 48;
    private readonly ISystemMetricsService _service;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Queue<double> _cpuHistory = new();
    private readonly Queue<double> _gpuHistory = new();
    private IDisposable? _metricsSubscription;
    private int _detailsLoaded;
    private bool _disposed;
    private string _statusText = "正在读取设备信息…";
    private string _deviceName = Environment.MachineName;
    private string _deviceModel = "正在识别";
    private string _operatingSystem = "正在识别";
    private string _architecture = "--";
    private string _processorName = "正在识别处理器";
    private string _graphicsName = "正在识别图形处理器";
    private string _totalMemoryText = "--";
    private string _systemDrive = "--";
    private double _cpuUsage;
    private double _gpuUsage;
    private double _memoryUsage;
    private double _diskUsage;
    private string _cpuUsageText = "0%";
    private string _gpuUsageText = "--";
    private string _memoryUsageText = "0%";
    private string _diskUsageText = "0%";
    private string _cpuTemperatureText = "--";
    private string _gpuTemperatureText = "--";
    private string _memoryText = "-- / --";
    private string _downloadText = "↓ 0 Kbps";
    private string _uploadText = "↑ 0 Kbps";
    private IReadOnlyList<double> _cpuHistoryValues = Array.Empty<double>();
    private IReadOnlyList<double> _gpuHistoryValues = Array.Empty<double>();

    public DeviceInformationViewModel(ISystemMetricsService service) => _service = service;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public string DeviceName { get => _deviceName; private set => SetField(ref _deviceName, value); }
    public string DeviceModel { get => _deviceModel; private set => SetField(ref _deviceModel, value); }
    public string OperatingSystem { get => _operatingSystem; private set => SetField(ref _operatingSystem, value); }
    public string Architecture { get => _architecture; private set => SetField(ref _architecture, value); }
    public string ProcessorName { get => _processorName; private set => SetField(ref _processorName, value); }
    public string GraphicsName { get => _graphicsName; private set => SetField(ref _graphicsName, value); }
    public string TotalMemoryText { get => _totalMemoryText; private set => SetField(ref _totalMemoryText, value); }
    public string SystemDrive { get => _systemDrive; private set => SetField(ref _systemDrive, value); }
    public double CpuUsage { get => _cpuUsage; private set => SetField(ref _cpuUsage, value); }
    public double GpuUsage { get => _gpuUsage; private set => SetField(ref _gpuUsage, value); }
    public double MemoryUsage { get => _memoryUsage; private set => SetField(ref _memoryUsage, value); }
    public double DiskUsage { get => _diskUsage; private set => SetField(ref _diskUsage, value); }
    public string CpuUsageText { get => _cpuUsageText; private set => SetField(ref _cpuUsageText, value); }
    public string GpuUsageText { get => _gpuUsageText; private set => SetField(ref _gpuUsageText, value); }
    public string MemoryUsageText { get => _memoryUsageText; private set => SetField(ref _memoryUsageText, value); }
    public string DiskUsageText { get => _diskUsageText; private set => SetField(ref _diskUsageText, value); }
    public string CpuTemperatureText { get => _cpuTemperatureText; private set => SetField(ref _cpuTemperatureText, value); }
    public string GpuTemperatureText { get => _gpuTemperatureText; private set => SetField(ref _gpuTemperatureText, value); }
    public string MemoryText { get => _memoryText; private set => SetField(ref _memoryText, value); }
    public string DownloadText { get => _downloadText; private set => SetField(ref _downloadText, value); }
    public string UploadText { get => _uploadText; private set => SetField(ref _uploadText, value); }
    public IReadOnlyList<double> CpuHistoryValues { get => _cpuHistoryValues; private set => SetField(ref _cpuHistoryValues, value); }
    public IReadOnlyList<double> GpuHistoryValues { get => _gpuHistoryValues; private set => SetField(ref _gpuHistoryValues, value); }

    public async Task StartAsync()
    {
        if (_disposed) return;
        _metricsSubscription ??= _service.Subscribe(ReceiveSnapshot, ReceiveError);
        if (Interlocked.Exchange(ref _detailsLoaded, 1) != 0) return;
        try
        {
            var details = await _service.GetDeviceDetailsAsync(_cancellation.Token).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyDetails(details);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusText = $"读取失败：{exception.GetBaseException().Message}");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
        _cancellation.Cancel();
        _cancellation.Dispose();
    }

    public void Stop()
    {
        Interlocked.Exchange(ref _metricsSubscription, null)?.Dispose();
    }

    private void ReceiveSnapshot(SystemMetricsSnapshot snapshot)
    {
        if (Dispatcher.UIThread.CheckAccess()) ApplySnapshot(snapshot);
        else Dispatcher.UIThread.Post(() => ApplySnapshot(snapshot));
    }

    private void ReceiveError(Exception exception)
    {
        Dispatcher.UIThread.Post(() =>
            StatusText = $"监控已暂停：{exception.GetBaseException().Message}");
    }

    private void ApplyDetails(SystemDeviceDetails details)
    {
        DeviceName = details.DeviceName;
        DeviceModel = details.DeviceModel;
        OperatingSystem = details.OperatingSystem;
        Architecture = details.Architecture;
        ProcessorName = details.ProcessorName;
        GraphicsName = details.GraphicsName;
        TotalMemoryText = FormatBytes(details.TotalMemoryBytes);
        SystemDrive = details.SystemDrive;
    }

    private void ApplySnapshot(SystemMetricsSnapshot snapshot)
    {
        CpuUsage = snapshot.CpuUsage;
        GpuUsage = snapshot.GpuUsage ?? 0;
        MemoryUsage = snapshot.TotalMemoryBytes <= 0 ? 0 : snapshot.UsedMemoryBytes * 100d / snapshot.TotalMemoryBytes;
        DiskUsage = snapshot.DiskUsage;
        CpuUsageText = FormatPercent(snapshot.CpuUsage);
        GpuUsageText = snapshot.GpuUsage is null ? "--" : FormatPercent(snapshot.GpuUsage.Value);
        MemoryUsageText = FormatPercent(MemoryUsage);
        DiskUsageText = FormatPercent(snapshot.DiskUsage);
        CpuTemperatureText = FormatTemperature(snapshot.CpuTemperature);
        GpuTemperatureText = FormatTemperature(snapshot.GpuTemperature);
        MemoryText = $"{FormatBytes(snapshot.UsedMemoryBytes)} / {FormatBytes(snapshot.TotalMemoryBytes)}";
        DownloadText = $"↓ {FormatRate(snapshot.DownloadBytesPerSecond)}";
        UploadText = $"↑ {FormatRate(snapshot.UploadBytesPerSecond)}";
        CpuHistoryValues = AppendHistory(_cpuHistory, snapshot.CpuUsage);
        GpuHistoryValues = AppendHistory(_gpuHistory, snapshot.GpuUsage ?? 0);
        StatusText = $"●  实时监控 · {snapshot.Timestamp:HH:mm:ss}";
    }

    private static IReadOnlyList<double> AppendHistory(Queue<double> history, double value)
    {
        history.Enqueue(value);
        while (history.Count > HistoryLength) history.Dequeue();
        return history.ToArray();
    }

    private static string FormatPercent(double value) => $"{Math.Round(value):0}%";

    private static string FormatTemperature(double? value) => value is null ? "--" : $"{Math.Round(value.Value):0}°C";

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "--";
        var gibibytes = bytes / 1024d / 1024d / 1024d;
        return gibibytes >= 10 ? $"{gibibytes:0.0} GB" : $"{gibibytes:0.00} GB";
    }

    private static string FormatRate(long bytesPerSecond)
    {
        var bitsPerSecond = Math.Max(0, bytesPerSecond) * 8d;
        if (bitsPerSecond >= 1_000_000) return $"{bitsPerSecond / 1_000_000:0.0} Mbps";
        return $"{bitsPerSecond / 1_000:0} Kbps";
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
