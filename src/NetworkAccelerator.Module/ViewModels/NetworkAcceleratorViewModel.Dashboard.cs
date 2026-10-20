using Avalonia.Threading;
using NetworkAccelerator.Core.Models;
using NetworkAccelerator.Core.Services;
using NetworkAccelerator.Module.Controls;

namespace NetworkAccelerator.Module.ViewModels;

public sealed partial class NetworkAcceleratorViewModel
{
    private readonly AcceleratorDashboard _dash = new();
    private CancellationTokenSource? _realtimeCts;
    private int _exitLookupVersion;
    private int _serviceTestVersion;
    private int _previewNodeCount;
    private bool _hasPreviewConfig;
    private bool _allowLan;
    private bool _reduceMotion;
    private DateTimeOffset _previewUpdated;
    private string _previewSource = string.Empty;
    private string? _exitCacheKey;
    private ExitIpInfo? _cachedExit;
    private string _lanAddressText = "—";

    public AcceleratorDashboard Dash => _dash;
    public string OverviewRunText => IsConnected ? "运行中" : "未运行";
    public string OverviewDurationText => IsConnected ? ConnectionDurationText : "—";
    public bool IsRuleMode => Mode == ProxyMode.Rule;
    public bool IsGlobalMode => Mode == ProxyMode.Global;
    public bool IsDirectMode => Mode == ProxyMode.Direct;
    public int ModeIndex => Mode switch
    {
        ProxyMode.Global => 1,
        ProxyMode.Direct => 2,
        _ => 0
    };
    public bool IsSystemProxy => !TunEnabled;
    public string SystemProxyDetailText => IsSystemProxy ? $"端口 {ProxyPort}" : "已关闭";
    public string TunDetailText => TunEnabled ? "已开启" : "已关闭";
    public string AllowLanDetailText => AllowLan ? "已开启" : "已关闭";
    public string LanAddressText { get => _lanAddressText; private set => SetField(ref _lanAddressText, value); }
    public bool ShowOverviewAlert =>
        IsBusy ||
        StatusMessage.Contains("失败", StringComparison.Ordinal) ||
        StatusMessage.Contains("管理员", StringComparison.Ordinal);

    public bool AllowLan
    {
        get => _allowLan;
        private set
        {
            if (SetField(ref _allowLan, value)) OnPropertyChanged(nameof(AllowLanDetailText));
        }
    }

    public bool ReduceMotion
    {
        get => _reduceMotion;
        private set
        {
            if (!SetField(ref _reduceMotion, value)) return;
            DashboardMotion.ReduceMotion = value;
        }
    }

