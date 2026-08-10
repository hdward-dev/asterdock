using AsterDock.Contracts;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using NetworkAccelerator.Core.Models;
using NetworkAccelerator.Module.Models;
using NetworkAccelerator.Module.ViewModels;

namespace NetworkAccelerator.Module.Views;

public partial class NetworkAcceleratorView : UserControl
{
    private readonly IApplicationContext? _context;
    private readonly NetworkAcceleratorViewModel? _viewModel;
    private bool _initialized;

    public NetworkAcceleratorView()
    {
        InitializeComponent();
        ModeSliderHost.SizeChanged += (_, _) => UpdateModeSlider();
    }

    public NetworkAcceleratorView(IApplicationContext context, string moduleDirectory) : this()
    {
        _context = context;
        _viewModel = new NetworkAcceleratorViewModel(context.DataDirectory, moduleDirectory);
        DataContext = _viewModel;
        AttachedToVisualTree += (_, _) =>
        {
            if (_initialized || _viewModel is null) return;
            _initialized = true;
            _ = InitializeAsync();
        };
    }

    public Task ToggleConnectionAsync() => _viewModel?.ToggleConnectionAsync() ?? Task.CompletedTask;

    public void DisposeResources()
    {
        _viewModel?.Dispose();
    }

    private async Task InitializeAsync()
    {
        await _viewModel!.InitializeAsync();
        UpdateModeButtons();
    }

    private async void ToggleConnection_Click(object? sender, RoutedEventArgs e) => await _viewModel!.ToggleConnectionAsync();
    private async void RefreshLatency_Click(object? sender, RoutedEventArgs e) => await _viewModel!.RefreshLatenciesAsync();
    private async void InstallCore_Click(object? sender, RoutedEventArgs e) => await _viewModel!.InstallCoreAsync();

    private async void CopyTerminalProxyCommand_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        var port = _viewModel.ProxyPort;
        var command = OperatingSystem.IsWindows()
            ? $"$env:HTTP_PROXY='http://127.0.0.1:{port}'; $env:HTTPS_PROXY='http://127.0.0.1:{port}'; $env:ALL_PROXY='socks5://127.0.0.1:{port}'; $env:NO_PROXY='localhost,127.0.0.1'"
            : $"export HTTP_PROXY='http://127.0.0.1:{port}' HTTPS_PROXY='http://127.0.0.1:{port}' ALL_PROXY='socks5://127.0.0.1:{port}' NO_PROXY='localhost,127.0.0.1'";
        await clipboard.SetTextAsync(command);
        _viewModel.ShowStatusMessage(OperatingSystem.IsWindows()
            ? "已复制 PowerShell 代理命令"
            : "已复制终端代理命令");
    }

    private async void RuleMode_Click(object? sender, RoutedEventArgs e) => await SetModeAsync(ProxyMode.Rule);
    private async void GlobalMode_Click(object? sender, RoutedEventArgs e) => await SetModeAsync(ProxyMode.Global);
    private async void DirectMode_Click(object? sender, RoutedEventArgs e) => await SetModeAsync(ProxyMode.Direct);

    private async Task SetModeAsync(ProxyMode mode)
    {
        await _viewModel!.SetModeAsync(mode);
        UpdateModeButtons();
    }

    private void UpdateModeButtons()
    {
        if (_viewModel is null) return;
        RuleModeButton.Classes.Set("selected", _viewModel.Mode == ProxyMode.Rule);
        GlobalModeButton.Classes.Set("selected", _viewModel.Mode == ProxyMode.Global);
        DirectModeButton.Classes.Set("selected", _viewModel.Mode == ProxyMode.Direct);
        UpdateModeSlider();
    }

    private void UpdateModeSlider()
    {
        if (_viewModel is null || ModeSliderHost.Bounds.Width <= 0) return;
        var segmentWidth = ModeSliderHost.Bounds.Width / 3d;
        var index = _viewModel.Mode switch
        {
            ProxyMode.Global => 1,
            ProxyMode.Direct => 2,
            _ => 0
        };
        ModeSliderThumb.Width = segmentWidth;
        if (ModeSliderThumb.RenderTransform is TranslateTransform transform)
            transform.X = segmentWidth * index;
    }

    private async void TunToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null) await _viewModel.SetTunEnabledAsync(TunToggle.IsChecked == true);
    }

    private async void Node_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: NodeItemViewModel node } && _viewModel is not null)
            await _viewModel.SelectNodeAsync(node);
    }

    private async void Configuration_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: SubscriptionConfiguration configuration } && _viewModel is not null)
            await _viewModel.SwitchConfigurationAsync(configuration);
    }

    private async void ManageSubscription_Click(object? sender, RoutedEventArgs e)
    {
        if (_context is null || _viewModel is null) return;
        var result = await _context.Windows.ShowDialogAsync<SubscriptionSettingsResult?>(
            new SubscriptionSettingsWindow(_viewModel.Configurations, _viewModel.ActiveConfiguration?.Id));
        if (result is not null)
            await _viewModel.ApplyConfigurationsAsync(result.Configurations, result.ActiveConfigurationId);
    }

    private async void UpdateSubscription_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        if (string.IsNullOrWhiteSpace(_viewModel.SubscriptionUrl))
        {
            ManageSubscription_Click(sender, e);
            return;
        }
        await _viewModel.UpdateSubscriptionAsync(_viewModel.SubscriptionUrl);
    }

    private void ViewLog_Click(object? sender, RoutedEventArgs e)
    {
        if (_context is null || _viewModel is null) return;
        Dispatcher.UIThread.Post(() => _context.Windows.Show(new LogWindow(_viewModel.LastLogText)), DispatcherPriority.Background);
    }
}
