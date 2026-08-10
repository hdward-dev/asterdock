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

public sealed class NetworkAcceleratorViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SettingsStore _settingsStore;
    private readonly SubscriptionService _subscriptionService;
    private readonly SingBoxConfigurationService _configurationService;
    private readonly SingBoxEngineService _engine;
    private readonly SingBoxInstallerService _installer;
    private NetworkAcceleratorSettings _settings = new();
    private SubscriptionProfile? _profile;
    private PeriodicTimer? _durationTimer;
    private DateTimeOffset _connectedAt;
    private bool _disposed;
    private bool _isBusy;
    private bool _isRefreshingLatencies;
    private string _nodeSearchText = string.Empty;
    private bool _isConnected;
    private bool _tunEnabled;
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

    public NetworkAcceleratorViewModel(string dataDirectory, string moduleDirectory)
    {
        _settingsStore = new SettingsStore(dataDirectory);
        _subscriptionService = new SubscriptionService(dataDirectory);
        _configurationService = new SingBoxConfigurationService(dataDirectory);
        _engine = new SingBoxEngineService(moduleDirectory, dataDirectory);
        _installer = new SingBoxInstallerService(dataDirectory);
        _engine.LogReceived += Engine_LogReceived;
        _engine.StateChanged += Engine_StateChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<NodeItemViewModel> Nodes { get; } = [];
    public ObservableCollection<SubscriptionConfiguration> Configurations { get; } = [];
    public bool IsBusy { get => _isBusy; private set => SetField(ref _isBusy, value); }
    public bool IsRefreshingLatencies
    {
        get => _isRefreshingLatencies;
        private set
        {
            if (SetField(ref _isRefreshingLatencies, value))
                OnPropertyChanged(nameof(RefreshLatencyButtonText));
        }
    }
    public string RefreshLatencyButtonText => IsRefreshingLatencies ? "测速中…" : "测速";
    public string NodeSearchText
    {
        get => _nodeSearchText;
        set
        {
            if (SetField(ref _nodeSearchText, value)) ApplyNodeFilter();
        }
    }
    public bool IsConnected { get => _isConnected; private set { if (SetField(ref _isConnected, value)) OnPropertyChanged(nameof(ConnectionButtonText)); } }
    public bool TunEnabled { get => _tunEnabled; set => SetField(ref _tunEnabled, value); }
    public ProxyMode Mode { get => _mode; private set => SetField(ref _mode, value); }
    public string ConnectionStatusText { get => _connectionStatusText; private set => SetField(ref _connectionStatusText, value); }
    public string StatusMessage { get => _statusMessage; private set => SetField(ref _statusMessage, value); }
    public string CurrentNodeText { get => _currentNodeText; private set => SetField(ref _currentNodeText, value); }
    public string CurrentLatencyText { get => _currentLatencyText; private set => SetField(ref _currentLatencyText, value); }
    public string ConnectionDurationText { get => _connectionDurationText; private set => SetField(ref _connectionDurationText, value); }
    public string SubscriptionName { get => _subscriptionName; private set => SetField(ref _subscriptionName, value); }
    public string SubscriptionUpdatedText { get => _subscriptionUpdatedText; private set => SetField(ref _subscriptionUpdatedText, value); }
    public string RemainingTrafficText { get => _remainingTrafficText; private set => SetField(ref _remainingTrafficText, value); }
    public string ExpiresText { get => _expiresText; private set => SetField(ref _expiresText, value); }
    public double TrafficPercent { get => _trafficPercent; private set => SetField(ref _trafficPercent, value); }
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

    public void ShowStatusMessage(string message) => StatusMessage = message;

    public async Task InitializeAsync()
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
                TunEnabled = _settings.TunEnabled;
                RefreshCoreStatus(coreVersion);
                ApplyProfile();
            });
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"初始化失败：{exception.GetBaseException().Message}");
        }
    }

    public async Task ToggleConnectionAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (_engine.IsRunning)
            {
                await _engine.StopAsync().ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(ApplyDisconnectedState);
                return;
            }
            if (_profile is null || _profile.Nodes.Count == 0) throw new InvalidOperationException("请先配置并更新订阅");
            _settings.Mode = Mode;
            _settings.TunEnabled = TunEnabled;
            var configPath = await _configurationService.WriteAsync(_profile, _settings).ConfigureAwait(false);
            await _settingsStore.SaveAsync(_settings).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage =
                TunEnabled && _engine.RequiresAdministratorForTun
                    ? "请在系统提示中允许管理员权限…"
                    : "正在启动网络加速…");
            await _engine.StartAsync(configPath, requireAdministrator: TunEnabled).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _connectedAt = DateTimeOffset.Now;
                IsConnected = true;
                ConnectionStatusText = "已连接";
                StatusMessage = TunEnabled ? "TUN 已接管系统网络流量" : "系统代理已启用";
                StartDurationTimer();
            });
            _ = RefreshLatenciesAsync();
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyDisconnectedState();
                StatusMessage = $"连接失败：{exception.GetBaseException().Message}";
            });
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
                StatusMessage = $"sing-box {SingBoxInstallerService.StableVersion} 安装成功";
            });
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"核心安装失败：{exception.GetBaseException().Message}");
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
                StatusMessage = $"订阅更新成功，共 {_profile.Nodes.Count} 个节点";
            });
            _ = RefreshLatenciesAsync();
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"订阅更新失败：{exception.GetBaseException().Message}");
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }
    }

    public async Task SetModeAsync(ProxyMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        _settings.Mode = mode;
        OnPropertyChanged(nameof(RulesText));
        await _settingsStore.SaveAsync(_settings).ConfigureAwait(false);
        if (IsConnected) await RestartAsync().ConfigureAwait(false);
    }

    public async Task SetTunEnabledAsync(bool enabled)
    {
        TunEnabled = enabled;
        _settings.TunEnabled = enabled;
        if (enabled && _engine.RequiresAdministratorForTun)
            StatusMessage = "TUN 将在开始加速时请求管理员权限";
        await _settingsStore.SaveAsync(_settings).ConfigureAwait(false);
        if (IsConnected) await RestartAsync().ConfigureAwait(false);
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
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"节点切换失败：{exception.GetBaseException().Message}");
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
        await _engine.StopAsync().ConfigureAwait(false);
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
                await _engine.StopAsync().ConfigureAwait(false);
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
                StatusMessage = $"{successPrefix ?? "已切换到配置"}：{selected.Name}";
            });
        }
        catch (Exception exception)
        {
            reconnect = false;
            await Dispatcher.UIThread.InvokeAsync(() =>
                StatusMessage = $"配置切换失败：{exception.GetBaseException().Message}");
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
            StatusMessage = "请配置订阅后开始使用";
            return;
        }

        Nodes.Add(new NodeItemViewModel(null, automatic: true));
        foreach (var node in _profile.Nodes) Nodes.Add(new NodeItemViewModel(node));
        ApplyNodeFilter();
        HasNodes = _profile.Nodes.Count > 0;
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

    private void ApplyDisconnectedState()
    {
        _durationTimer?.Dispose();
        _durationTimer = null;
        IsConnected = false;
        ConnectionStatusText = "未连接";
        ConnectionDurationText = "00:00:00";
        if (!StatusMessage.StartsWith("连接失败", StringComparison.Ordinal)) StatusMessage = "网络加速已停止";
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
        Dispatcher.UIThread.Post(() => LastLogText = SanitizeLog(message));

    private void Engine_StateChanged(object? sender, EventArgs e)
    {
        if (!_engine.IsRunning) Dispatcher.UIThread.Post(ApplyDisconnectedState);
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
