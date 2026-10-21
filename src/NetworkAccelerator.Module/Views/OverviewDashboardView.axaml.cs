using AsterDock.Contracts;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NetworkAccelerator.Core.Models;
using NetworkAccelerator.Module.Controls;
using NetworkAccelerator.Module.ViewModels;

namespace NetworkAccelerator.Module.Views;

public partial class OverviewDashboardView : UserControl
{
    private int _motionGeneration;

    public OverviewDashboardView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _motionGeneration++;
        DashboardMotion.Changed += OnMotionChanged;
        SyncMotionClass();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _motionGeneration++;
        DashboardMotion.Changed -= OnMotionChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnMotionChanged()
    {
        var generation = _motionGeneration;
        var weak = new WeakReference<OverviewDashboardView>(this);
        Dispatcher.UIThread.Post(() =>
        {
            if (!weak.TryGetTarget(out var view) || view._motionGeneration != generation) return;
            view.SyncMotionClass();
        });
    }

    private void SyncMotionClass() => Classes.Set("still", DashboardMotion.ReduceMotion);

    public event Action<ProxyMode>? ModeRequested;
    public event EventHandler? SubscriptionUpdateRequested;
    public event Action<string>? SectionRequested;

    private NetworkAcceleratorViewModel? ViewModel => DataContext as NetworkAcceleratorViewModel;

    private void ToggleConnection_Click(object? sender, RoutedEventArgs e) =>
        Run(model => model.ToggleConnectionAsync(), "连接失败");

    private void RuleMode_Click(object? sender, RoutedEventArgs e) => ModeRequested?.Invoke(ProxyMode.Rule);
    private void GlobalMode_Click(object? sender, RoutedEventArgs e) => ModeRequested?.Invoke(ProxyMode.Global);
    private void DirectMode_Click(object? sender, RoutedEventArgs e) => ModeRequested?.Invoke(ProxyMode.Direct);

    private void SystemProxyToggle_Click(object? sender, RoutedEventArgs e) =>
        Run(model => model.SetSystemProxyEnabledAsync(SystemProxyToggle.IsChecked == true), "系统代理");

    private void TunToggle_Click(object? sender, RoutedEventArgs e) =>
        Run(model => model.SetTunEnabledAsync(OverviewTunToggle.IsChecked == true), "虚拟网卡");

    private void AllowLanToggle_Click(object? sender, RoutedEventArgs e) =>
        Run(model => model.SetAllowLanAsync(AllowLanToggle.IsChecked == true), "局域网代理");

    private void CopyLanAddress_Click(object? sender, RoutedEventArgs e) =>
        Run(async model =>
        {
            if (model.LanAddressText == "—") return;
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
            await clipboard.SetTextAsync(model.LanAddressText);
            model.ShowStatusMessage("已复制内网 IP", FeedbackKind.Success);
        }, "复制失败");

    private void UpdateSubscription_Click(object? sender, RoutedEventArgs e) =>
        SubscriptionUpdateRequested?.Invoke(this, e);

    private void RefreshServices_Click(object? sender, RoutedEventArgs e) =>
        Run(model => model.RefreshServiceStatusAsync(), "服务状态测试失败");

    private void Run(Func<NetworkAcceleratorViewModel, Task> action, string errorTitle)
    {
        if (ViewModel is not { } model) return;
        model.Run(() => action(model), errorTitle);
    }

    private void OpenSection_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string section }) SectionRequested?.Invoke(section);
    }

    private void ConnectionTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var index))
            ViewModel?.Dash.SelectTab(index);
    }
}
