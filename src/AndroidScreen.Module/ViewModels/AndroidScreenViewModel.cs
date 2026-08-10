using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using AndroidScreen.Module.Services;

namespace AndroidScreen.Module.ViewModels;

public sealed class AndroidScreenViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly string _moduleDirectory;
    private readonly ScrcpyInstallerService _installer;
    private Process? _process;
    private string _networkAddress = string.Empty;
    private string _statusMessage;
    private bool _isRunning;
    private bool _isBusy;
    private bool _isScrcpyAvailable;

    public AndroidScreenViewModel(string dataDirectory, string moduleDirectory)
    {
        _moduleDirectory = moduleDirectory;
        _installer = new ScrcpyInstallerService(dataDirectory);
        _statusMessage = "正在检测 scrcpy…";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string NetworkAddress
    {
        get => _networkAddress;
        set => SetField(ref _networkAddress, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetField(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(LaunchButtonText));
        }
    }

    public string LaunchButtonText => IsRunning ? "结束投屏" : "开始投屏";
    public bool IsBusy { get => _isBusy; private set => SetField(ref _isBusy, value); }
    public bool IsScrcpyAvailable { get => _isScrcpyAvailable; private set { if (SetField(ref _isScrcpyAvailable, value)) OnPropertyChanged(nameof(IsScrcpyMissing)); } }
    public bool IsScrcpyMissing => !IsScrcpyAvailable;

    public void Initialize()
    {
        IsScrcpyAvailable = FindExecutable() is not null;
        StatusMessage = IsScrcpyAvailable
            ? "scrcpy 已就绪。连接 Android 设备后即可开始投屏。"
            : "尚未安装 scrcpy。点击“安装 scrcpy”从官方 Release 下载。";
    }

    public async Task ToggleAsync()
    {
        if (IsBusy) return;
        if (IsRunning) Stop();
        else await StartAsync();
    }

    public async Task InstallAsync()
    {
        if (IsBusy || IsRunning) return;
        IsBusy = true;
        StatusMessage = "正在从 scrcpy 官方 Release 下载并校验安装包…";
        try
        {
            await _installer.InstallAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsScrcpyAvailable = true;
                StatusMessage = "scrcpy 安装成功。连接 Android 设备后即可开始投屏。";
            });
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"scrcpy 安装失败：{exception.GetBaseException().Message}");
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }
    }

    public void Dispose()
    {
        Stop();
        _installer.Dispose();
    }

    private async Task StartAsync()
    {
        var executable = FindExecutable();
        if (executable is null)
        {
            await InstallAsync();
            executable = FindExecutable();
            if (executable is null) return;
        }
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };

        var address = NetworkAddress.Trim();
        if (!string.IsNullOrWhiteSpace(address)) startInfo.ArgumentList.Add($"--tcpip={address}");

        try
        {
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Exited += (_, _) => Dispatcher.UIThread.Post(() => HandleExited(process));
            if (!process.Start())
            {
                process.Dispose();
                StatusMessage = "无法启动 scrcpy。";
                return;
            }

            _process = process;
            IsRunning = true;
            StatusMessage = string.IsNullOrWhiteSpace(address)
                ? "scrcpy 已启动，正在等待 USB 调试设备。"
                : $"scrcpy 已启动，正在连接 {address}。";
        }
        catch (Exception exception)
        {
            StatusMessage = $"启动 scrcpy 失败：{exception.Message}";
        }
    }

    private void Stop()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null) return;

        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process completed between the status check and Kill.
        }
        finally
        {
            process.Dispose();
            IsRunning = false;
            StatusMessage = "投屏已结束。";
        }
    }

    private void HandleExited(Process process)
    {
        if (!ReferenceEquals(_process, process)) return;
        _process = null;
        process.Dispose();
        IsRunning = false;
        StatusMessage = "scrcpy 已退出。请确认设备已连接并已授权 USB 调试。";
    }

    private string? FindExecutable()
    {
        var fileName = OperatingSystem.IsWindows() ? "scrcpy.exe" : "scrcpy";
        var installedPath = _installer.FindExecutable();
        if (installedPath is not null) return installedPath;
        var bundledPath = Path.Combine(_moduleDirectory, fileName);
        return File.Exists(bundledPath) ? bundledPath : null;
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
