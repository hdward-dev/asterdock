using AsterDock.Contracts;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using DeviceInformation.Module.ViewModels;

namespace DeviceInformation.Module.Views;

public partial class DeviceStatusWidgetWindow : Window
{
    private const int ScreenMargin = 18;
    private readonly DeviceInformationViewModel? _viewModel;

    public DeviceStatusWidgetWindow()
    {
        InitializeComponent();
    }

    public DeviceStatusWidgetWindow(ISystemMetricsService systemMetrics)
        : this()
    {
        _viewModel = new DeviceInformationViewModel(systemMetrics);
        DataContext = _viewModel;
        // AcrylicBlur/Blur color the whole rectangular native surface. Per-pixel
        // transparency keeps the area outside the rounded root border invisible.
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        MoveToTopRight();
        if (_viewModel is not null) _ = _viewModel.StartAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        Closed -= OnClosed;
        _viewModel?.Dispose();
    }

    private void MoveToTopRight()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var scale = Math.Max(1, RenderScaling);
        var width = (int)Math.Ceiling(Width * scale);
        Position = new PixelPoint(
            screen.WorkingArea.Right - width - ScreenMargin,
            screen.WorkingArea.Y + ScreenMargin);
    }

    private void Pin_Click(object? sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        ToolTip.SetTip(PinButton, Topmost ? "取消置顶" : "保持置顶");
        PinButton.Opacity = Topmost ? 1 : 0.55;
    }

    private void CloseWidget_Click(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(Close, DispatcherPriority.Background);
}
