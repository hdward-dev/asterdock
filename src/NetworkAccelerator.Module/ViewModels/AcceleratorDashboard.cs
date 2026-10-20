using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using NetworkAccelerator.Core.Models;
using NetworkAccelerator.Core.Services;

namespace NetworkAccelerator.Module.ViewModels;

public sealed class AcceleratorDashboard : INotifyPropertyChanged
{
    public const int VisibleRowCap = 40;
    private readonly ServiceProbe[] _probes;
    private long _uploadPeak;
    private long _downloadPeak;
    private bool _live;
    private bool _testing;
    private int _tab;
    private int _liveCount;
    private int _recentCount;
    private int _dnsCount;
    private bool _showConnectionRows;
    private bool _hasConnectionOverflow;
    private bool _hasGroups;
    private bool _hasCountryCode;
    private bool _hasExitKind;
    private bool _hasExitRegion;
    private string _summaryLevel = "idle";
    private string _uploadRateText = "—";
    private string _downloadRateText = "—";
    private string _uploadPeakText = "—";
    private string _downloadPeakText = "—";
    private string _uploadTotalText = "—";
    private string _downloadTotalText = "—";
    private string _tcpChipText = "TCP —";
    private string _udpChipText = "UDP —";
    private string _connectionsEmptyText = "未连接";
    private string _connectionOverflowText = string.Empty;
    private string _groupsEmptyText = "未连接";
    private string _groupCountText = "—";
    private string _serviceSummaryText = "未连接";
    private string _exitIpText = "—";
    private string _countryCodeText = string.Empty;
    private string _exitKindText = string.Empty;
    private string _exitRegionText = string.Empty;
    private string _exitOrgText = "—";
    private IReadOnlyList<ConnectionRow> _liveRows = [];
    private IReadOnlyList<ConnectionRow> _recentRows = [];
    private IReadOnlyList<ConnectionRow> _dnsRows = [];

