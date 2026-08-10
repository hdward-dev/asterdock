using AsterDock.Contracts;
using Avalonia.Threading;
using Home.Module.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Home.Module.ViewModels;

public sealed class HomeViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IApplicationContext _context;
    private readonly ISystemMetricsService _systemMetrics;
    private IDisposable? _metricsSubscription;
    private bool _disposed;
    private string _availableApplicationsText = "0 个应用可用";
    private double _cpuUsage;
    private double _gpuUsage;
    private double _memoryUsage;
    private string _cpuUsageText = "0%";
    private string _gpuUsageText = "--";
    private string _memoryUsageText = "0%";
    private bool _hasApplications;
    private bool _hasRecentApplications;

    public HomeViewModel(IApplicationContext context)
    {
        _context = context;
        _systemMetrics = context.SystemMetrics;
        _context.Shell.StateChanged += Shell_StateChanged;
        RefreshShellState();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<HomeApplicationItem> Applications { get; } = [];
    public ObservableCollection<RecentApplicationItem> RecentApplications { get; } = [];
    public string AvailableApplicationsText { get => _availableApplicationsText; private set => SetField(ref _availableApplicationsText, value); }
    public double CpuUsage { get => _cpuUsage; private set => SetField(ref _cpuUsage, value); }
    public double GpuUsage { get => _gpuUsage; private set => SetField(ref _gpuUsage, value); }
    public double MemoryUsage { get => _memoryUsage; private set => SetField(ref _memoryUsage, value); }
    public string CpuUsageText { get => _cpuUsageText; private set => SetField(ref _cpuUsageText, value); }
    public string GpuUsageText { get => _gpuUsageText; private set => SetField(ref _gpuUsageText, value); }
    public string MemoryUsageText { get => _memoryUsageText; private set => SetField(ref _memoryUsageText, value); }
    public bool HasApplications { get => _hasApplications; private set => SetField(ref _hasApplications, value); }
    public bool HasRecentApplications { get => _hasRecentApplications; private set => SetField(ref _hasRecentApplications, value); }

    public Task StartAsync()
    {
        if (_disposed || _metricsSubscription is not null) return Task.CompletedTask;
        _metricsSubscription = _systemMetrics.Subscribe(ApplyMetrics, HandleMetricsError);
        return Task.CompletedTask;
    }

    public void Stop()
    {
        Interlocked.Exchange(ref _metricsSubscription, null)?.Dispose();
    }

    public void OpenApplication(string applicationId) => _context.Shell.OpenApplication(applicationId);
    public void OpenInvoicePrinter() => _context.Shell.OpenApplication("invoice-printer");
    public void OpenDeviceInformation() => _context.Shell.OpenApplication("device-information");
    public void ShowSettings() => _context.Shell.ShowSettings();
    public void ShowApplicationSwitcher() => _context.Shell.ShowApplicationSwitcher();

    public void ToggleDeviceWidget()
        => _context.Shell.TryExecuteApplicationAction("device-information", "toggle-desktop-widget");

    public void Dispose()
    {
        _context.Shell.StateChanged -= Shell_StateChanged;
        _disposed = true;
        Stop();
    }

    private void Shell_StateChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) RefreshShellState();
        else Dispatcher.UIThread.Post(RefreshShellState);
    }

    private void RefreshShellState()
    {
        Applications.Clear();
        foreach (var application in _context.Shell.Applications.Where(application =>
                     !string.Equals(application.Id, _context.ApplicationId, StringComparison.OrdinalIgnoreCase)))
            Applications.Add(new HomeApplicationItem(application));

        var applicationCount = Applications.Count;
        Applications.Add(HomeApplicationItem.CreateAddTile());

        RecentApplications.Clear();
        foreach (var recent in _context.Shell.RecentApplications.Where(recent =>
                     !string.Equals(recent.Application.Id, _context.ApplicationId, StringComparison.OrdinalIgnoreCase)).Take(4))
            RecentApplications.Add(new RecentApplicationItem(recent));

        HasApplications = applicationCount > 0;
        HasRecentApplications = RecentApplications.Count > 0;
        AvailableApplicationsText = $"{applicationCount} 个应用可用";
    }

    private void ApplyMetrics(SystemMetricsSnapshot snapshot)
    {
        void Apply()
        {
            CpuUsage = snapshot.CpuUsage;
            GpuUsage = snapshot.GpuUsage ?? 0;
            MemoryUsage = snapshot.TotalMemoryBytes <= 0 ? 0 : snapshot.UsedMemoryBytes * 100d / snapshot.TotalMemoryBytes;
            CpuUsageText = FormatPercent(snapshot.CpuUsage);
            GpuUsageText = snapshot.GpuUsage is null ? "--" : FormatPercent(snapshot.GpuUsage.Value);
            MemoryUsageText = FormatPercent(MemoryUsage);
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    private void HandleMetricsError(Exception exception)
    {
        // The homepage keeps the last successful values. The detailed device
        // page surfaces sampling errors to the user.
    }

    private static string FormatPercent(double value) => $"{Math.Round(value):0}%";

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
