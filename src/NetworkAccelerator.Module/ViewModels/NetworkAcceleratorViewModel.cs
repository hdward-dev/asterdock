using AsterDock.Contracts;
using Avalonia.Threading;
using NetworkAccelerator.Core.Models;
using NetworkAccelerator.Core.Services;
using NetworkAccelerator.Module.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace NetworkAccelerator.Module.ViewModels;

public sealed partial class NetworkAcceleratorViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SettingsStore _settingsStore;
    private readonly SubscriptionService _subscriptionService;
    private readonly SingBoxConfigurationService _configurationService;
    private readonly SingBoxEngineService _engine;
    private readonly SingBoxInstallerService _installer;
    public const string CoreExitedMessage = "加速核心已退出";
    public const string ServiceTestNeedsConnectionMessage = "请先开始加速后再测服务延迟";
    public const string ServiceProbeFailedMessage = "服务延迟测试失败";

    private readonly IUserFeedbackService? _feedback;
    private int _suppressCoreExitNotice;
    private NetworkAcceleratorSettings _settings = new();
    private SubscriptionProfile? _profile;
    private PeriodicTimer? _durationTimer;
    private DateTimeOffset _connectedAt;
    private bool _disposed;
    private bool _isBusy;
    private bool _isUpdatingSubscription;
    private int _connectionFaultSignal;
    private bool _isRefreshingLatencies;
    private string _nodeSearchText = string.Empty;
    private bool _isConnected;
    private bool _tunEnabled;
    private bool _systemProxyEnabled = true;
    private (bool SystemProxyEnabled, bool TunEnabled)? _pendingTunRevert;
    private ProxyMode _mode;
    private string _connectionStatusText = "未连接";
    private string _statusMessage = "请配置订阅后开始使用";
    private string _currentNodeText = "未选择节点";
    private string _currentLatencyText = "--";
    private string _connectionDurationText = "00:00:00";
    private string _subscriptionName = "未配置订阅";
    private string _subscriptionUpdatedText = "尚未更新";
    private string _remainingTrafficText = "-- / --";
    private string _expiresText = "--";
    private double _trafficPercent;
    private string _coreStatusText = "正在检测 sing-box…";
    private string _lastLogText = "暂无运行日志";
    private bool _hasNodes;
    private bool _isCoreAvailable;
    private string? _coreVersion;
    private SubscriptionConfiguration? _activeConfiguration;

    public NetworkAcceleratorViewModel(
        string dataDirectory,
        string moduleDirectory,
        ILoadingService? loading = null,
        IUserFeedbackService? feedback = null)
    {
        _feedback = feedback;
        _settingsStore = new SettingsStore(dataDirectory);
        _subscriptionService = new SubscriptionService(dataDirectory);
        _configurationService = new SingBoxConfigurationService(dataDirectory);
        _engine = new SingBoxEngineService(moduleDirectory, dataDirectory);
        _installer = new SingBoxInstallerService(dataDirectory);
        _engine.LogReceived += Engine_LogReceived;
        _engine.StateChanged += Engine_StateChanged;
        if (loading is not null)
            _engine.BeginAuthorization = () => loading.Begin("正在授予 TUN 权限…");
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<NodeItemViewModel> Nodes { get; } = [];
    public ObservableCollection<SubscriptionConfiguration> Configurations { get; } = [];
    public ObservableCollection<string> LogEntries { get; } = [];
    public bool HasNoLogs => LogEntries.Count == 0;

    public void ClearLogs()
    {
        LogEntries.Clear();
        OnPropertyChanged(nameof(HasNoLogs));
    }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value)) OnPropertyChanged(nameof(ShowOverviewAlert));
        }
    }

    public bool IsUpdatingSubscription
    {
        get => _isUpdatingSubscription;
        private set => SetField(ref _isUpdatingSubscription, value);
    }

    public int ConnectionFaultSignal
    {
        get => _connectionFaultSignal;
        private set => SetField(ref _connectionFaultSignal, value);
    }
    public bool IsRefreshingLatencies
    {
        get => _isRefreshingLatencies;
        private set
        {
            if (SetField(ref _isRefreshingLatencies, value))
            {
                OnPropertyChanged(nameof(RefreshLatencyButtonText));
                OnPropertyChanged(nameof(CanRefreshLatencies));
            }
        }
    }
    public string RefreshLatencyButtonText => IsRefreshingLatencies ? "测速中…" : "测速";
    public bool CanRefreshLatencies => HasNodes && !IsRefreshingLatencies;
    public bool HasVisibleNodes => HasNodes && Nodes.Any(node => node.IsVisible);
    public bool HasNoSearchResults => HasNodes && !HasVisibleNodes;
    public string NodeCountText => HasNodes
        ? $"{Nodes.Count(node => !node.IsAutomatic)} 条线路 · 支持自动选择"
        : "添加订阅，获取可用线路";
    public string NodeSearchText
    {
        get => _nodeSearchText;
        set
        {
            if (SetField(ref _nodeSearchText, value)) ApplyNodeFilter();
        }
    }
    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (!SetField(ref _isConnected, value)) return;
            OnPropertyChanged(nameof(ConnectionButtonText));
            OnPropertyChanged(nameof(OverviewRunText));
            OnPropertyChanged(nameof(OverviewDurationText));
        }
    }
    public bool TunEnabled
    {
        get => _tunEnabled;
        set
        {
            if (!SetField(ref _tunEnabled, value)) return;
            OnPropertyChanged(nameof(ConnectionMethodText));
            OnPropertyChanged(nameof(IsSystemProxy));
            OnPropertyChanged(nameof(SystemProxyDetailText));
            OnPropertyChanged(nameof(TunDetailText));
        }
    }

    public bool SystemProxyEnabled
    {
        get => _systemProxyEnabled;
        private set
        {
            if (!SetField(ref _systemProxyEnabled, value)) return;
            OnPropertyChanged(nameof(IsSystemProxy));
            OnPropertyChanged(nameof(SystemProxyDetailText));
            OnPropertyChanged(nameof(ConnectionMethodText));
        }
    }

    public string ConnectionMethodText => TunEnabled
        ? "TUN 虚拟网卡"
        : IsSystemProxy
            ? "系统代理"
            : "未接管系统流量";
    public ProxyMode Mode
    {
        get => _mode;
        private set
        {
            if (!SetField(ref _mode, value)) return;
            OnPropertyChanged(nameof(ModeDescription));
            OnPropertyChanged(nameof(RulesText));
            OnPropertyChanged(nameof(IsRuleMode));
            OnPropertyChanged(nameof(IsGlobalMode));
            OnPropertyChanged(nameof(IsDirectMode));
            OnPropertyChanged(nameof(ModeIndex));
            OnPropertyChanged(nameof(RouteRuleCountText));
        }
    }
    public string ModeDescription => Mode switch
    {
        ProxyMode.Global => "所有流量通过当前节点，本机与局域网流量除外。",
        ProxyMode.Direct => "流量直接连接，适合临时关闭代理或排查问题。",
        _ => "根据规则分流，局域网直连，其余流量通过代理。"
    };
    public string ConnectionStatusText { get => _connectionStatusText; private set => SetField(ref _connectionStatusText, value); }
    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetField(ref _statusMessage, value)) OnPropertyChanged(nameof(ShowOverviewAlert));
        }
    }
    public string CurrentNodeText { get => _currentNodeText; private set => SetField(ref _currentNodeText, value); }
    public string CurrentLatencyText { get => _currentLatencyText; private set => SetField(ref _currentLatencyText, value); }
    public string ConnectionDurationText
    {
        get => _connectionDurationText;
        private set
        {
            if (SetField(ref _connectionDurationText, value)) OnPropertyChanged(nameof(OverviewDurationText));
        }
    }
    public string SubscriptionName { get => _subscriptionName; private set => SetField(ref _subscriptionName, value); }
    public string SubscriptionUpdatedText { get => _subscriptionUpdatedText; private set => SetField(ref _subscriptionUpdatedText, value); }
    public string RemainingTrafficText { get => _remainingTrafficText; private set => SetField(ref _remainingTrafficText, value); }
    public string ExpiresText { get => _expiresText; private set => SetField(ref _expiresText, value); }
    public double TrafficPercent
    {
        get => _trafficPercent;
        private set
        {
            if (SetField(ref _trafficPercent, value)) OnPropertyChanged(nameof(RemainingTrafficPercent));
        }
    }
    public double RemainingTrafficPercent => 100 - TrafficPercent;
    public bool HasTrafficInformation => _profile?.TotalBytes is > 0;
    public string CoreStatusText { get => _coreStatusText; private set => SetField(ref _coreStatusText, value); }
    public string LastLogText { get => _lastLogText; private set => SetField(ref _lastLogText, value); }
    public bool HasNodes { get => _hasNodes; private set { if (SetField(ref _hasNodes, value)) OnPropertyChanged(nameof(HasNoNodes)); } }
    public bool HasNoNodes => !HasNodes;
    public bool IsCoreAvailable { get => _isCoreAvailable; private set { if (SetField(ref _isCoreAvailable, value)) OnPropertyChanged(nameof(IsCoreMissing)); } }
    public bool IsCoreMissing => !IsCoreAvailable;
    public bool TunRequiresAdministrator => _engine.RequiresAdministratorForTun;
    public string ConnectionButtonText => IsConnected ? "停止加速" : "开始加速";
    public SubscriptionConfiguration? ActiveConfiguration
    {
        get => _activeConfiguration;
        private set => SetField(ref _activeConfiguration, value);
    }
    public string SubscriptionUrl => ActiveConfiguration?.Source ?? string.Empty;
    public int ProxyPort => SingBoxConfigurationService.MixedProxyPort;
    public string RulesText => Mode == ProxyMode.Rule ? "规则集 2 个" : Mode == ProxyMode.Global ? "全局代理" : "全部直连";

    public void ShowStatusMessage(string message, FeedbackKind? toast = null)
    {
        StatusMessage = message;
        if (toast is { } kind) _feedback.Notify(kind, message);
    }

    private int _initializeStarted;

    public Task InitializeAsync()
    {
        // Two pages share this model. Only the first attach loads settings and touches the engine.
        if (System.Threading.Interlocked.Exchange(ref _initializeStarted, 1) != 0) return Task.CompletedTask;
        return InitializeCoreAsync();
    }

    private async Task InitializeCoreAsync()
    {
        try
        {
            _settings = await _settingsStore.LoadAsync().ConfigureAwait(false);
            NormalizeConfigurations();
            _profile = ActiveConfiguration is null
                ? null
                : await _subscriptionService.LoadCachedAsync(ActiveConfiguration.Id).ConfigureAwait(false);
            var coreVersion = await _engine.GetVersionAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                RefreshConfigurations();
                Mode = _settings.Mode;
                SystemProxyEnabled = _settings.SystemProxyEnabled;
                TunEnabled = _settings.TunEnabled;
                RefreshCoreStatus(coreVersion);
                AllowLan = _settings.AllowLan;
                ReduceMotion = _settings.ReduceMotion;
                ApplyProfile();
                RefreshLanAddress();
            });
            _ = RefreshExitIpAsync(throughProxy: false, force: false);
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Fail("初始化失败", exception));
        }
    }

    public async Task ToggleConnectionAsync()
    {
        if (IsBusy) return;
        var stopping = _engine.IsRunning;
        IsBusy = true;
        try
        {
            if (stopping)
            {
                await StopEngineAsync().ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(ApplyDisconnectedState);
                return;
            }
            await StartCoreAsync().ConfigureAwait(false);
        }
        catch (TunAuthorizationException exception)
        {
            await RevertAfterTunAuthorizationAsync(exception, restoreConnection: false).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                Fail(stopping ? "停止失败" : "连接失败", exception, fault: !stopping));
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }
    }

    public async Task InstallCoreAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = $"正在下载 sing-box {SingBoxInstallerService.StableVersion}…";
        try
        {
            await _installer.InstallAsync().ConfigureAwait(false);
            var coreVersion = await _engine.GetVersionAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                RefreshCoreStatus(coreVersion);
                Succeed($"sing-box {SingBoxInstallerService.StableVersion} 安装成功");
            });
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Fail("核心安装失败", exception));
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }
    }

    public async Task UpdateSubscriptionAsync(string subscriptionUrl)
    {
        if (IsBusy) return;
        IsBusy = true;
        IsUpdatingSubscription = true;
        StatusMessage = "正在更新订阅…";
        try
        {
            var configuration = EnsureActiveConfiguration();
            configuration.Source = subscriptionUrl.Trim();
            _settings.SubscriptionUrl = configuration.Source;
            await _settingsStore.SaveAsync(_settings).ConfigureAwait(false);
            _profile = await _subscriptionService.UpdateAsync(configuration.Source, configuration.Id).ConfigureAwait(false);
            configuration.CachedSource = configuration.Source;
            await _settingsStore.SaveAsync(_settings).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyProfile();
                Succeed($"订阅更新成功，共 {_profile.Nodes.Count} 个节点");
            });
            _ = RefreshLatenciesAsync();
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Fail("订阅更新失败", exception));
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsUpdatingSubscription = false;
                IsBusy = false;
            });
        }
    }

    public async Task SetModeAsync(ProxyMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        _settings.Mode = mode;
        OnPropertyChanged(nameof(RulesText));
        try
        {
            await _settingsStore.SaveAsync(_settings).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Fail("保存设置失败", exception));
            return;
        }

        if (IsConnected) await RestartAsync().ConfigureAwait(false);
    }

    public Task SetSystemProxyEnabledAsync(bool enabled)
    {
        var next = TakeoverMode.ApplySystemProxy(_settings.SystemProxyEnabled, _settings.TunEnabled, enabled);
        return CommitTakeoverAsync(next.SystemProxyEnabled, next.TunEnabled);
    }

    public Task SetTunEnabledAsync(bool enabled)
    {
        var next = TakeoverMode.ApplyTun(_settings.SystemProxyEnabled, _settings.TunEnabled, enabled);
        return CommitTakeoverAsync(next.SystemProxyEnabled, next.TunEnabled);
    }

    private async Task CommitTakeoverAsync(bool systemProxyEnabled, bool tunEnabled)
    {
        if (systemProxyEnabled == _settings.SystemProxyEnabled && tunEnabled == _settings.TunEnabled)
        {
            await ApplyTakeoverFlagsAsync(systemProxyEnabled, tunEnabled).ConfigureAwait(false);
            return;
        }

        var previous = (_settings.SystemProxyEnabled, _settings.TunEnabled);
        _pendingTunRevert = tunEnabled && !previous.Item2 ? previous : null;
        await ApplyTakeoverFlagsAsync(systemProxyEnabled, tunEnabled).ConfigureAwait(false);
        if (tunEnabled && _engine.RequiresAdministratorForTun)
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = "TUN 将在开始加速时请求管理员权限");

        var wasConnected = IsConnected || _engine.IsRunning;
        if (!wasConnected)
        {
            try
            {
                await _settingsStore.SaveAsync(_settings).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await Dispatcher.UIThread.InvokeAsync(() => Fail("保存设置失败", exception));
            }

            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() => IsBusy = true);
        try
        {
            await StopEngineAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(ApplyDisconnectedState);
            await StartCoreAsync().ConfigureAwait(false);
        }
        catch (TunAuthorizationException exception)
        {
            await RevertAfterTunAuthorizationAsync(exception, restoreConnection: true).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyDisconnectedState();
                Fail("连接失败", exception, fault: true);
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }
    }

    private async Task StartCoreAsync()
    {
        if (_profile is null || _profile.Nodes.Count == 0) throw new InvalidOperationException("请先配置并更新订阅");
        _settings.Mode = Mode;
        _settings.TunEnabled = TunEnabled;
        _settings.SystemProxyEnabled = SystemProxyEnabled;
        var configPath = await _configurationService.WriteAsync(_profile, _settings).ConfigureAwait(false);
        await _settingsStore.SaveAsync(_settings).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => StatusMessage =
            TunEnabled && _engine.RequiresAdministratorForTun
                ? "请在系统提示中允许管理员权限…"
                : "正在启动网络加速…");
        await _engine.StartAsync(configPath, requireAdministrator: TunEnabled).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _pendingTunRevert = null;
            _connectedAt = DateTimeOffset.Now;
            IsConnected = true;
            ConnectionStatusText = "已连接";
            StatusMessage = TunEnabled
                ? "TUN 已接管系统网络流量"
                : IsSystemProxy
                    ? "系统代理已启用"
                    : "加速已启动，未接管系统流量";
            StartDurationTimer();
            StartRealtime();
        });
        _ = RefreshLatenciesAsync();
        _ = RefreshExitIpAsync(throughProxy: true, force: true);
        _ = RefreshServiceStatusAsync();
    }

    private async Task RevertAfterTunAuthorizationAsync(TunAuthorizationException exception, bool restoreConnection)
    {
        if (_pendingTunRevert is { } previous)
        {
            await ApplyTakeoverFlagsAsync(previous.SystemProxyEnabled, previous.TunEnabled).ConfigureAwait(false);
            _pendingTunRevert = null;
        }
        else if (TunEnabled || _settings.TunEnabled)
        {
            await ApplyTakeoverFlagsAsync(_settings.SystemProxyEnabled, false).ConfigureAwait(false);
        }

        await Dispatcher.UIThread.InvokeAsync(() => PresentAuthorization(_feedback, exception));
        await Dispatcher.UIThread.InvokeAsync(ApplyDisconnectedState);
        if (restoreConnection && !TunEnabled && _profile is { Nodes.Count: > 0 })
        {
            try
            {
                await StartCoreAsync().ConfigureAwait(false);
            }
            catch (Exception restoreFailure)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ApplyDisconnectedState();
                    Fail("连接失败", restoreFailure, fault: true);
                });
                return;
            }
        }
        else
        {
            await _settingsStore.SaveAsync(_settings).ConfigureAwait(false);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            StatusMessage = exception.Message;
            ConnectionFaultSignal++;
        });
    }

    public static void PresentAuthorization(IUserFeedbackService? feedback, TunAuthorizationException exception)
    {
        if (feedback is null) return;
        if (exception.Cancelled)
        {
            feedback.Notify(FeedbackKind.Warning, TunAuthorizationException.CancelledMessage);
            return;
        }

        if (exception.SetuidWrapperMissing)
        {
            _ = feedback.ShowErrorAsync("无法授予 TUN 权限", TunAuthorizationException.SetuidWrapperMessage, exception);
            return;
        }

        feedback.Report("授予 TUN 权限失败", exception);
    }

    private async Task ApplyTakeoverFlagsAsync(bool systemProxyEnabled, bool tunEnabled)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _settings.SystemProxyEnabled = systemProxyEnabled;
            _settings.TunEnabled = tunEnabled;
            SystemProxyEnabled = systemProxyEnabled;
            TunEnabled = tunEnabled;
        });
    }

    public async Task SelectNodeAsync(NodeItemViewModel item)
    {
        if (item.IsSelected) return;
        try
        {
            if (IsConnected)
            {
                using var api = new ClashApiClient(_settings.ApiSecret);
                await api.SelectNodeAsync(item.Tag).ConfigureAwait(false);
            }
            _settings.SelectedNode = item.Tag;
            if (ActiveConfiguration is not null) ActiveConfiguration.SelectedNode = item.Tag;
            await _settingsStore.SaveAsync(_settings).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => SelectNode(item.Tag));
            if (IsConnected)
            {
                _ = RefreshExitIpAsync(throughProxy: true, force: true);
                _ = RefreshServiceStatusAsync();
            }
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Fail("节点切换失败", exception));
        }
    }

    public async Task SwitchConfigurationAsync(SubscriptionConfiguration configuration)
    {
        if (IsBusy || ActiveConfiguration?.Id == configuration.Id) return;
        await ChangeConfigurationAsync(configuration.Id, null).ConfigureAwait(false);
    }

    public async Task ApplyConfigurationsAsync(
        IReadOnlyList<SubscriptionConfiguration> configurations,
        string activeConfigurationId)
    {
        if (IsBusy || configurations.Count == 0) return;

        var previous = _settings.SubscriptionConfigurations
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        _settings.SubscriptionConfigurations = configurations.Select(item =>
        {
            var clone = item.Clone();
            if (previous.TryGetValue(clone.Id, out var old) &&
                string.Equals(old.Source, clone.Source, StringComparison.Ordinal))
            {
                clone.CachedSource = old.CachedSource;
                clone.SelectedNode = old.SelectedNode;
            }
            else
            {
                clone.CachedSource = string.Empty;
            }
            return clone;
        }).ToList();
        await ChangeConfigurationAsync(activeConfigurationId, "配置已保存").ConfigureAwait(false);
    }

    public async Task RefreshLatenciesAsync()
    {
        if (IsRefreshingLatencies) return;

        var items = Nodes.ToArray();
        if (items.Length == 0) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsRefreshingLatencies = true;
            foreach (var item in items) item.IsMeasuringLatency = true;
        });

        try
        {
            await Task.WhenAll(items.Select(MeasureLatencyAsync)).ConfigureAwait(false);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var item in items) item.IsMeasuringLatency = false;
                IsRefreshingLatencies = false;
                UpdateCurrentLatency();
                var measurable = items.Where(item => !item.IsAutomatic).ToArray();
                if (measurable.Length > 0 && measurable.All(item => item.Latency is null))
                    _feedback.Notify(FeedbackKind.Warning, "节点延迟测试失败");
            });
        }
    }

    private async Task MeasureLatencyAsync(NodeItemViewModel item)
    {
        int? latency = null;
        try
        {
            if (IsConnected)
            {
                using var api = new ClashApiClient(_settings.ApiSecret);
                latency = await api.MeasureDelayAsync(item.Tag).ConfigureAwait(false);
            }
            else if (item.Node is { Server.Length: > 0, ServerPort: > 0 } node)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var tcp = new TcpClient();
                var stopwatch = Stopwatch.StartNew();
                await tcp.ConnectAsync(node.Server, node.ServerPort, timeout.Token).ConfigureAwait(false);
                stopwatch.Stop();
                latency = Math.Max(1, (int)stopwatch.ElapsedMilliseconds);
            }
        }
        catch
        {
            latency = null;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            item.Latency = latency;
            item.IsMeasuringLatency = false;
            if (item.IsSelected) UpdateCurrentLatency();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopRealtime();
        _dash.ReleaseMotion();
        _durationTimer?.Dispose();
        _durationTimer = null;
        _engine.LogReceived -= Engine_LogReceived;
        _engine.StateChanged -= Engine_StateChanged;
        _engine.Dispose();
        _installer.Dispose();
        _subscriptionService.Dispose();
    }

    private async Task RestartAsync()
    {
        await StopEngineAsync().ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(ApplyDisconnectedState);
        await ToggleConnectionAsync().ConfigureAwait(false);
    }

    private async Task ChangeConfigurationAsync(string configurationId, string? successPrefix)
    {
        var reconnect = _engine.IsRunning;
        await Dispatcher.UIThread.InvokeAsync(() => IsBusy = true);
        try
        {
            if (reconnect)
            {
                await StopEngineAsync().ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(ApplyDisconnectedState);
            }

            var selected = _settings.SubscriptionConfigurations.FirstOrDefault(item => item.Id == configurationId)
                           ?? _settings.SubscriptionConfigurations.First();
            _settings.ActiveSubscriptionId = selected.Id;
            _settings.SubscriptionUrl = selected.Source;
            _settings.SelectedNode = selected.SelectedNode;
            ActiveConfiguration = selected;

            _profile = string.Equals(selected.Source, selected.CachedSource, StringComparison.Ordinal)
                ? await _subscriptionService.LoadCachedAsync(selected.Id).ConfigureAwait(false)
                : null;
            if (_profile is null)
            {
                _profile = await _subscriptionService.UpdateAsync(selected.Source, selected.Id).ConfigureAwait(false);
                selected.CachedSource = selected.Source;
            }
            await _settingsStore.SaveAsync(_settings).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                RefreshConfigurations();
                ApplyProfile();
                Succeed($"{successPrefix ?? "已切换到配置"}：{selected.Name}");
            });
        }
        catch (Exception exception)
        {
            reconnect = false;
            await Dispatcher.UIThread.InvokeAsync(() => Fail("配置切换失败", exception));
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }

        if (reconnect && _profile is { Nodes.Count: > 0 }) await ToggleConnectionAsync().ConfigureAwait(false);
    }

    private void ApplyProfile()
    {
        Nodes.Clear();
        if (_profile is null)
        {
            HasNodes = false;
            SubscriptionName = "未配置订阅";
            SubscriptionUpdatedText = "尚未更新";
            RemainingTrafficText = "订阅未提供流量信息";
            TrafficPercent = 0;
            ExpiresText = "--";
            SelectNode(string.Empty);
            RefreshNodePresentation();
            OnPropertyChanged(nameof(HasTrafficInformation));
            StatusMessage = "请配置订阅后开始使用";
            NotifyConfigCard();
            return;
        }

        Nodes.Add(new NodeItemViewModel(null, automatic: true));
        foreach (var node in _profile.Nodes) Nodes.Add(new NodeItemViewModel(node));
        ApplyNodeFilter();
        HasNodes = _profile.Nodes.Count > 0;
        RefreshNodePresentation();
        OnPropertyChanged(nameof(HasTrafficInformation));
        SubscriptionName = _profile.Name;
        SubscriptionUpdatedText = $"{_profile.UpdatedAt:MM-dd HH:mm} 更新";
        ExpiresText = _profile.ExpiresAt?.ToString("yyyy-MM-dd") ?? "未提供";
        if (_profile.TotalBytes is > 0)
        {
            var used = Math.Max(0, _profile.UsedBytes ?? 0);
            RemainingTrafficText = $"{FormatBytes(Math.Max(0, _profile.TotalBytes.Value - used))} / {FormatBytes(_profile.TotalBytes.Value)}";
            TrafficPercent = Math.Clamp(used * 100d / _profile.TotalBytes.Value, 0, 100);
        }
        else
        {
            RemainingTrafficText = "订阅未提供流量信息";
            TrafficPercent = 0;
        }
        var selected = Nodes.Any(node => node.Tag == _settings.SelectedNode)
            ? _settings.SelectedNode
            : Nodes.FirstOrDefault()?.Tag ?? string.Empty;
        SelectNode(selected);
        NotifyConfigCard();
    }

    private void NormalizeConfigurations()
    {
        _settings.SubscriptionConfigurations ??= [];
        if (_settings.SubscriptionConfigurations.Count == 0 && !string.IsNullOrWhiteSpace(_settings.SubscriptionUrl))
        {
            _settings.SubscriptionConfigurations.Add(new SubscriptionConfiguration
            {
                Id = "default",
                Name = "我的订阅",
                Source = _settings.SubscriptionUrl,
                CachedSource = _settings.SubscriptionUrl,
                SelectedNode = _settings.SelectedNode
            });
        }

        ActiveConfiguration = _settings.SubscriptionConfigurations
                                  .FirstOrDefault(item => item.Id == _settings.ActiveSubscriptionId)
                              ?? _settings.SubscriptionConfigurations.FirstOrDefault();
        if (ActiveConfiguration is null) return;
        _settings.ActiveSubscriptionId = ActiveConfiguration.Id;
        _settings.SubscriptionUrl = ActiveConfiguration.Source;
        _settings.SelectedNode = ActiveConfiguration.SelectedNode;
    }

    private SubscriptionConfiguration EnsureActiveConfiguration()
    {
        if (ActiveConfiguration is not null) return ActiveConfiguration;
        var configuration = new SubscriptionConfiguration { Name = "我的订阅" };
        _settings.SubscriptionConfigurations.Add(configuration);
        _settings.ActiveSubscriptionId = configuration.Id;
        ActiveConfiguration = configuration;
        Dispatcher.UIThread.Post(RefreshConfigurations);
        return configuration;
    }

    private void RefreshConfigurations()
    {
        Configurations.Clear();
        foreach (var configuration in _settings.SubscriptionConfigurations) Configurations.Add(configuration);
        ActiveConfiguration = Configurations.FirstOrDefault(item => item.Id == _settings.ActiveSubscriptionId)
                              ?? Configurations.FirstOrDefault();
        OnPropertyChanged(nameof(ActiveConfiguration));
        OnPropertyChanged(nameof(SubscriptionUrl));
    }

    private void SelectNode(string tag)
    {
        foreach (var node in Nodes) node.IsSelected = node.Tag == tag;
        var selected = Nodes.FirstOrDefault(node => node.IsSelected);
        CurrentNodeText = selected?.Name ?? "未选择节点";
        UpdateCurrentLatency();
    }

    private void UpdateCurrentLatency()
    {
        var selected = Nodes.FirstOrDefault(node => node.IsSelected);
        CurrentLatencyText = selected?.LatencyText ?? "--";
    }

    private void ApplyNodeFilter()
    {
        var query = NodeSearchText.Trim();
        foreach (var node in Nodes)
        {
            node.IsVisible = query.Length == 0 ||
                             node.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                             node.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase);
        }
        RefreshNodePresentation();
    }

    private void RefreshNodePresentation()
    {
        OnPropertyChanged(nameof(HasVisibleNodes));
        OnPropertyChanged(nameof(HasNoSearchResults));
        OnPropertyChanged(nameof(NodeCountText));
        OnPropertyChanged(nameof(CanRefreshLatencies));
    }

    private void StartDurationTimer()
    {
        _durationTimer?.Dispose();
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        _durationTimer = timer;
        _ = UpdateDurationAsync(timer);
    }

    private async Task UpdateDurationAsync(PeriodicTimer timer)
    {
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            var duration = DateTimeOffset.Now - _connectedAt;
            await Dispatcher.UIThread.InvokeAsync(() => ConnectionDurationText =
                $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}");
        }
    }

    private void Fail(string title, Exception exception, bool fault = false)
    {
        StatusMessage = $"{title}：{exception.GetBaseException().Message}";
        if (fault) ConnectionFaultSignal++;
        _feedback.Report(title, exception);
    }

    public void Run(Func<Task> action, string? errorTitle = null)
    {
        if (_feedback is not null)
        {
            _feedback.Run(action, errorTitle);
            return;
        }

        _ = Observe(action, errorTitle);
    }

    private async Task Observe(Func<Task> action, string? errorTitle)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Fail(string.IsNullOrWhiteSpace(errorTitle) ? "操作未完成" : errorTitle, exception));
        }
    }

    private void Succeed(string message)
    {
        StatusMessage = message;
        _feedback.Notify(FeedbackKind.Success, message);
    }

    private void ApplyDisconnectedState()
    {
        _durationTimer?.Dispose();
        _durationTimer = null;
        StopRealtime();
        IsConnected = false;
        ConnectionStatusText = "未连接";
        ConnectionDurationText = "00:00:00";
        _dash.ResetLive();
        if (!StatusMessage.StartsWith("连接失败", StringComparison.Ordinal)) StatusMessage = "网络加速已停止";
        _ = RefreshExitIpAsync(throughProxy: false, force: false);
    }

    private void RefreshCoreStatus(string? version = null)
    {
        IsCoreAvailable = _engine.IsCoreAvailable;
        if (!string.IsNullOrWhiteSpace(version)) _coreVersion = version;
        CoreStatusText = IsCoreAvailable
            ? $"sing-box · {_coreVersion ?? "版本未知"}"
            : "未找到 sing-box 核心";
    }

    private void Engine_LogReceived(object? sender, string message) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            LastLogText = SanitizeLog(message);
            LogEntries.Insert(0, LastLogText);
            while (LogEntries.Count > 300) LogEntries.RemoveAt(LogEntries.Count - 1);
            OnPropertyChanged(nameof(HasNoLogs));
        });

    private async Task StopEngineAsync()
    {
        Interlocked.Increment(ref _suppressCoreExitNotice);
        try
        {
            await _engine.StopAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_disposed && IsConnected) ApplyDisconnectedState();
            });
        }
        finally
        {
            // The process exit is posted at normal priority. Clearing afterwards,
            // and only after the connected flag is already false, keeps that exit
            // from looking like the core quit on its own.
            Dispatcher.UIThread.Post(
                () => Interlocked.Decrement(ref _suppressCoreExitNotice),
                DispatcherPriority.Background);
        }
    }

    public Task PreviewIntentionalStopAsync() => StopEngineAsync();

    public void PreviewEngineExit() => Engine_StateChanged(_engine, EventArgs.Empty);

    private void Engine_StateChanged(object? sender, EventArgs e)
    {
        if (_engine.IsRunning) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            var unexpected = IsConnected && Volatile.Read(ref _suppressCoreExitNotice) == 0;
            ApplyDisconnectedState();
            if (!unexpected) return;
            StatusMessage = CoreExitedMessage;
            ConnectionFaultSignal++;
            _feedback.Notify(FeedbackKind.Error, CoreExitedMessage);
        });
    }

    private static string SanitizeLog(string message)
    {
        var clean = Regex.Replace(message, "\\x1B(?:[@-_]|\\[[0-?]*[ -/]*[@-~])", string.Empty);
        return clean.Length > 180 ? clean[..180] + "…" : clean;
    }

    private static string FormatBytes(long bytes)
    {
        var gibibytes = bytes / 1024d / 1024d / 1024d;
        return $"{gibibytes:0.0} GB";
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