    public AcceleratorDashboard()
    {
        Google = new ServiceProbe("Google");
        GitHub = new ServiceProbe("GitHub");
        YouTube = new ServiceProbe("YouTube");
        ChatGPT = new ServiceProbe("ChatGPT");
        _probes = [Google, GitHub, YouTube, ChatGPT];
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ConnectionLine> VisibleConnections { get; } = [];
    public ObservableCollection<ProxyGroupInfo> ProxyGroups { get; } = [];
    public ServiceProbe Google { get; }
    public ServiceProbe GitHub { get; }
    public ServiceProbe YouTube { get; }
    public ServiceProbe ChatGPT { get; }

    public string UploadRateText { get => _uploadRateText; private set => SetField(ref _uploadRateText, value); }
    public string DownloadRateText { get => _downloadRateText; private set => SetField(ref _downloadRateText, value); }
    public string UploadPeakText { get => _uploadPeakText; private set => SetField(ref _uploadPeakText, value); }
    public string DownloadPeakText { get => _downloadPeakText; private set => SetField(ref _downloadPeakText, value); }
    public string UploadTotalText { get => _uploadTotalText; private set => SetField(ref _uploadTotalText, value); }
    public string DownloadTotalText { get => _downloadTotalText; private set => SetField(ref _downloadTotalText, value); }
    public string TcpChipText { get => _tcpChipText; private set => SetField(ref _tcpChipText, value); }
    public string UdpChipText { get => _udpChipText; private set => SetField(ref _udpChipText, value); }
    public string ConnectionsEmptyText { get => _connectionsEmptyText; private set => SetField(ref _connectionsEmptyText, value); }
    public string ConnectionOverflowText { get => _connectionOverflowText; private set => SetField(ref _connectionOverflowText, value); }
    public string GroupsEmptyText { get => _groupsEmptyText; private set => SetField(ref _groupsEmptyText, value); }
    public string GroupCountText { get => _groupCountText; private set => SetField(ref _groupCountText, value); }
    public string ServiceSummaryText { get => _serviceSummaryText; private set => SetField(ref _serviceSummaryText, value); }
    public string ExitIpText { get => _exitIpText; private set => SetField(ref _exitIpText, value); }
    public string CountryCodeText { get => _countryCodeText; private set => SetField(ref _countryCodeText, value); }
    public string ExitKindText { get => _exitKindText; private set => SetField(ref _exitKindText, value); }
    public string ExitRegionText { get => _exitRegionText; private set => SetField(ref _exitRegionText, value); }
    public string ExitOrgText { get => _exitOrgText; private set => SetField(ref _exitOrgText, value); }
    public bool ShowConnectionRows { get => _showConnectionRows; private set => SetField(ref _showConnectionRows, value); }
    public bool HasConnectionOverflow { get => _hasConnectionOverflow; private set => SetField(ref _hasConnectionOverflow, value); }
    public bool HasGroups { get => _hasGroups; private set => SetField(ref _hasGroups, value); }
    public bool HasNoGroups => !HasGroups;
    public bool HasCountryCode { get => _hasCountryCode; private set => SetField(ref _hasCountryCode, value); }
    public bool HasExitKind { get => _hasExitKind; private set => SetField(ref _hasExitKind, value); }
    public bool HasExitRegion { get => _hasExitRegion; private set => SetField(ref _hasExitRegion, value); }
    public bool IsLiveTab => _tab == 0;
    public bool IsRecentTab => _tab == 1;
    public bool IsDnsTab => _tab == 2;
    public bool CanTestServices => _live && !_testing;
    public bool ServicesAllAvailable => _summaryLevel == "all";
    public bool ServicesPartial => _summaryLevel == "partial";
    public bool ServicesUnavailable => _summaryLevel == "down";
    public string LiveTabText => $"{_liveCount} 实时连接";
    public string RecentTabText => $"{_recentCount} 最近请求";
    public string DnsTabText => $"{_dnsCount} DNS查询";

    public int LiveCount
    {
        get => _liveCount;
        private set
        {
            if (SetField(ref _liveCount, value)) OnPropertyChanged(nameof(LiveTabText));
        }
    }

    public int RecentCount
    {
        get => _recentCount;
        private set
        {
            if (SetField(ref _recentCount, value)) OnPropertyChanged(nameof(RecentTabText));
        }
    }

    public int DnsCount
    {
        get => _dnsCount;
        private set
        {
            if (SetField(ref _dnsCount, value)) OnPropertyChanged(nameof(DnsTabText));
        }
    }

    public void SelectTab(int tab)
    {
        var next = Math.Clamp(tab, 0, 2);
        if (_tab == next) return;
        _tab = next;
        OnPropertyChanged(nameof(IsLiveTab));
        OnPropertyChanged(nameof(IsRecentTab));
        OnPropertyChanged(nameof(IsDnsTab));
        RefreshVisible();
    }

    public void ApplyRealtime(RealtimeSnapshot snapshot)
    {
        _live = true;
        if (snapshot.UploadRate > _uploadPeak) _uploadPeak = snapshot.UploadRate;
        if (snapshot.DownloadRate > _downloadPeak) _downloadPeak = snapshot.DownloadRate;
        UploadRateText = TrafficFormatter.FormatRate(snapshot.UploadRate);
        DownloadRateText = TrafficFormatter.FormatRate(snapshot.DownloadRate);
        UploadPeakText = TrafficFormatter.FormatRate(_uploadPeak);
        DownloadPeakText = TrafficFormatter.FormatRate(_downloadPeak);
        UploadTotalText = TrafficFormatter.FormatTotal(snapshot.UploadTotal);
        DownloadTotalText = TrafficFormatter.FormatTotal(snapshot.DownloadTotal);
        TcpChipText = $"TCP {snapshot.TcpCount}";
        UdpChipText = $"UDP {snapshot.UdpCount}";
        _liveRows = snapshot.Live;
        _recentRows = snapshot.Recent;
        _dnsRows = snapshot.Dns;
        LiveCount = snapshot.Live.Count;
        RecentCount = snapshot.Recent.Count;
        DnsCount = snapshot.Dns.Count;
        ReplaceGroups(snapshot.Groups);
        RefreshVisible();
        OnPropertyChanged(nameof(CanTestServices));
    }

    public void ResetLive()
    {
        _live = false;
        _testing = false;
        _uploadPeak = 0;
        _downloadPeak = 0;
        UploadRateText = "—";
        DownloadRateText = "—";
        UploadPeakText = "—";
        DownloadPeakText = "—";
        UploadTotalText = "—";
        DownloadTotalText = "—";
        TcpChipText = "TCP —";
        UdpChipText = "UDP —";
        _liveRows = [];
        _recentRows = [];
        _dnsRows = [];
        LiveCount = 0;
        RecentCount = 0;
        DnsCount = 0;
        foreach (var probe in _probes) probe.Reset();
        ReplaceGroups([]);
        RefreshVisible();
        UpdateSummary();
        OnPropertyChanged(nameof(CanTestServices));
    }

    public void BeginServiceTest()
    {
        if (!_live) return;
        _testing = true;
        SetSummary("检测中", "idle");
        OnPropertyChanged(nameof(CanTestServices));
    }

    public void ApplyServiceResults(IReadOnlyList<int?> delays)
    {
        _testing = false;
        for (var i = 0; i < _probes.Length; i++)
            _probes[i].Apply(i < delays.Count ? delays[i] : null, true);
        UpdateSummary();
        OnPropertyChanged(nameof(CanTestServices));
    }

    public void ApplyExit(ExitIpInfo? info)
    {
        ExitIpText = string.IsNullOrWhiteSpace(info?.Ip) ? "—" : info.Ip;
        CountryCodeText = string.IsNullOrWhiteSpace(info?.CountryCode) || info.CountryCode == "—" ? string.Empty : info.CountryCode;
        HasCountryCode = CountryCodeText.Length > 0;
        ExitKindText = string.IsNullOrWhiteSpace(info?.NetworkKind) || info.NetworkKind == "—" ? string.Empty : info.NetworkKind;
        HasExitKind = ExitKindText.Length > 0;
        ExitRegionText = string.IsNullOrWhiteSpace(info?.Region) || info.Region == "—" ? string.Empty : info.Region;
        HasExitRegion = ExitRegionText.Length > 0;
        ExitOrgText = string.IsNullOrWhiteSpace(info?.Organization) || info.Organization == "—" ? "—" : info.Organization;
    }

    public void ShowPreview(DashboardPreview preview)
    {
        if (!preview.IsConnected)
        {
            ResetLive();
            ApplyExit(preview.Exit);
            return;
        }

        _uploadPeak = Math.Max(0, preview.UploadPeak);
        _downloadPeak = Math.Max(0, preview.DownloadPeak);
        var dns = preview.Live.Concat(preview.Recent).Where(row => row.IsDns).ToArray();
        ApplyRealtime(new RealtimeSnapshot(
            preview.UploadRate,
            preview.DownloadRate,
            preview.UploadTotal,
            preview.DownloadTotal,
            preview.Live,
            preview.Recent,
            dns,
            preview.Tcp,
            preview.Udp,
            preview.Groups));
        ApplyServiceResults(preview.ServiceDelays);
        ApplyExit(preview.Exit);
    }

    private void ReplaceGroups(IReadOnlyList<ProxyGroupInfo> groups)
    {
        Sync(ProxyGroups, groups);
        HasGroups = _live && groups.Count > 0;
        OnPropertyChanged(nameof(HasNoGroups));
        GroupsEmptyText = HasGroups ? string.Empty : _live ? "没有策略组" : "未连接";
        GroupCountText = _live ? groups.Count.ToString() : "—";
    }

    private void RefreshVisible()
    {
        var source = _tab switch
        {
            1 => _recentRows,
            2 => _dnsRows,
            _ => _liveRows
        };
        var now = DateTimeOffset.Now;
        var lines = source.Take(VisibleRowCap).Select(row => new ConnectionLine(
            row.SourceText,
            row.TargetText,
            row.ProtocolText,
            row.OutboundText,
            TrafficFormatter.FormatTotal(row.Download),
            TrafficFormatter.FormatTotal(row.Upload),
            TrafficFormatter.FormatDuration(now - row.Start))).ToArray();
        Sync(VisibleConnections, lines);
        ShowConnectionRows = lines.Length > 0;
        ConnectionsEmptyText = lines.Length == 0 ? EmptyText() : string.Empty;
        HasConnectionOverflow = source.Count > VisibleRowCap;
        ConnectionOverflowText = HasConnectionOverflow ? $"还有 {source.Count - VisibleRowCap} 条未显示" : string.Empty;
    }

    private string EmptyText()
    {
        if (!_live) return "未连接";
        return _tab switch
        {
            1 => "还没有结束的连接",
            2 => "没有 DNS 查询",
            _ => "当前没有连接"
        };
    }

    private void UpdateSummary()
    {
        if (!_live)
        {
            SetSummary("未连接", "idle");
            return;
        }

        if (_testing)
        {
            SetSummary("检测中", "idle");
            return;
        }

        if (_probes.All(probe => probe.IsIdle))
        {
            SetSummary("未检测", "idle");
            return;
        }

        var down = _probes.Count(probe => probe.IsDown);
        var available = _probes.Count(probe => probe.IsOk || probe.IsSlow);
        if (down == _probes.Length) SetSummary("不可用", "down");
        else if (available == _probes.Length) SetSummary("全部可用", "all");
        else SetSummary("部分可用", "partial");
    }

    private void SetSummary(string text, string level)
    {
        ServiceSummaryText = text;
        if (_summaryLevel == level) return;
        _summaryLevel = level;
        OnPropertyChanged(nameof(ServicesAllAvailable));
        OnPropertyChanged(nameof(ServicesPartial));
        OnPropertyChanged(nameof(ServicesUnavailable));
    }

    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        while (target.Count > source.Count) target.RemoveAt(target.Count - 1);
        for (var i = 0; i < source.Count; i++)
        {
            if (i == target.Count) target.Add(source[i]);
            else if (!EqualityComparer<T>.Default.Equals(target[i], source[i])) target[i] = source[i];
        }
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
