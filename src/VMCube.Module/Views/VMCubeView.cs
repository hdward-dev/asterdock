using System.Text.Json;
using AsterDock.Contracts;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using VMCube.Module.Services;
using URemote.Core;
using Avalonia.Platform.Storage;

namespace VMCube.Module.Views;

public sealed partial class VMCubeView : UserControl, IDisposable
{
    private readonly IApplicationContext _context;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly LibvirtService _service = new();
    private readonly LocalVmService _local;
    private bool IsLocal => _isLocal;
    private readonly Dictionary<string, ConsoleWindow> _consoles = [];
    private List<VirtualMachine> _machines = [];
    private string? _connectedUri;
    private bool _demo, _busy, _disposed;
    private string SettingsPath => Path.Combine(_context.DataDirectory, "connection.json");

    public VMCubeView()
    {
        InitializeComponent();
        _context = null!;
        _local = null!;
    }

    public VMCubeView(IApplicationContext context, bool connectWhenShown = true) : this()
    {
        _context = context;
        _local = new LocalVmService(context.DataDirectory);
        try
        {
            _engineDirectory.Text = QemuEngineInstaller.GetInstalledDirectory(context.DataDirectory);
            if (File.Exists(SettingsPath)) _uri.Text = JsonSerializer.Deserialize<string>(File.ReadAllText(SettingsPath)) ?? _uri.Text;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _status.Text = "连接配置读取失败，请重新输入 URI。";
            _context.Feedback.Notify(FeedbackKind.Error, _status.Text, "连接配置");
        }
        _log.ItemsSource = _logItems;
        WireInterface();
        _search.TextChanged += (_, _) => Filter();
        _filter.SelectionChanged += (_, _) => Filter();
        UpdateStats(); Filter(); AddLog("VMCUBE 0.7.1 已就绪");
        if (connectWhenShown)
            AttachedToVisualTree += async (_, _) => { if (!_disposed && IsLocal && _connectedUri is null && !_demo) await ExecuteAsync(ConnectAsync); };
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await ExecuteAsync(RefreshAsync);
    private async void CheckEngine_Click(object? sender, RoutedEventArgs e) => await ExecuteAsync(CheckOrInstallEngineAsync);
    private async void InstallSpice_Click(object? sender, RoutedEventArgs e) => await ExecuteAsync(InstallSpiceComponentAsync);
    private async void ChooseEngine_Click(object? sender, RoutedEventArgs e) => await ExecuteAsync(ChooseEngineAsync);
    private async void CreateVm_Click(object? sender, RoutedEventArgs e) => await ExecuteAsync(CreateLocalVmAsync);
    private async void ImportVm_Click(object? sender, RoutedEventArgs e) => await ExecuteAsync(ImportLocalVmAsync);
    private async void ConnectHost_Click(object? sender, RoutedEventArgs e) => await ExecuteAsync(ConnectAsync);
    private async void ExitDemo_Click(object? sender, RoutedEventArgs e) => await ExecuteAsync(ConnectAsync);
    private async void Demo_Click(object? sender, RoutedEventArgs e) => await ExecuteAsync(DemoAsync);

    private async Task ExecuteAsync(Func<Task> action)
    {
        if (_busy || _disposed) return;
        _busy = true; _connection.IsEnabled = false; _engineControls.IsEnabled = false; _spiceControls.IsEnabled = false; _refresh.IsEnabled = false; _libraryActions.IsEnabled = false; RenderMachines();
        try { await action(); }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            if (_disposed) return;
            _status.Text = "操作失败 · " + ex.Message; AddLog(_status.Text); UpdateStats(); Filter();
            _context.Feedback.Report("操作失败", ex);
        }
        finally
        {
            _busy = false;
            if (!_disposed) { _connection.IsEnabled = true; _engineControls.IsEnabled = true; _spiceControls.IsEnabled = true; _refresh.IsEnabled = _demo || _connectedUri is not null; _libraryActions.IsEnabled = true; RenderMachines(); }
        }
    }
    private async Task ConnectAsync()
    {
        if (_demo) { _machines.Clear(); _connectedUri = null; }
        _demo = false;
        if (IsLocal)
        {
            RefreshEngineStatus();
            await _local.MigrateRegistryAsync(_lifetime.Token);
            _machines = await _local.ReadAsync(_lifetime.Token);
            if (_disposed) return;
            _connectedUri = "local"; _status.Text = _machines.Count == 0 ? "本机工作区已就绪 · 创建或导入你的第一台虚拟机" : "本机工作区已连接 · 选择虚拟机连接桌面或管理运行状态";
            UpdateStats(); Filter(); return;
        }
        var uri = _uri.Text?.Trim() ?? "";
        _status.Text = "正在连接…";
        var machines = await _service.ReadAsync(uri, _lifetime.Token);
        if (_disposed) return;
        _machines = machines; _connectedUri = uri;
        _status.Text = "已连接 · " + uri; AddLog("连接成功 · " + uri);
        UpdateStats(); Filter();
        try { Directory.CreateDirectory(_context.DataDirectory); await File.WriteAllTextAsync(SettingsPath, JsonSerializer.Serialize(uri), _lifetime.Token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AddLog("连接成功，但无法保存连接配置：" + ex.Message); }
    }
    private Task DemoAsync()
    {
        _demo = true; _connectedUri = null;
        _machines = [new(Guid.NewGuid().ToString(), "ubuntu-dev-01", "running", 4, 8),
            new(Guid.NewGuid().ToString(), "debian-services", "running", 2, 4),
            new(Guid.NewGuid().ToString(), "windows-lab", "shut off", 8, 16),
            new(Guid.NewGuid().ToString(), "test-sandbox", "paused", 2, 2)];
        _status.Text = "演示模式 · 所有数据和操作均为模拟"; AddLog("进入演示模式"); ShowPage(IsLocal ? 0 : 1); UpdateStats(); Filter(); return Task.CompletedTask;
    }
    private async Task RefreshAsync()
    {
        if (!_demo && _connectedUri is not null)
        {
            var machines = _connectedUri == "local" ? await _local.ReadAsync(_lifetime.Token) : await _service.ReadAsync(_connectedUri, _lifetime.Token);
            if (_disposed) return;
            _machines = machines;
        }
        UpdateStats(); Filter(); AddLog("列表已刷新" + (_demo ? "（演示）" : ""));
    }
    private void UpdateStats()
    {
        _catalogHint.Text = (_demo ? "演示模式" : IsLocal ? "本机工作区" : _connectedUri ?? "通过 SSH 连接你的 KVM 主机") +
            $" · {_machines.Count(x => x.State == "running")} 台运行中 · 配置内存 {_machines.Sum(x => x.MemoryGiB):0.#} GiB";
        UpdateWorkspaceDisplay();
    }
    private void Filter() => RenderMachines();
    private void RenderDetails()
    {
        var vm = _machines.FirstOrDefault(x => x.Id == _selectedVmId);
        _details.Children.Clear();
        if (vm is null)
        {
            _details.Children.Add(new VmDetailsView { DataContext = new VmDetailsModel { IsEmpty = true } });
            return;
        }
        var imported = _connectedUri == "local" ? _local.GetDefinition(vm.Id).Imported : null;
        var showRemote = vm.State == "running" && !_demo && _connectedUri != "local";
        var view = new VmDetailsView { DataContext = new VmDetailsModel {
            Name = vm.Name,
            Spec = $"处理器   {vm.Cpus} vCPU\n最大配置内存   {vm.MemoryGiB:0.#} GiB",
            StateText = "●  " + vm.StateLabel,
            IsRunning = vm.State == "running",
            IsPaused = vm.State == "paused",
            ShowImported = imported is not null,
            ImportedSummary = imported is null ? "" : "已导入 · " + (imported.FirmwareCodePath is null ? "BIOS" : "UEFI") +
                (imported.SecureBoot ? " / Secure Boot" : "") + (imported.TpmStateDirectory is null ? "" : " / TPM 2.0"),
            DiskText = imported is null ? "" : "磁盘\n" + imported.DiskPath + "\n\nUUID\n" + vm.Id,
            ShowRemoteConsole = showRemote,
            ShowStart = vm.State == "shut off",
            ShowEject = vm.State == "shut off" && _connectedUri == "local" && _local.GetDefinition(vm.Id).IsoPath is not null,
            ShowConnect = vm.State == "running",
            ShowPause = vm.State == "running",
            ShowShutdown = vm.State == "running",
            ShowResume = vm.State == "paused",
            ShowForce = _connectedUri == "local" && vm.State is "running" or "paused",
            ActionsEnabled = !_busy,
            ShowLocalNote = _connectedUri == "local",
            Footer = _demo ? "演示操作不会连接真实服务器。" : "操作后读取实际状态。正常关机请求可能不会立即完成。",
            Start = () => ExecuteAsync(() => ActAsync(vm, "start")),
            Eject = () => ExecuteAsync(async () => { await _local.DetachIsoAsync(vm.Id, _lifetime.Token); AddLog(vm.Name + " · 已移除安装 ISO，磁盘保留"); }),
            Connect = () => ExecuteAsync(() => ConnectConsoleAsync(vm)),
            Pause = () => ExecuteAsync(() => ActAsync(vm, "suspend")),
            Shutdown = () => ExecuteAsync(() => ConfirmPowerAsync(vm, force: false)),
            Resume = () => ExecuteAsync(() => ActAsync(vm, "resume")),
            Force = () => ExecuteAsync(() => ConfirmPowerAsync(vm, force: true)),
        } };
        _details.Children.Add(view);
        if (showRemote)
        {
            if (_consoleProtocol.Parent is Panel protocolParent) protocolParent.Children.Remove(_consoleProtocol);
            if (_consolePassword.Parent is Panel passwordParent) passwordParent.Children.Remove(_consolePassword);
            view.ConsoleHost.Children.Add(_consoleProtocol);
            view.ConsoleHost.Children.Add(_consolePassword);
        }
    }
    private async Task ConnectConsoleAsync(VirtualMachine vm)
    {
        if (_demo) { AddLog("演示虚拟机没有真实桌面，请连接 KVM 主机或使用直接连接桌面。"); return; }
        if (_connectedUri is not { } uri) return;
        var key = uri + "/" + vm.Id;
        if (_consoles.TryGetValue(key, out var existing) && existing.IsConnected) { existing.Activate(); return; }
        try
        {
            var localConsole = uri == "local" ? await _local.GetConsoleAsync(vm.Id, _lifetime.Token) : default;
            var endpoint = uri == "local" ? localConsole.Endpoint : await _service.GetConsoleAsync(uri, vm.Id, _lifetime.Token, _consoleProtocol.SelectedIndex switch { 1 => ConsoleProtocol.Vnc, 2 => ConsoleProtocol.Spice, _ => null });
            if (_disposed) return;
            var options = new LocalDesktopOptions(LocalDesktopProtocol.Vnc, endpoint.Host, endpoint.Port, Password: uri == "local" ? localConsole.Password : _consolePassword.Text ?? "");
            _consolePassword.Text = "";
            if (endpoint.UseTls && uri != "local" && new Uri(uri).Scheme == "qemu+ssh")
                throw new NotSupportedException("此 SPICE 控制台仅开放 TLS 端口，请用“直接连接桌面”填写证书对应主机名和 TLS 端口，避免隧道地址导致主机名校验失败。");
            if (endpoint.Protocol == ConsoleProtocol.Spice && !await EnsureSpiceComponentAsync()) return;
            OpenConsole(key, vm.Name, options, uri == "local" ? null : token => ConsoleTunnel.OpenAsync(uri, endpoint, token), endpoint.Protocol, endpoint.UseTls);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (SpiceComponentMissingException ex) { if (!_disposed) { _status.Text = ex.Message; AddLog(ex.Message); RefreshSpiceStatus(); _ = _context.Feedback.ShowWarningAsync("缺少 SPICE 组件", ex.Message); } }
        catch (Exception ex) { if (!_disposed) { _status.Text = "控制台连接失败 · " + ex.Message; AddLog(_status.Text); _context.Feedback.Report("控制台连接失败", ex); } }
    }
    private bool RefreshEngineStatus()
    {
        _local.EngineDirectory = _engineDirectory.Text?.Trim();
        try
        {
            var system = _local.ResolveEngine("qemu-system-x86_64");
            var image = _local.ResolveEngine("qemu-img");
            var module = Path.GetDirectoryName(typeof(LocalVmService).Assembly.Location)!;
            var bundled = system.StartsWith(Path.Combine(module, "native") + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            _engineStatus.Text = bundled ? "QEMU 已就绪 · 使用应用内置引擎，无需另行安装" : "QEMU 已就绪 · 使用已安装的本机引擎";
            ToolTip.SetTip(_engineStatus, $"QEMU：{system}\n磁盘工具：{image}");
            _checkEngine.Content = "重新检测引擎";
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _engineStatus.Text = ex.Message; ToolTip.SetTip(_engineStatus, null); _checkEngine.Content = "安装本机引擎";
            return false;
        }
    }
    private async Task CheckOrInstallEngineAsync()
    {
        if (RefreshEngineStatus())
        {
            _status.Text = "本机 QEMU 已就绪，无需重复安装。";
            AddLog(_status.Text); return;
        }
        try
        {
            var installer = new QemuEngineInstaller();
            var directory = await installer.InstallAsync(_context.DataDirectory, new Progress<string>(value => { if (!_disposed) _status.Text = value; }), _lifetime.Token);
            if (!_disposed) { _engineDirectory.Text = directory; RefreshEngineStatus(); AddLog("本机 QEMU 引擎已安装并校验"); }
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) { if (!_disposed) { _status.Text = ex.Message; AddLog(ex.Message); } }
    }
    private bool RefreshSpiceStatus()
    {
        var helper = SpiceDesktopSession.TryFindHelper(_context.DataDirectory);
        if (helper is null)
        {
            _spiceStatus.Text = SpiceDesktopSession.MissingComponentMessage(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);
            ToolTip.SetTip(_spiceStatus, null); _installSpice.Content = "安装 SPICE 组件";
            return false;
        }
        var module = Path.GetDirectoryName(typeof(SpiceDesktopSession).Assembly.Location)!;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var bundled = helper.StartsWith(Path.Combine(module, "native") + Path.DirectorySeparatorChar, comparison)
            || helper.StartsWith(Path.Combine(AppContext.BaseDirectory, "native") + Path.DirectorySeparatorChar, comparison);
        _spiceStatus.Text = bundled ? "SPICE 已就绪 · 使用应用内置组件，无需另行安装" : "SPICE 已就绪 · 使用已安装的本机组件";
        ToolTip.SetTip(_spiceStatus, helper); _installSpice.Content = "重新检测 SPICE";
        return true;
    }
    private async Task<bool> EnsureSpiceComponentAsync()
    {
        if (SpiceDesktopSession.TryFindHelper(_context.DataDirectory) is not null) return true;
        var message = SpiceDesktopSession.MissingComponentMessage(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);
        if (!await _context.Feedback.ConfirmAsync("安装 SPICE 组件", message, "安装 SPICE 组件", "取消"))
        {
            _status.Text = message; AddLog(message); RefreshSpiceStatus(); return false;
        }
        await InstallSpiceComponentAsync();
        if (_disposed) return false;
        if (SpiceDesktopSession.TryFindHelper(_context.DataDirectory) is null)
            throw new SpiceComponentMissingException(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);
        return true;
    }
    private async Task InstallSpiceComponentAsync()
    {
        if (RefreshSpiceStatus())
        {
            _status.Text = "本机 SPICE 组件已就绪，无需重复安装。";
            AddLog(_status.Text); return;
        }
        var installer = new SpiceComponentInstaller();
        var directory = await installer.InstallAsync(_context.DataDirectory, new Progress<string>(value => { if (!_disposed) _status.Text = value; }), _lifetime.Token);
        if (!_disposed) { RefreshSpiceStatus(); AddLog("SPICE 组件已安装并校验 · " + directory); }
    }
    private async Task ChooseEngineAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择 QEMU 引擎所在目录", AllowMultiple = false });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) { _engineDirectory.Text = path; _local.EngineDirectory = path; Directory.CreateDirectory(_context.DataDirectory); await File.WriteAllTextAsync(Path.Combine(_context.DataDirectory, "engine-directory.json"), JsonSerializer.Serialize(path), _lifetime.Token); RefreshEngineStatus(); }
    }
    private async Task CreateLocalVmAsync()
    {
        _local.EngineDirectory = _engineDirectory.Text?.Trim();
        _ = _local.ResolveEngine("qemu-img");
        var supportsSpice = await _local.SupportsSpiceAsync(_lifetime.Token);
        var dialog = new CreateVmWindow(_local, supportsSpice, _lifetime.Token, () => !_disposed, _context.Feedback);
        var result = await _context.Windows.ShowDialogAsync<LocalVmDefinition?>(dialog);
        if (result is not null && !_disposed) { AddLog(result.Name + " · 本机虚拟机已创建"); await ConnectAsync(); }
    }
    private async Task ImportLocalVmAsync()
    {
        _local.EngineDirectory = _engineDirectory.Text?.Trim();
        var result = await _context.Windows.ShowDialogAsync<LocalVmDefinition?>(BuildImportDialog());
        if (result is not null && !_disposed) { AddLog(result.Name + " · 已导入，保留原有磁盘与固件状态"); await ConnectAsync(); }
    }
    private Window BuildImportDialog() => new ImportVmWindow(_local, _lifetime.Token, _context.Feedback);
    private void OpenConsole(string key, string name, LocalDesktopOptions options, Func<CancellationToken, Task<ConsoleTunnel>>? tunnel = null, ConsoleProtocol consoleProtocol = ConsoleProtocol.Vnc, bool useTls = false, string? caFile = null)
    {
        if (_consoles.TryGetValue(key, out var existing))
        {
            if (!existing.Completion.IsCompleted) { existing.Activate(); return; }
            existing.Close();
        }
        var window = new ConsoleWindow(name, options, tunnel, consoleProtocol, useTls, caFile, _context.Feedback, _context.DataDirectory);
        _consoles[key] = window;
        if (!key.StartsWith("direct/", StringComparison.Ordinal)) {
            window.FramePresented += frame => UpdatePreview(key, frame);
            window.ConnectionEnded += () => { if (_consoles.GetValueOrDefault(key) == window) PreviewDisconnected(key); };
        }
        window.Closed += (_, _) => { if (_consoles.GetValueOrDefault(key) == window) { PreviewDisconnected(key); _consoles.Remove(key); } };
        window.Connected += () => { if (!_disposed) AddLog(name + " · 桌面已连接"); };
        _context.Windows.Show(window);
        AddLog(name + " · 正在打开控制台");
    }
    private async Task ConfirmPowerAsync(VirtualMachine vm, bool force)
    {
        var title = force ? "强制关机" : "正常关机";
        var message = force
            ? $"强制关机相当于断电，未保存数据可能丢失。确认停止「{vm.Name}」？"
            : $"确认向「{vm.Name}」发送正常关机请求？完成时间取决于来宾系统，请刷新确认状态。";
        var confirm = force ? "确认强制关机" : "确认关机";
        if (!await _context.Feedback.ConfirmAsync(title, message, confirm, "取消", danger: force)) return;
        await ActAsync(vm, force ? "destroy" : "shutdown");
    }
    private async Task ActAsync(VirtualMachine vm, string action)
    {
        if (_demo)
        {
            var index = _machines.FindIndex(x => x.Id == vm.Id);
            _machines[index] = vm with { State = action switch { "start" or "resume" => "running", "suspend" => "paused", _ => "shut off" } };
        }
        else if (_connectedUri == "local") await _local.ActAsync(vm.Id, action, _lifetime.Token);
        else if (_connectedUri is not null) await _service.ActAsync(_connectedUri, action, vm.Id, _lifetime.Token);
        else throw new InvalidOperationException("请先连接主机");
        if (_disposed) return;
        AddLog($"{vm.Name} · {action} 请求成功" + (_demo ? "（演示）" : "")); await RefreshAsync();
    }
    private readonly System.Collections.ObjectModel.ObservableCollection<string> _logItems = [];
    private void AddLog(string message)
    {
        _logItems.Insert(0, $"{DateTimeOffset.Now:HH:mm:ss}  {message}");
        while (_logItems.Count > 20) _logItems.RemoveAt(_logItems.Count - 1);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var window in _consoles.Values.ToArray()) window.Close();
        _consoles.Clear(); ClearPreviews(); _consolePassword.Text = "";
        _lifetime.Cancel(); _lifetime.Dispose(); _local.Dispose();
    }
}
