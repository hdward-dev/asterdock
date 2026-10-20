using AsterDock.Contracts;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using NetworkAccelerator.Core.Models;
using NetworkAccelerator.Module.Controls;
using NetworkAccelerator.Module.Models;
using NetworkAccelerator.Module.ViewModels;

namespace NetworkAccelerator.Module.Views;

public partial class NetworkAcceleratorView : UserControl
{
    private const double NavStride = 76;

    private readonly IApplicationContext? _context;
    private readonly NetworkAcceleratorViewModel? _viewModel;
    private readonly Dictionary<string, Control> _pages = new();
    private bool _initialized;
    private bool _motionHooked;
    private bool _clocked;
    private string _selectedSection = "";
    private string? _shown;
    private Control? _incoming;
    private Control? _outgoing;
    private double _swapMs = double.PositiveInfinity;
    private double _pillMs = double.PositiveInfinity;
    private double _pillFrom;
    private double _pillTo;
    private readonly TranslateTransform _pillTransform = new();

    public NetworkAcceleratorView()
    {
        InitializeComponent();
        _pages["Overview"] = OverviewPage;
        _pages["Proxies"] = ProxiesPage;
        _pages["Configuration"] = SubscriptionCard;
        _pages["Connections"] = ConnectionsWrap;
        _pages["Rules"] = RulesPage;
        _pages["Logs"] = LogsPage;
        _pages["Settings"] = SettingsPage;
        foreach (var page in _pages.Values)
            page.RenderTransform = new TranslateTransform();
        NavPill.RenderTransform = _pillTransform;
        Classes.Set("still", DashboardMotion.ReduceMotion);
        OverviewPage.ModeRequested += mode => _ = SetModeAsync(mode);
        OverviewPage.SubscriptionUpdateRequested += (_, _) => UpdateSubscription_Click(this, new RoutedEventArgs());
        OverviewPage.SectionRequested += SelectSection;
        ModeSliderHost.SizeChanged += (_, _) => UpdateModeSlider();
        SizeChanged += (_, _) => UpdateLayoutForWidth();
        SelectSection("Overview");
    }

    public void PreviewNavigate(string section) => SelectSection(section);

    public void PreviewNavPress(int index, bool down)
    {
        var buttons = SectionNavigation.Children.OfType<Button>().ToList();
        if ((uint)index >= (uint)buttons.Count) return;
        buttons[index].Classes.Set("down", down);
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_motionHooked) return;
        _motionHooked = true;
        DashboardMotion.Changed += OnMotionChanged;
        Classes.Set("still", DashboardMotion.ReduceMotion);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (_motionHooked)
        {
            _motionHooked = false;
            DashboardMotion.Changed -= OnMotionChanged;
        }

