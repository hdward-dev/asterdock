using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace NetworkAccelerator.Core.Services;

public sealed class SingBoxEngineService : IDisposable
{
    private readonly object _sync = new();
    private readonly string _moduleDirectory;
    private readonly string _dataDirectory;
    private readonly LinuxTunPrivilege _linuxTun;
    private Process? _process;
    private bool _elevatedSession;
    private bool _disposed;
    private int _pkexecOutputSeen;

    public SingBoxEngineService(string moduleDirectory, string dataDirectory)
    {
        _moduleDirectory = moduleDirectory;
        _dataDirectory = dataDirectory;
        _linuxTun = new LinuxTunPrivilege(
            dataDirectory,
            new SystemProcessRunner(),
            new LinuxCommandResolver(),
            new LinuxCapabilityXattrReader());
    }

    public Func<IDisposable?>? BeginAuthorization
    {
        get => _linuxTun.BeginAuthorization;
        set => _linuxTun.BeginAuthorization = value;
    }

    public event EventHandler<string>? LogReceived;
    public event EventHandler? StateChanged;

    public bool IsRunning
    {
        get
        {
            lock (_sync) return _process is { HasExited: false };
        }
    }

    public string? ExecutablePath => FindExecutable();
    public bool IsCoreAvailable => ExecutablePath is not null;
    public bool RequiresAdministratorForTun => OperatingSystem.IsWindows() && !IsWindowsAdministrator();

    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        var executable = FindExecutable();
        if (executable is null) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var process = new Process { StartInfo = CreateStartInfo(executable, ["version"]) };
        try
        {
            if (!process.Start()) return null;
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = string.Join(Environment.NewLine,
                await outputTask.ConfigureAwait(false),
                await errorTask.ConfigureAwait(false));
            const string prefix = "sing-box version ";
            var versionLine = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (versionLine is null) return null;
            var version = versionLine.Trim()[prefix.Length..].Trim();
            return version.TrimStart('v', 'V');
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
            return null;
        }
    }

    public async Task StartAsync(
        string configPath,
        bool requireAdministrator = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning) return;
        var executable = FindExecutable() ?? throw new FileNotFoundException(
            "未找到 sing-box 核心。请将核心放入应用目录的 core 文件夹，或加入系统 PATH。");
        await ValidateAsync(executable, configPath, cancellationToken).ConfigureAwait(false);

        if (requireAdministrator && RequiresAdministratorForTun)
            await StartElevatedAsync(executable, configPath, cancellationToken).ConfigureAwait(false);
        else if (requireAdministrator && OperatingSystem.IsLinux())
        {
            var launch = await _linuxTun.PrepareAsync(executable, cancellationToken).ConfigureAwait(false);
            if (launch.ViaPkexec)
                await StartLinuxPkexecAsync(launch.PkexecPath!, launch.Executable, configPath, cancellationToken).ConfigureAwait(false);
            else
                StartTracked(launch.Executable, ["run", "-c", configPath], null);
        }
        else
            StartTracked(executable, ["run", "-c", configPath], null);

        if (requireAdministrator)
            await FlushSystemDnsCacheAsync(cancellationToken).ConfigureAwait(false);
    }

    private void StartTracked(string executable, IReadOnlyList<string> arguments, StringBuilder? errorCapture)
    {
        StartTracked(executable, arguments, errorCapture, null);
    }

    private Process StartTracked(
        string executable,
        IReadOnlyList<string> arguments,
        StringBuilder? errorCapture,
        string? workingDirectory)
    {
        var info = CreateStartInfo(executable, arguments, workingDirectory);
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) Interlocked.Exchange(ref _pkexecOutputSeen, 1);
            PublishLog(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                if (errorCapture is not null)
                {
                    lock (errorCapture) errorCapture.AppendLine(e.Data);
                }
                Interlocked.Exchange(ref _pkexecOutputSeen, 1);
            }
            PublishLog(e.Data);
        };
        process.Exited += (_, _) =>
        {
            PublishLog($"sing-box 已退出，代码 {process.ExitCode}");
            StateChanged?.Invoke(this, EventArgs.Empty);
        };
        if (!process.Start()) throw new InvalidOperationException("无法启动 sing-box 核心");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        lock (_sync)
        {
            _process = process;
            _elevatedSession = false;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
        return process;
    }

    private async Task StartLinuxPkexecAsync(
        string pkexecPath,
        string executable,
        string configPath,
        CancellationToken cancellationToken)
    {
        var error = new StringBuilder();
        Interlocked.Exchange(ref _pkexecOutputSeen, 0);
        Process process;
        using (BeginAuthorization?.Invoke())
        {
            process = StartTracked(pkexecPath, [executable, "run", "-c", configPath], error, _dataDirectory);
            try
            {
                await WaitForPkexecHandoffAsync(process, error, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await StopAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    private async Task WaitForPkexecHandoffAsync(Process process, StringBuilder error, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < TimeSpan.FromMinutes(2))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                throw CreatePkexecException(process, error);
            }

            if (TryReadComm(process.Id, out var command))
            {
                if (command.Equals("sing-box", StringComparison.Ordinal)) return;
            }
            else if (Interlocked.CompareExchange(ref _pkexecOutputSeen, 0, 0) == 1 && !LooksLikePkexec(error))
            {
                return;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        throw new TunAuthorizationException(TunAuthorizationException.Failed("等待授权超时"), cancelled: false);
    }

    private static TunAuthorizationException CreatePkexecException(Process process, StringBuilder error)
    {
        string text;
        lock (error) text = error.ToString();
        var outcome = LinuxTunAuthorization.ClassifyPkexec(SafeExitCode(process), text, started: true);
        if (outcome == LinuxPkexecOutcome.Cancelled)
            return new TunAuthorizationException(TunAuthorizationException.CancelledMessage, cancelled: true);
        return new TunAuthorizationException(TunAuthorizationException.Failed(text), cancelled: false);
    }

    private static bool TryReadComm(int pid, out string command)
    {
        command = string.Empty;
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            command = File.ReadAllText($"/proc/{pid}/comm").Trim();
            return command.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool LooksLikePkexec(StringBuilder error)
    {
        string text;
        lock (error) text = error.ToString();
        return text.Contains("pkexec", StringComparison.OrdinalIgnoreCase)
               || text.Contains("authorized", StringComparison.OrdinalIgnoreCase)
               || text.Contains("polkit", StringComparison.OrdinalIgnoreCase);
    }

    private async Task StartElevatedAsync(string executable, string configPath, CancellationToken cancellationToken)
    {
        var helperPath = Path.Combine(_moduleDirectory, "AsterDock.NetworkElevatedHost.exe");
        if (!File.Exists(helperPath))
            throw new FileNotFoundException("缺少网络加速权限辅助程序，请重新构建或安装应用。", helperPath);

        var stopSignalPath = GetElevatedStopSignalPath();
        var readySignalPath = GetElevatedReadySignalPath();
        var logPath = GetElevatedLogPath();
        TryDelete(stopSignalPath);
        TryDelete(readySignalPath);
        TryDelete(logPath);

        var info = new ProcessStartInfo(helperPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = _moduleDirectory,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        AddArgument(info, "--core", executable);
        AddArgument(info, "--config", configPath);
        AddArgument(info, "--stop-signal", stopSignalPath);
        AddArgument(info, "--ready-signal", readySignalPath);
        AddArgument(info, "--log", logPath);
        AddArgument(info, "--parent-pid", Environment.ProcessId.ToString());

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.Exited += (_, _) =>
        {
            PublishElevatedLog();
            PublishLog($"TUN 权限辅助程序已退出，代码 {SafeExitCode(process)}");
            StateChanged?.Invoke(this, EventArgs.Empty);
        };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动 TUN 权限辅助程序");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            process.Dispose();
            throw new OperationCanceledException("已取消管理员授权，TUN 模式未启动。", exception);
        }

        lock (_sync)
        {
            _process = process;
            _elevatedSession = true;
        }

        try
        {
            var startedAt = Stopwatch.StartNew();
            while (startedAt.Elapsed < TimeSpan.FromSeconds(12))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(readySignalPath))
                {
                    StateChanged?.Invoke(this, EventArgs.Empty);
                    return;
                }
                if (process.HasExited)
                {
                    PublishElevatedLog();
                    throw new InvalidOperationException(ReadElevatedFailure() ?? "sing-box TUN 启动失败");
                }
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            throw new TimeoutException("等待 sing-box TUN 启动超时");
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync()
    {
        Process? process;
        bool elevatedSession;
        lock (_sync)
        {
            process = _process;
            elevatedSession = _elevatedSession;
        }
        if (process is null) return;
        var canRelease = false;
        try
        {
            if (!process.HasExited)
            {
                if (elevatedSession)
                {
                    await File.WriteAllTextAsync(GetElevatedStopSignalPath(), "stop").ConfigureAwait(false);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    try
                    {
                        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw new TimeoutException("停止 TUN 权限辅助程序超时");
                    }
                }
                else
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            canRelease = true;
        }
        finally
        {
            if (canRelease || process.HasExited)
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_process, process)) _process = null;
                    _elevatedSession = false;
                }
                process.Dispose();
                TryDelete(GetElevatedStopSignalPath());
                TryDelete(GetElevatedReadySignalPath());
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { StopAsync().GetAwaiter().GetResult(); }
        catch { }
    }

    private async Task ValidateAsync(string executable, string configPath, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = CreateStartInfo(executable, ["check", "-c", configPath]) };
        if (!process.Start()) throw new InvalidOperationException("无法启动 sing-box 配置检查");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var message = string.Join(Environment.NewLine, await output.ConfigureAwait(false), await error.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0) throw new InvalidDataException(string.IsNullOrWhiteSpace(message) ? "sing-box 配置检查失败" : message);
    }

    private static ProcessStartInfo CreateStartInfo(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executable)!
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    private async Task FlushSystemDnsCacheAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var executable = Path.Combine(systemDirectory, "ipconfig.exe");
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("/flushdns");
            if (!process.Start()) return;
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var message = string.Join(Environment.NewLine,
                await outputTask.ConfigureAwait(false),
                await errorTask.ConfigureAwait(false)).Trim();
            PublishLog(process.ExitCode == 0
                ? "已清理系统 DNS 缓存"
                : $"清理系统 DNS 缓存失败：{message}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            PublishLog($"清理系统 DNS 缓存失败：{exception.GetBaseException().Message}");
        }
    }

    private string? FindExecutable()
    {
        var fileName = OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box";
        var candidates = new[]
        {
            Path.Combine(_moduleDirectory, "core", fileName),
            Path.Combine(_dataDirectory, "core", fileName),
            Path.Combine(_moduleDirectory, fileName)
        };
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private string GetElevatedStopSignalPath() => Path.Combine(_dataDirectory, "tun-stop.signal");
    private string GetElevatedReadySignalPath() => Path.Combine(_dataDirectory, "tun-ready.signal");
    private string GetElevatedLogPath() => Path.Combine(_dataDirectory, "tun-elevated.log");

    private void PublishElevatedLog()
    {
        try
        {
            if (!File.Exists(GetElevatedLogPath())) return;
            foreach (var line in File.ReadLines(GetElevatedLogPath()).TakeLast(30)) PublishLog(line);
        }
        catch { }
    }

    private string? ReadElevatedFailure()
    {
        try
        {
            if (!File.Exists(GetElevatedLogPath())) return null;
            var line = File.ReadLines(GetElevatedLogPath()).LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
            return line is null ? null : StripAnsi(line);
        }
        catch { return null; }
    }

    private static bool IsWindowsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void AddArgument(ProcessStartInfo info, string name, string value)
    {
        info.ArgumentList.Add(name);
        info.ArgumentList.Add(value);
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch { return -1; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private void PublishLog(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message)) LogReceived?.Invoke(this, StripAnsi(message));
    }

    private static string StripAnsi(string message) =>
        Regex.Replace(message, "\\x1B(?:[@-_]|\\[[0-?]*[ -/]*[@-~])", string.Empty);
}
