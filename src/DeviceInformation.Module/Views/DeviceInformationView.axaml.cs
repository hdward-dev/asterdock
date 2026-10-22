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
    private readonly bool _ownsModel;
    private bool _observing;
    private DeviceStatusWidgetWindow? _standaloneWidget;

    public DeviceInformationView()
    {
        InitializeComponent();
    }

    public DeviceInformationView(ISystemMetricsService systemMetrics, Action? showWidget, IUserFeedbackService? feedback = null)
        : this(new DeviceInformationViewModel(systemMetrics, feedback), showWidget, ownsModel: true)
    {
        _systemMetrics = systemMetrics;
    }

    public DeviceInformationView(DeviceInformationViewModel viewModel, Action? showWidget, bool ownsModel = false)
    {
        InitializeComponent();
        _showWidget = showWidget;
        _viewModel = viewModel;
        _ownsModel = ownsModel;
        DataContext = viewModel;
        AttachedToVisualTree += async (_, _) =>
        {
            if (_observing || _viewModel is null) return;
            _observing = true;
            await _viewModel.AttachAsync();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (!_observing || _viewModel is null) return;
            _observing = false;
            _viewModel.Detach();
        };
    }

    public void DisposeResources()
    {
        _standaloneWidget?.Close();
        _standaloneWidget = null;
        if (_observing && _viewModel is not null)
        {
            _observing = false;
            _viewModel.Detach();
        }

        if (_ownsModel) _viewModel?.Dispose();
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
