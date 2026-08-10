using AsterDock.Contracts;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DeviceInformation.Module.ViewModels;

namespace DeviceInformation.Module.Views;

public partial class DeviceInformationView : UserControl
{
    private readonly Action? _showWidget;
    private readonly ISystemMetricsService? _systemMetrics;
    private readonly DeviceInformationViewModel? _viewModel;
    private DeviceStatusWidgetWindow? _standaloneWidget;

    public DeviceInformationView()
    {
        InitializeComponent();
    }

    public DeviceInformationView(ISystemMetricsService systemMetrics, Action? showWidget)
    {
        InitializeComponent();
        _systemMetrics = systemMetrics;
        _showWidget = showWidget;
        _viewModel = new DeviceInformationViewModel(systemMetrics);
        DataContext = _viewModel;
        AttachedToVisualTree += (_, _) => _ = _viewModel.StartAsync();
        DetachedFromVisualTree += (_, _) => _viewModel.Stop();
    }

    public void DisposeResources()
    {
        _standaloneWidget?.Close();
        _standaloneWidget = null;
        _viewModel?.Dispose();
    }

    private void OpenWidget_Click(object? sender, RoutedEventArgs e)
    {
        if (_showWidget is not null)
        {
            _showWidget();
            return;
        }

        if (_standaloneWidget is not null)
        {
            _standaloneWidget.Activate();
            return;
        }

        if (_systemMetrics is null) return;
        _standaloneWidget = new DeviceStatusWidgetWindow(_systemMetrics);
        _standaloneWidget.Closed += (_, _) => _standaloneWidget = null;
        _standaloneWidget.Show();
    }
}
