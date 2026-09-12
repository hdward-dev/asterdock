using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AndroidScreen.Module.Models;
using AndroidScreen.Module.Services;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace AndroidScreen.Module.ViewModels;

public sealed class AndroidScreenViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly string _moduleDirectory;
    private readonly ScrcpyInstallerService _installer;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private EmbeddedScreenSession? _session;
    private sealed record PendingFrame(EmbeddedScreenSession Session, byte[] Bytes);
    private PendingFrame? _pendingFrame;
    private int _renderQueued;
    private bool _disposed;
    private bool _initialized;
    private bool _refreshingDevices;
    private DispatcherTimer? _deviceTimer;
    private string _networkAddress = "";
    private string _pairAddress = "";
    private string _pairCode = "";
    private string _statusMessage = "连接手机，让操作回到大屏幕。";
    private bool _isRunning, _isBusy, _isCoreAvailable;
    private AndroidDevice? _selectedDevice;
    private Bitmap? _frame;
    private string _deviceName = "设备预览";
    private string _frameInfo = "画面将显示在这里";
    private int _qualityIndex = 1;
    private string? _receivedClipboard;
    private bool _hasError;
    private string _errorDetails = "";

    public AndroidScreenViewModel(string dataDirectory, string moduleDirectory)
    { _moduleDirectory = moduleDirectory; _installer = new(dataDirectory); }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<AndroidDevice> Devices { get; } = [];
    public AndroidDevice? SelectedDevice { get => _selectedDevice; set => SetField(ref _selectedDevice, value); }
    public string NetworkAddress { get => _networkAddress; set => SetField(ref _networkAddress, value); }
    public string PairAddress { get => _pairAddress; set => SetField(ref _pairAddress, value); }
    public string PairCode { get => _pairCode; set => SetField(ref _pairCode, value); }
    public int QualityIndex { get => _qualityIndex; set => SetField(ref _qualityIndex, value); }
    public string StatusMessage { get => _statusMessage; private set => SetField(ref _statusMessage, value); }
    public string DeviceName { get => _deviceName; private set => SetField(ref _deviceName, value); }
    public string FrameInfo { get => _frameInfo; private set => SetField(ref _frameInfo, value); }
    public Bitmap? Frame { get => _frame; private set { if (SetField(ref _frame, value)) OnPropertyChanged(nameof(HasFrame)); } }
    public bool HasFrame => Frame is not null;
    public bool IsRunning { get => _isRunning; private set { if (SetField(ref _isRunning, value)) NotifyState(); } }
    public bool IsBusy { get => _isBusy; private set { if (SetField(ref _isBusy, value)) NotifyState(); } }
    public bool CanConfigure => !IsBusy && !IsRunning;
    public bool IsCoreAvailable { get => _isCoreAvailable; private set { if (SetField(ref _isCoreAvailable, value)) OnPropertyChanged(nameof(IsCoreMissing)); } }
    public bool IsCoreMissing => !IsCoreAvailable;
    public string LaunchButtonText => IsRunning ? "结束投屏" : IsBusy ? "取消连接" : "开始投屏";
    public string ConnectionLabel => HasFrame ? "正在投屏" : IsBusy || IsRunning ? "连接中" : "未连接";
    public string? ReceivedClipboard => _receivedClipboard;
    public bool HasError { get => _hasError; private set => SetField(ref _hasError, value); }
    public string ErrorDetails { get => _errorDetails; private set => SetField(ref _errorDetails, value); }

    public async Task InitializeAsync()
    {
        if (_initialized || _disposed) return; _initialized = true;
        IsCoreAvailable = _installer.FindExecutable() is not null;
        if (IsCoreAvailable || ScreenRuntime.FindAdb(_moduleDirectory) is not null) await RefreshAsync();
        else StatusMessage = "请点击“准备投屏核心”，完成后会自动检测 USB 设备。";
        if (_disposed) return;
        _deviceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _deviceTimer.Tick += async (_, _) => await PollDevicesAsync();
        _deviceTimer.Start();
    }

    public async Task InstallAsync() => await ExecuteAsync(async token =>
    {
        StatusMessage = "正在下载并校验投屏核心，首次准备可能需要几分钟…";
        await _installer.InstallAsync(token);
        IsCoreAvailable = true;
        await RefreshDevicesAsync(token);
    });

    public async Task RefreshAsync() => await ExecuteAsync(RefreshDevicesAsync);
    private AndroidBridge GetBridge()
    {
        var executable = _installer.FindExecutable();
        var adb = executable is not null ? ScreenRuntime.ResolveCore(executable).Adb : ScreenRuntime.FindAdb(_moduleDirectory);
        return new AndroidBridge(adb ?? throw new IOException("请点击“准备投屏核心”，完成后会自动检测 USB 设备。"));
    }
    private Task RefreshDevicesAsync(CancellationToken token) => RefreshDevicesAsync(token, true);

    private async Task RefreshDevicesAsync(CancellationToken token, bool showStatus)
    {
        if (_refreshingDevices) return;
        _refreshingDevices = true;
        try
        {
            IsCoreAvailable = _installer.FindExecutable() is not null;
            var devices = await GetBridge().DevicesAsync(token);
            if (_disposed || IsRunning) return;
            var previous = SelectedDevice?.Serial;
            var changed = !Devices.SequenceEqual(devices);
            if (!changed && !showStatus) return;
            if (changed) { Devices.Clear(); foreach (var device in devices) Devices.Add(device); }
            SelectedDevice = Devices.FirstOrDefault(x => x.Serial == previous) ?? Devices.FirstOrDefault(x => x.IsAvailable) ?? Devices.FirstOrDefault();
            StatusMessage = Devices.Count == 0 ? "未发现设备。请连接 USB，或填写无线调试地址。" :
                Devices.Any(x => x.State == "unauthorized") ? "请解锁手机并允许 USB 调试，然后刷新设备。" : $"已发现 {Devices.Count} 台设备，选择后即可投屏。";
        }
        finally { _refreshingDevices = false; }
    }

    private async Task PollDevicesAsync()
    {
        if (_disposed || !CanConfigure || _refreshingDevices) return;
        try { await RefreshDevicesAsync(_lifetime.Token, false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or System.ComponentModel.Win32Exception)
        { /* Explicit refresh reports errors; background checks preserve the current status. */ }
    }

    public async Task ConnectWirelessAsync() => await ExecuteAsync(async token =>
    {
        var address = ValidateAddress(NetworkAddress);
        StatusMessage = "正在连接无线设备…";
        var result = await GetBridge().RunAsync(token, "connect", address);
        if (!result.Contains("connected to", StringComparison.OrdinalIgnoreCase)) throw new IOException(result);
        await RefreshDevicesAsync(token);
        SelectedDevice = Devices.FirstOrDefault(x => x.Serial == address || x.Serial == address + ":5555") ?? SelectedDevice;
    });

    public async Task PairAsync() => await ExecuteAsync(async token =>
    {
        var address = ValidateAddress(PairAddress);
        var code = PairCode.Trim();
        if (code.Length != 6 || code.Any(x => !char.IsAsciiDigit(x))) throw new ArgumentException("请输入手机显示的六位配对码。");
        StatusMessage = "正在配对无线设备…";
        var result = await GetBridge().RunAsync(token, "pair", address, code);
        PairCode = "";
        if (!result.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase)) throw new IOException(result);
        StatusMessage = "配对成功。请使用手机“无线调试”主页上的连接地址连接。";
    });

    public static string ValidateAddress(string address)
    {
        address = address.Trim();
        if (address.Length is 0 or > 260 || address.Any(x => !(char.IsAsciiLetterOrDigit(x) || x is '.' or ':' or '-' or '[' or ']')) || address.StartsWith('-'))
            throw new ArgumentException("请输入有效的设备地址，例如 192.168.1.20:5555。");
        return address;
    }

    public async Task ToggleAsync()
    {
        if (IsBusy) { _operation?.Cancel(); return; }
        if (IsRunning) { await StopAsync(); return; }
        await ExecuteAsync(async token =>
        {
            if (!IsCoreAvailable)
            {
                StatusMessage = "正在准备投屏核心…";
                await _installer.InstallAsync(token); IsCoreAvailable = true;
                await RefreshDevicesAsync(token);
            }
            var device = SelectedDevice ?? throw new IOException("请先连接并选择一台 Android 设备。");
            if (!device.IsAvailable) throw new IOException(device.State == "unauthorized" ? "请先在手机上允许 USB 调试，然后刷新设备。" : "设备离线，请重新连接后刷新。");
            var core = ScreenRuntime.ResolveCore(_installer.FindExecutable()!);
            var decoder = ScreenRuntime.ResolveDecoder(_moduleDirectory);
            var session = new EmbeddedScreenSession(new(core.Adb), device.Serial);
            _session = session;
            session.FrameReceived += bytes => QueueFrame(session, bytes);
            session.ClipboardReceived += text => Dispatcher.UIThread.Post(() =>
            {
                if (_session != session || _disposed) return;
                _receivedClipboard = text; OnPropertyChanged(nameof(ReceivedClipboard));
            });
            StatusMessage = "正在建立画面和控制通道…";
            try
            {
                await session.StartAsync(core.Server, decoder, QualityIndex switch { 0 => 1024, 2 => 1920, _ => 1600 }, token);
                token.ThrowIfCancellationRequested();
                DeviceName = session.DeviceName; IsRunning = true;
                StatusMessage = "已连接，正在等待第一帧画面…";
                _ = ObserveSessionAsync(session);
            }
            catch { if (_session == session) _session = null; await session.DisposeAsync(); ClearFrame(); throw; }
        });
    }

    private async Task ObserveSessionAsync(EmbeddedScreenSession session)
    {
        string message;
        Exception? failure = null;
        try { await session.Completion; message = "投屏已结束。"; }
        catch (OperationCanceledException) { message = "投屏已结束。"; }
        catch (Exception ex) { failure = ex; message = "投屏中断，请重试。可展开错误详情查看原因。"; }
        if (_session != session) return;
        _session = null; IsRunning = false; ClearFrame(); StatusMessage = message;
        if (failure is not null) { ErrorDetails = failure.ToString(); HasError = true; }
        await session.DisposeAsync();
    }

    public async Task StopAsync(string message = "投屏已结束。")
    {
        _operation?.Cancel();
        var session = _session; _session = null;
        IsRunning = false; ClearFrame(); StatusMessage = message;
        if (session is not null) await session.DisposeAsync();
    }

    public void Send(byte[] data)
    {
        if (!IsRunning || _session is null) return;
        if (!_session.Send(data)) _ = StopAsync("设备响应过慢，已停止投屏，请重新连接。");
    }
    public void SendKey(uint code) { Send(ScrcpyProtocol.Key(code, 0)); Send(ScrcpyProtocol.Key(code, 1)); }
    public void ShowMessage(string text) => StatusMessage = text;

    private void QueueFrame(EmbeddedScreenSession session, byte[] bytes)
    {
        if (_session != session || _disposed) return;
        Interlocked.Exchange(ref _pendingFrame, new PendingFrame(session, bytes));
        if (Interlocked.Exchange(ref _renderQueued, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _renderQueued, 0);
            var frame = Interlocked.Exchange(ref _pendingFrame, null);
            if (frame is null || _session != frame.Session || _disposed) return;
            try
            {
                using var stream = new MemoryStream(frame.Bytes, writable: false);
                var bitmap = new Bitmap(stream);
                var old = Frame; Frame = bitmap; old?.Dispose();
                FrameInfo = $"{bitmap.PixelSize.Width} × {bitmap.PixelSize.Height} · 最高 30 FPS";
                if (old is null) { StatusMessage = "画面已就绪。点击画面即可控制手机。"; NotifyState(); }
            }
            catch (Exception ex) { _ = StopAsync("无法显示视频帧：" + ex.Message); }
        }, DispatcherPriority.Render);
    }

    private void ClearFrame()
    {
        Interlocked.Exchange(ref _pendingFrame, null);
        var old = Frame; Frame = null; old?.Dispose();
        FrameInfo = "画面将显示在这里"; DeviceName = "设备预览"; _receivedClipboard = null; NotifyState();
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> action)
    {
        if (!CanConfigure || _disposed) return;
        HasError = false; ErrorDetails = "";
        IsBusy = true;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _operation = operation;
        try { await action(operation.Token); }
        catch (OperationCanceledException) { if (!_disposed) StatusMessage = "操作已取消或连接超时，请检查设备后重试。"; }
        catch (Exception ex)
        {
            if (!_disposed) { StatusMessage = "操作未完成：" + ex.GetBaseException().Message; ErrorDetails = ex.ToString(); HasError = true; }
        }
        finally { _operation = null; IsBusy = false; }
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _deviceTimer?.Stop(); _lifetime.Cancel();
        _ = StopAsync(); _installer.Dispose(); _lifetime.Dispose();
    }
    private void NotifyState()
    { OnPropertyChanged(nameof(CanConfigure)); OnPropertyChanged(nameof(LaunchButtonText)); OnPropertyChanged(nameof(ConnectionLabel)); }
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; OnPropertyChanged(name); return true; }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
