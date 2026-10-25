using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using AsterDock.Contracts;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using FileShareApp.Module.Services;

namespace FileShareApp.Module.Views;

public partial class FileShareView : UserControl, INotifyPropertyChanged
{
    private readonly IApplicationContext _context;
    private readonly ShareServerManager _server;
    private readonly ShareSettings _settings;
    private bool _running;
    private bool _reveal;
    private string _message = "";
    private string _portText;

    public FileShareView() : this(new DesignContext()) { }

    public FileShareView(IApplicationContext context) : this(context, new ShareServerManager(context.DataDirectory)) { }

    public FileShareView(IApplicationContext context, ShareServerManager server)
    {
        _context = context;
        _server = server;
        _settings = _server.Load();
        if (string.IsNullOrWhiteSpace(_settings.Password))
        {
            _settings.Password = ShareSettings.GeneratePassword();
            _server.Save(_settings);
        }
        _running = _server.IsRunning;
        _portText = _settings.Port.ToString();
        _settings.PropertyChanged += SettingsChanged;
        _server.StateChanged += ServerStateChanged;
        InitializeComponent();
        DataContext = this;
    }

    public ShareSettings Settings => _settings;
    public string StatusTitle => _running ? "共享已开启" : "共享已关闭";
    public string StatusDescription => _running ? "局域网内的设备可以访问共享网页" : "开启后，同一网络的设备即可访问";
    public string ProtectionText => _settings.PasswordEnabled ? "口令保护" : "无需口令";
    public string PasswordToggleText => _settings.PasswordEnabled ? "已开启" : "已关闭";
    public string DirectoryName => string.IsNullOrWhiteSpace(_settings.Root) ? "未选择" : new DirectoryInfo(_settings.Root).Name;
    public string PortText { get => _portText; set { _portText = value; OnPropertyChanged(); } }
    public string LanAddress => ShareServerManager.GetLanAddresses().FirstOrDefault() ?? "未检测到局域网";
    public string PortSummary => $"端口 {_settings.Port} · 浏览器访问";
    public string ShareUrl => BuildUrl(_settings.PasswordEnabled);
    public string CopyHint => _settings.PasswordEnabled ? "已包含口令，对方打开即可访问" : "当前为普通链接，无需口令";
    public char PasswordChar => _reveal ? '\0' : '●';
    public string RevealText => _reveal ? "隐藏" : "显示";
    public string SwitchHint => _running ? "点击停止共享" : "点击开启共享";
    public bool IsRunning { get => _running; set => _ = SetRunningAsync(value); }
    public string Message { get => _message; private set { _message = value; OnPropertyChanged(); } }

    public void Release()
    {
        _settings.PropertyChanged -= SettingsChanged;
        _server.StateChanged -= ServerStateChanged;
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        Refresh();
        OnPropertyChanged(nameof(DirectoryName));
        OnPropertyChanged(nameof(PasswordToggleText));
    }

    private void ServerStateChanged()
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(ServerStateChanged);
            return;
        }
        _running = _server.IsRunning;
        Refresh();
    }

    private async void ChooseDirectory(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;
        var selected = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择共享目录", AllowMultiple = false });
        var path = selected.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        _settings.Root = path;
        Persist();
    }

    private void OpenDirectory(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Directory.Exists(_settings.Root)) Process.Start(new ProcessStartInfo(_settings.Root) { UseShellExecute = true });
    }

    private void ToggleReveal(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _reveal = !_reveal;
        OnPropertyChanged(nameof(PasswordChar));
        OnPropertyChanged(nameof(RevealText));
    }

    private void Regenerate(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _settings.Password = ShareSettings.GeneratePassword();
        Persist();
        Message = "已生成新口令，应用设置后生效。";
    }

    private async void Apply(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!int.TryParse(_portText, out var port) || port is < 1 or > 65535)
        {
            Message = "端口需要是 1 到 65535 的数字。";
            return;
        }
        _settings.Port = port;
        Persist();
        if (_running) await RestartAsync();
        else Message = "设置已保存。";
        Refresh();
    }

    private async void CopyLink(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(ShareUrl);
        Message = "分享链接已复制。";
    }

    private void OpenBrowser(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo(BuildUrl(false)) { UseShellExecute = true });

    private async Task SetRunningAsync(bool value)
    {
        if (_running == value) return;
        try
        {
            if (value) await _server.StartAsync(_settings, CancellationToken.None);
            else await _server.StopAsync();
            _running = value;
            Message = value ? "共享服务已启动。" : "共享服务已停止。";
        }
        catch (Exception ex)
        {
            _running = _server.IsRunning;
            Message = ex.Message;
            await _context.Feedback.ShowErrorAsync("无法更改共享状态", ex.Message, ex);
        }
        Refresh();
    }

    private async Task RestartAsync()
    {
        await _server.StopAsync();
        await _server.StartAsync(_settings, CancellationToken.None);
        _running = true;
        Message = "设置已应用，共享服务已重启。";
    }

    private void Persist() => _server.Save(_settings);

    private string BuildUrl(bool includePassword)
    {
        var address = ShareServerManager.GetLanAddresses().FirstOrDefault() ?? "localhost";
        var url = $"http://{address}:{_settings.Port}/";
        return includePassword && !string.IsNullOrWhiteSpace(_settings.Password)
            ? $"{url}?ak={Uri.EscapeDataString(_settings.Password)}"
            : url;
    }

    private void Refresh()
    {
        foreach (var name in new[] { nameof(StatusTitle), nameof(StatusDescription), nameof(IsRunning), nameof(SwitchHint), nameof(ShareUrl), nameof(PortSummary), nameof(LanAddress), nameof(ProtectionText), nameof(CopyHint) })
            OnPropertyChanged(name);
    }

    public new event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private sealed class DesignContext : IApplicationContext
    {
        public string ApplicationId => "file-share";
        public string DataDirectory => Path.GetTempPath();
        public IWindowService Windows => null!;
        public IApplicationShell Shell => null!;
        public ISystemMetricsService SystemMetrics => null!;
        public ILoadingService Loading => null!;
        public IUserFeedbackService Feedback => null!;
    }
}
