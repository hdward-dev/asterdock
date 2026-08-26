using Avalonia.Media;
using SerialDebugger.Module.Models;
using SerialDebugger.Module.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SerialDebugger.Module.ViewModels;

public sealed class SerialDebuggerViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly Color[] AccentColors =
    [
        Color.Parse("#1267E8"),
        Color.Parse("#16883F"),
        Color.Parse("#B54708"),
        Color.Parse("#7F56D9")
    ];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;
    private bool _initialized;
    private bool _disposed;
    private bool _isTileLayout = true;
    private bool _isLinked;
    private bool _isOrchestrationExpanded = true;
    private bool _isBusy;
    private string _workspaceStatus = "双串口工作区已就绪";

    public SerialDebuggerViewModel(string dataDirectory)
    {
        _settingsPath = Path.Combine(dataDirectory, "workspace.json");
        CreateDefaultSessions();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<SerialPortSessionViewModel> Sessions { get; } = [];
    public bool IsTileLayout
    {
        get => _isTileLayout;
        set
        {
            if (!SetField(ref _isTileLayout, value)) return;
            OnPropertyChanged(nameof(IsTabLayout));
        }
    }
    public bool IsTabLayout => !IsTileLayout;
    public bool IsLinked { get => _isLinked; set { if (SetField(ref _isLinked, value)) WorkspaceStatus = value ? "端口联动发送已开启" : "端口联动发送已关闭"; } }
    public bool IsOrchestrationExpanded { get => _isOrchestrationExpanded; set => SetField(ref _isOrchestrationExpanded, value); }
    public bool IsBusy { get => _isBusy; private set => SetField(ref _isBusy, value); }
    public string WorkspaceStatus { get => _workspaceStatus; private set => SetField(ref _workspaceStatus, value); }
    public string ConnectedSummary => $"{Sessions.Count(session => session.IsConnected)} / {Sessions.Count} 已连接";
    public string LinkedButtonText => IsLinked ? "联动已开" : "联动";

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            if (!File.Exists(_settingsPath)) return;
            var json = await File.ReadAllTextAsync(_settingsPath).ConfigureAwait(false);
            var settings = JsonSerializer.Deserialize<SerialWorkspaceSettings>(json, JsonOptions);
            if (settings is null || settings.Ports.Count == 0) return;

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                ClearSessions();
                IsTileLayout = settings.IsTileLayout;
                IsLinked = settings.IsLinked;
                foreach (var profile in settings.Ports) AddSession(profile);
                WorkspaceStatus = $"已恢复 {Sessions.Count} 个串口卡片";
            });
        }
        catch (Exception exception)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                WorkspaceStatus = $"工作区恢复失败：{exception.GetBaseException().Message}");
        }
    }

    public void AddSession(SerialPortProfile? profile = null)
    {
        var ports = GetPortNames();
        var preferredPort = ports.FirstOrDefault(port =>
            string.Equals(port.PortName, profile?.PortName, StringComparison.OrdinalIgnoreCase))?.PortName;
        if (string.IsNullOrWhiteSpace(preferredPort))
            preferredPort = ports.FirstOrDefault(port => Sessions.All(item =>
                                !string.Equals(item.PortName, port.PortName, StringComparison.OrdinalIgnoreCase)))?.PortName
                            ?? string.Empty;

        var session = new SerialPortSessionViewModel(
            GetSessionTitle(Sessions.Count),
            preferredPort,
            profile?.BaudRate ?? (Sessions.Count == 1 ? 9600 : 115200),
            new SolidColorBrush(AccentColors[Sessions.Count % AccentColors.Length]));
        if (profile is not null) session.ApplyProfile(profile with { PortName = preferredPort });
        session.PropertyChanged += Session_PropertyChanged;
        Sessions.Add(session);
        RefreshWorkspaceProperties();
        WorkspaceStatus = $"已添加 {session.Title}";
    }

    public void DuplicateSession(SerialPortSessionViewModel source)
    {
        AddSession(source.CreateProfile());
        WorkspaceStatus = $"已复制 {source.Title} 的配置";
    }

    public void RemoveSession(SerialPortSessionViewModel session)
    {
        if (Sessions.Count <= 1)
        {
            WorkspaceStatus = "工作区至少保留一个串口卡片";
            return;
        }
        session.PropertyChanged -= Session_PropertyChanged;
        Sessions.Remove(session);
        session.Dispose();
        RenumberSessions();
        RefreshWorkspaceProperties();
        WorkspaceStatus = "串口卡片已移除";
    }

    public void CollapseAll()
    {
        var shouldCollapse = Sessions.Any(session => !session.IsCollapsed);
        foreach (var session in Sessions) session.IsCollapsed = shouldCollapse;
        WorkspaceStatus = shouldCollapse ? "已收起全部串口卡片" : "已展开全部串口卡片";
    }

    public async Task SaveAsync()
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(directory);
            var settings = new SerialWorkspaceSettings(
                IsTileLayout,
                IsLinked,
                Sessions.Select(session => session.CreateProfile()).ToList());
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            await File.WriteAllTextAsync(_settingsPath, json).ConfigureAwait(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => WorkspaceStatus = "工作区配置已保存");
        }
        catch (Exception exception)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                WorkspaceStatus = $"保存失败：{exception.GetBaseException().Message}");
        }
    }

    public async Task SendFromAsync(SerialPortSessionViewModel source)
    {
        Func<byte[], Task>? linkedSend = IsLinked
            ? payload => SendToLinkedSessionsAsync(source, payload)
            : null;
        await source.TriggerSendAsync(linkedSend).ConfigureAwait(false);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(RefreshWorkspaceProperties);
    }

    public async Task SendQuickCommandAsync(SerialPortSessionViewModel source, QuickCommand command)
    {
        Func<byte[], Task>? linkedSend = IsLinked
            ? payload => SendToLinkedSessionsAsync(source, payload)
            : null;
        await source.SendQuickCommandAsync(command, linkedSend).ConfigureAwait(false);
    }

    public async Task RunOrchestrationAsync()
    {
        if (IsBusy) return;
        if (Sessions.Count < 2)
        {
            WorkspaceStatus = "联调编排至少需要两个串口卡片";
            return;
        }
        var first = Sessions[0];
        var second = Sessions[1];
        if (!first.IsConnected || !second.IsConnected)
        {
            WorkspaceStatus = "请先连接前两个串口";
            return;
        }

        IsBusy = true;
        try
        {
            WorkspaceStatus = $"正在通过 {first.PortName} 发送启动命令…";
            await first.TriggerSendAsync().ConfigureAwait(false);
            await Task.Delay(500).ConfigureAwait(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                WorkspaceStatus = $"正在通过 {second.PortName} 读取数据…");
            await second.TriggerSendAsync().ConfigureAwait(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => WorkspaceStatus = "联调流程执行完成");
        }
        catch (Exception exception)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                WorkspaceStatus = $"联调执行失败：{exception.GetBaseException().Message}");
        }
        finally
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearSessions();
    }

    private async Task SendToLinkedSessionsAsync(SerialPortSessionViewModel source, byte[] payload)
    {
        var targets = Sessions.Where(session => !ReferenceEquals(session, source) && session.IsConnected).ToList();
        if (targets.Count == 0) return;
        await Task.WhenAll(targets.Select(session => session.SendPayloadAsync(payload))).ConfigureAwait(false);
    }

    private void CreateDefaultSessions()
    {
        var ports = GetPortNames();
        AddSession(new SerialPortProfile(
            ports.ElementAtOrDefault(0)?.PortName ?? string.Empty,
            115200, 8, "1", "无", "无", "UTF-8", "CRLF", "AA 55 01 00 FF"));
        AddSession(new SerialPortProfile(
            ports.ElementAtOrDefault(1)?.PortName ?? string.Empty,
            9600, 8, "1", "无", "无", "UTF-8", "CRLF", "01 03 00 00 00 02"));
        WorkspaceStatus = "水平平铺工作区已就绪";
    }

    private void ClearSessions()
    {
        foreach (var session in Sessions)
        {
            session.PropertyChanged -= Session_PropertyChanged;
            session.Dispose();
        }
        Sessions.Clear();
        RefreshWorkspaceProperties();
    }

    private void RenumberSessions()
    {
        for (var index = 0; index < Sessions.Count; index++) Sessions[index].Title = GetSessionTitle(index);
    }

    private void Session_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SerialPortSessionViewModel.IsConnected) or nameof(SerialPortSessionViewModel.StatusText))
            RefreshWorkspaceProperties();
    }

    private void RefreshWorkspaceProperties()
    {
        OnPropertyChanged(nameof(ConnectedSummary));
        OnPropertyChanged(nameof(LinkedButtonText));
    }

    private static IReadOnlyList<SerialPortDevice> GetPortNames()
        => SerialPortDiscovery.GetConnectedPorts();

    private static string GetSessionTitle(int index)
    {
        var value = index + 1;
        var suffix = string.Empty;
        while (value > 0)
        {
            value--;
            suffix = (char)('A' + value % 26) + suffix;
            value /= 26;
        }
        return $"端口 {suffix}";
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        if (propertyName == nameof(IsLinked)) OnPropertyChanged(nameof(LinkedButtonText));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record SerialWorkspaceSettings(
    bool IsTileLayout,
    bool IsLinked,
    List<SerialPortProfile> Ports);