        ReleaseClock();
        base.OnDetachedFromVisualTree(e);
    }

    private void Navigate_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string section }) SelectSection(section);
    }

    private void SelectSection(string section)
    {
        var (title, description) = section switch
        {
            "Overview" => ("概览", "出口、流量、连接与策略，一目了然"),
            "Proxies" => ("代理", "选择线路、测试延迟，调整代理方式"),
            "Configuration" => ("配置", "管理订阅来源、切换配置与更新线路"),
            "Connections" => ("连接", "查看当前连接状态与本地代理入口"),
            "Rules" => ("规则", "了解当前路由策略与内置分流规则"),
            "Logs" => ("日志", "查看本次会话的核心运行记录"),
            "Settings" => ("设置", "调整网络接入方式，管理加速核心"),
            _ => (string.Empty, string.Empty)
        };
        if (title.Length == 0) return;
        if (section == _selectedSection && _shown == section && _outgoing is null) return;

        _selectedSection = section;
        SectionTitle.Text = title;
        SectionDescription.Text = description;
        PageStatus.IsVisible = section != "Overview" && section != "Connections";
        PlaceSharedCard(section);
        UpdateLayoutForWidth();
        foreach (var button in SectionNavigation.Children.OfType<Button>())
            button.Classes.Set("selected", Equals(button.Tag, section));
        AnimatePill(section);

        if (!_pages.TryGetValue(section, out var target)) return;
        Control? from = null;
        var animate = !DashboardMotion.ReduceMotion
            && IsLoaded
            && _shown is not null
            && _shown != section
            && _pages.TryGetValue(_shown, out from);
        if (!animate || from is null)
        {
            if (_outgoing is not null) ResetPage(_outgoing, false);
            ShowOnly(target);
            _shown = section;
            return;
        }

        if (_outgoing is not null && _outgoing != from && _outgoing != target)
            ResetPage(_outgoing, false);

        _outgoing = from;
        _incoming = target;
        _shown = section;
        _swapMs = 0;
        from.Opacity = 1;
        SetShift(from, 0);
        from.IsHitTestVisible = false;
        target.Opacity = 0;
        SetShift(target, DashboardMotion.PageSwapShift);
        target.IsHitTestVisible = false;
        PageHost.MinHeight = Math.Max(from.Bounds.Height, PageHost.Bounds.Height);
        target.IsVisible = true;
        EnsureClock();
    }

    private void PlaceSharedCard(string section)
    {
        if (section == "Proxies") MoveCard(ProxiesSide, insertAtStart: false);
        else if (section == "Settings") MoveCard(SettingsPage, insertAtStart: true);
    }

    private void MoveCard(Panel destination, bool insertAtStart)
    {
        if (ReferenceEquals(ProxySettingsCard.Parent, destination)) return;
        if (ProxySettingsCard.Parent is Panel panel)
            panel.Children.Remove(ProxySettingsCard);
        if (insertAtStart) destination.Children.Insert(0, ProxySettingsCard);
        else destination.Children.Add(ProxySettingsCard);
    }

    private void ShowOnly(Control target)
    {
        foreach (var page in _pages.Values)
            ResetPage(page, ReferenceEquals(page, target));
        _incoming = null;
        _outgoing = null;
        _swapMs = SwapDuration;
        PageHost.MinHeight = 0;
        PageScroll.Offset = default;
    }

    private static void ResetPage(Control page, bool visible)
    {
        page.IsVisible = visible;
        page.Opacity = 1;
        page.IsHitTestVisible = true;
        SetShift(page, 0);
    }

    private static void SetShift(Control page, double y)
    {
        if (page.RenderTransform is TranslateTransform transform)
            transform.Y = y;
    }

    private void AnimatePill(string section)
    {
        _pillFrom = _pillTransform.Y;
        _pillTo = SectionIndex(section) * NavStride;
        if (DashboardMotion.ReduceMotion || !IsLoaded)
        {
            _pillTransform.Y = _pillTo;
            _pillMs = PillDuration;
            return;
        }

        _pillMs = 0;
        EnsureClock();
    }

    private void OnMotionTick(TimeSpan elapsed)
    {
        var ms = elapsed.TotalMilliseconds;
        if (_pillMs < PillDuration)
        {
            _pillMs += ms;
            var t = EaseOutCubic(Math.Clamp(_pillMs / PillDuration, 0, 1));
            _pillTransform.Y = _pillFrom + (_pillTo - _pillFrom) * t;
        }

        if (_outgoing is not null && _incoming is not null)
        {
            _swapMs += ms;
            var t = EaseOutCubic(Math.Clamp(_swapMs / SwapDuration, 0, 1));
            _incoming.Opacity = t;
            _outgoing.Opacity = 1 - t;
            SetShift(_incoming, DashboardMotion.PageSwapShift * (1 - t));
            SetShift(_outgoing, -DashboardMotion.PageSwapShift * t);
            if (t >= 1)
            {
                var done = _incoming;
                ResetPage(_outgoing, false);
                ResetPage(done, true);
                _outgoing = null;
                _incoming = null;
                PageHost.MinHeight = 0;
                PageScroll.Offset = default;
            }
        }

        if (_pillMs >= PillDuration && _outgoing is null)
            ReleaseClock();
    }

    private void OnMotionChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnMotionChanged);
            return;
        }

        Classes.Set("still", DashboardMotion.ReduceMotion);
        if (!DashboardMotion.ReduceMotion) return;
        _pillTransform.Y = _pillTo;
        _pillMs = PillDuration;
        if (_incoming is not null) ShowOnly(_incoming);
        else if (_shown is not null && _pages.TryGetValue(_shown, out var page)) ShowOnly(page);
        ReleaseClock();
    }

    private void EnsureClock()
    {
        if (_clocked) return;
        _clocked = true;
        MotionClock.Subscribe(OnMotionTick);
    }

    private void ReleaseClock()
    {
        if (!_clocked) return;
        _clocked = false;
        MotionClock.Unsubscribe(OnMotionTick);
    }

    private static double PillDuration => DashboardMotion.NavSlide.TotalMilliseconds;

    private static double SwapDuration => DashboardMotion.PageSwap.TotalMilliseconds;

    private static double EaseOutCubic(double t)
    {
        var inverse = 1 - t;
        return 1 - inverse * inverse * inverse;
    }

    private static int SectionIndex(string section) => section switch
    {
        "Proxies" => 1,
        "Configuration" => 2,
        "Connections" => 3,
        "Rules" => 4,
        "Logs" => 5,
        "Settings" => 6,
        _ => 0
    };

    private void ClearLogs_Click(object? sender, RoutedEventArgs e) => _viewModel?.ClearLogs();

    private void UpdateLayoutForWidth()
    {
        var contentWidth = Math.Max(0, Bounds.Width - 88);
        var stackedProxies = _selectedSection == "Proxies" && contentWidth < 900;
        var splitProxies = _selectedSection == "Proxies" && !stackedProxies;
        ProxiesPage.ColumnDefinitions = new ColumnDefinitions(splitProxies ? "*,20,300" : "*");
        Grid.SetColumn(ProxiesSide, splitProxies ? 2 : 0);
        Grid.SetRow(ProxiesSide, stackedProxies ? 1 : 0);
        ProxiesSide.Margin = new Thickness(0, stackedProxies ? 20 : 0, 0, 0);

        var compact = contentWidth < 620;
        ConnectionHeader.ColumnDefinitions = new ColumnDefinitions(compact ? "*" : "*,Auto");
        Grid.SetColumn(ConnectionAction, compact ? 0 : 1);
        Grid.SetRow(ConnectionAction, compact ? 1 : 0);
        ConnectionAction.Margin = new Avalonia.Thickness(0, compact ? 16 : 0, 0, 0);
        ConnectionMetrics.ColumnDefinitions = new ColumnDefinitions(compact ? "*,*" : "2*,*,*,*");
        Grid.SetColumn(DurationMetric, compact ? 0 : 2);
        Grid.SetColumn(PortMetric, compact ? 1 : 3);
        Grid.SetRow(DurationMetric, compact ? 1 : 0);
        Grid.SetRow(PortMetric, compact ? 1 : 0);
        DurationMetric.Margin = PortMetric.Margin = new Avalonia.Thickness(0, compact ? 16 : 0, 0, 0);
    }

    private void ClearSearch_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.NodeSearchText = string.Empty;
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

    private async void ReduceMotion_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null) await _viewModel.SetReduceMotionAsync(ReduceMotionSwitch.IsChecked);
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

}