    public async Task SetReduceMotionAsync(bool enabled)
    {
        if (_disposed || ReduceMotion == enabled && _settings.ReduceMotion == enabled) return;
        ReduceMotion = enabled;
        _settings.ReduceMotion = enabled;
        try
        {
            await _settingsStore.SaveAsync(_settings).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"保存设置失败：{exception.GetBaseException().Message}");
        }
    }

    public void PreviewSetMode(ProxyMode mode)
    {
        _settings.Mode = mode;
        Mode = mode;
    }

    public void PreviewSetTun(bool enabled) => TunEnabled = enabled;

    public string ConfigUpdatedText => HasDashboardConfig
        ? TrafficFormatter.FormatRelativeUpdate(ConfigUpdatedAt, DateTimeOffset.Now)
        : "尚未更新";
    public string ConfigSourceTag => HasDashboardConfig ? TrafficFormatter.DescribeSource(ConfigSource) : "—";
    public string StrategyGroupCountText => DashboardNodeCount > 0 ? "2" : "0";
    public string ProxyNodeCountText => DashboardNodeCount.ToString();
    public string RouteRuleCountText => SingBoxConfigurationService.CountGeneratedRouteRules(Mode).ToString();

    public async Task SetAllowLanAsync(bool enabled)
    {
        if (_disposed || (AllowLan == enabled && _settings.AllowLan == enabled)) return;
        AllowLan = enabled;
        _settings.AllowLan = enabled;
        try
        {
            await _settingsStore.SaveAsync(_settings).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"保存设置失败：{exception.GetBaseException().Message}");
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = enabled
            ? "已开启局域网代理。同一网络中的设备可以使用此代理"
            : "已关闭局域网代理");
        if (IsConnected) await RestartAsync().ConfigureAwait(false);
    }

    public async Task RefreshServiceStatusAsync()
    {
        if (_disposed || !IsConnected) return;
        var version = Interlocked.Increment(ref _serviceTestVersion);
        var secret = _settings.ApiSecret;
        await Dispatcher.UIThread.InvokeAsync(() => _dash.BeginServiceTest());
        var results = new int?[ServiceProbeCatalog.All.Count];
        try
        {
            using var api = new ClashApiClient(secret);
            await Task.WhenAll(ServiceProbeCatalog.All.Select((probe, index) =>
                MeasureServiceAsync(api, probe.Url, results, index))).ConfigureAwait(false);
        }
        catch
        {
            // Individual probes already record a timeout.
        }

        if (_disposed || version != _serviceTestVersion) return;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_disposed || !IsConnected || version != _serviceTestVersion) return;
            _dash.ApplyServiceResults(results);
        });
    }

    public void ShowDashboardPreview(DashboardPreview preview)
    {
        _hasPreviewConfig = preview.HasConfiguration;
        _previewNodeCount = preview.NodeCount;
        _previewUpdated = preview.UpdatedAt;
        _previewSource = preview.Source;
        _settings.Mode = preview.Mode;
        _settings.TunEnabled = preview.TunEnabled;
        _settings.AllowLan = preview.AllowLan;
        Mode = preview.Mode;
        TunEnabled = preview.TunEnabled;
        AllowLan = preview.AllowLan;
        IsConnected = preview.IsConnected;
        ConnectionStatusText = preview.IsConnected ? "已连接" : "未连接";
        ConnectionDurationText = preview.Duration;
        SubscriptionName = preview.SubscriptionName;
        LanAddressText = preview.LanAddress;
        if (!string.IsNullOrWhiteSpace(preview.StatusMessage)) StatusMessage = preview.StatusMessage;
        NotifyConfigCard();
        _dash.ShowPreview(preview);
    }

    private bool HasDashboardConfig => _profile is not null || _hasPreviewConfig;
    private int DashboardNodeCount => _profile?.Nodes.Count ?? _previewNodeCount;
    private DateTimeOffset ConfigUpdatedAt => _profile?.UpdatedAt ?? _previewUpdated;
    private string ConfigSource => _profile is not null
        ? ActiveConfiguration?.Source ?? _settings.SubscriptionUrl
        : _previewSource;

    private void NotifyConfigCard()
    {
        OnPropertyChanged(nameof(ConfigUpdatedText));
        OnPropertyChanged(nameof(ConfigSourceTag));
        OnPropertyChanged(nameof(StrategyGroupCountText));
        OnPropertyChanged(nameof(ProxyNodeCountText));
        OnPropertyChanged(nameof(RouteRuleCountText));
    }

    private void RefreshLanAddress() => LanAddressText = LanAddressLookup.GetPrimaryIPv4();

    private void StartRealtime()
    {
        StopRealtime();
        var cts = new CancellationTokenSource();
        _realtimeCts = cts;
        _ = RunRealtimeAsync(_settings.ApiSecret, cts);
    }

    private void StopRealtime()
    {
        Interlocked.Increment(ref _serviceTestVersion);
        try
        {
            _realtimeCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RunRealtimeAsync(string secret, CancellationTokenSource cts)
    {
        var cancellationToken = cts.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                var monitor = new ClashRealtimeMonitor();
                void OnPublished(RealtimeSnapshot snapshot)
                {
                    if (_disposed || cancellationToken.IsCancellationRequested) return;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_disposed || !IsConnected || cancellationToken.IsCancellationRequested) return;
                        _dash.ApplyRealtime(snapshot);
                    });
                }

                monitor.Published += OnPublished;
                try
                {
                    await monitor.RunAsync(secret, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // Keep the dashboard quiet and try the controller again.
                }
                finally
                {
                    monitor.Published -= OnPublished;
                    await monitor.DisposeAsync().ConfigureAwait(false);
                }

                if (cancellationToken.IsCancellationRequested || _disposed) break;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_realtimeCts, cts)) _realtimeCts = null;
            cts.Dispose();
        }
    }

    private async Task RefreshExitIpAsync(bool throughProxy, bool force)
    {
        var version = Interlocked.Increment(ref _exitLookupVersion);
        var key = throughProxy ? $"proxy:{_settings.SelectedNode}" : "direct";
        if (!force && _exitCacheKey == key && _cachedExit is not null)
        {
            var cached = _cachedExit;
            await Dispatcher.UIThread.InvokeAsync(() => _dash.ApplyExit(cached));
            return;
        }

        ExitIpInfo? info = null;
        try
        {
            info = await ExitIpLookup.LookupAsync(throughProxy, ProxyPort).ConfigureAwait(false);
        }
        catch
        {
            info = null;
        }

        if (_disposed || version != Volatile.Read(ref _exitLookupVersion)) return;
        if (info is not null)
        {
            _exitCacheKey = key;
            _cachedExit = info;
        }

        var display = info ?? (_exitCacheKey == key ? _cachedExit : null);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_disposed || version != _exitLookupVersion) return;
            _dash.ApplyExit(display);
        });
    }

    private static async Task MeasureServiceAsync(ClashApiClient api, string url, int?[] results, int index)
    {
        try
        {
            results[index] = await api.MeasureDelayAsync(ClashApiClient.SelectorTag, url).ConfigureAwait(false);
        }
        catch
        {
            results[index] = null;
        }
    }
}
