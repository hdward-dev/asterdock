using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using NetworkAccelerator.Core.Models;
using NetworkAccelerator.Module.ViewModels;

namespace NetworkAccelerator.Module.Views;

public partial class OverviewDashboardView : UserControl
{
    public OverviewDashboardView() => InitializeComponent();

    public event Action<ProxyMode>? ModeRequested;
    public event EventHandler? SubscriptionUpdateRequested;
    public event Action<string>? SectionRequested;

    private NetworkAcceleratorViewModel? ViewModel => DataContext as NetworkAcceleratorViewModel;

    private async void ToggleConnection_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null) await ViewModel.ToggleConnectionAsync();
    }

    private void RuleMode_Click(object? sender, RoutedEventArgs e) => ModeRequested?.Invoke(ProxyMode.Rule);
    private void GlobalMode_Click(object? sender, RoutedEventArgs e) => ModeRequested?.Invoke(ProxyMode.Global);
    private void DirectMode_Click(object? sender, RoutedEventArgs e) => ModeRequested?.Invoke(ProxyMode.Direct);

    private async void SystemProxyToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null) await ViewModel.SetTunEnabledAsync(SystemProxyToggle.IsChecked != true);
    }

    private async void TunToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null) await ViewModel.SetTunEnabledAsync(OverviewTunToggle.IsChecked == true);
    }

    private async void AllowLanToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null) await ViewModel.SetAllowLanAsync(AllowLanToggle.IsChecked == true);
    }

    private async void CopyLanAddress_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || ViewModel.LanAddressText == "—") return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(ViewModel.LanAddressText);
        ViewModel.ShowStatusMessage("已复制内网 IP");
    }

    private void UpdateSubscription_Click(object? sender, RoutedEventArgs e) =>
        SubscriptionUpdateRequested?.Invoke(this, e);

    private async void RefreshServices_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null) await ViewModel.RefreshServiceStatusAsync();
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
