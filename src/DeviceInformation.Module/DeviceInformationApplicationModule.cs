using AsterDock.Contracts;
using Avalonia.Controls;
using DeviceInformation.Module.ViewModels;
using DeviceInformation.Module.Views;

namespace DeviceInformation.Module;

public sealed class DeviceInformationApplicationModule :
    IApplicationModule,
    IApplicationContextAware,
    IApplicationQuickActionProvider
{
    private readonly List<DeviceInformationView> _views = [];
    private DeviceInformationViewModel? _model;
    private IApplicationContext? _context;
    private DeviceStatusWidgetWindow? _widgetWindow;

    public void Initialize(IApplicationContext context) => _context = context;

    public Control CreateView()
    {
        _model ??= new DeviceInformationViewModel(GetSystemMetrics(), _context?.Feedback);
        var view = new DeviceInformationView(_model, ShowWidget);
        _views.Add(view);
        return view;
    }

    public IReadOnlyList<ApplicationQuickAction> GetQuickActions() =>
    [
        new ApplicationQuickAction(
            "toggle-desktop-widget",
            "显示/隐藏设备监控窗",
            ToggleWidget)
    ];

    public void Dispose()
    {
        _widgetWindow?.Close();
        _widgetWindow = null;
        foreach (var view in _views) view.DisposeResources();
        _views.Clear();
        _model?.Dispose();
        _model = null;
        _context = null;
    }

    private void ShowWidget()
    {
        if (_widgetWindow is not null)
        {
            _widgetWindow.Activate();
            return;
        }

        var window = new DeviceStatusWidgetWindow(GetSystemMetrics(), _context?.Feedback);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_widgetWindow, window)) _widgetWindow = null;
        };
        _widgetWindow = window;
        if (_context is not null) _context.Windows.Show(window, owned: false);
        else window.Show();
    }

    private void ToggleWidget()
    {
        if (_widgetWindow is null) ShowWidget();
        else _widgetWindow.Close();
    }

    private ISystemMetricsService GetSystemMetrics() =>
        _context?.SystemMetrics ?? throw new InvalidOperationException("设备信息应用尚未完成容器初始化");
}
