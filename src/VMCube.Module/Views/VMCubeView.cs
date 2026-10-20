using System.Text.Json;
using AsterDock.Contracts;
using Avalonia;
using Avalonia.Controls;
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
    private readonly TextBox _engineDirectory = new() { PlaceholderText = "QEMU 引擎目录（留空自动检测）", MinWidth = 220 };
    private readonly WrapPanel _engineControls = new();
    private bool IsLocal => _isLocal;
    private readonly TextBox _uri = new() { Text = "qemu:///system", PlaceholderText = "libvirt 连接 URI", MinWidth = 220 };
    private readonly TextBox _search = new() { PlaceholderText = "搜索虚拟机名称", MinWidth = 100 };
    private readonly ComboBox _filter = new() { ItemsSource = new[] { "全部状态", "运行中", "已关闭", "已暂停" }, SelectedIndex = 0, Width = 112 };
    private readonly StackPanel _details = new() { Spacing = 12 };
    private readonly StackPanel _log = new() { Spacing = 6 };
    private readonly TextBlock _status = new() { Text = "尚未连接 · 输入连接 URI，或打开演示模式", TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _connection = new() { Orientation = Orientation.Horizontal };
    private readonly Button _refresh;
    private readonly Button _checkEngine;
    private readonly TextBlock _engineStatus = new() { Text = "正在检测本机 QEMU 引擎…", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBox _consolePassword = new() { PasswordChar = '●', PlaceholderText = "控制台密码（如已设置）" };
    private readonly ComboBox _consoleProtocol = new() { ItemsSource = new[] { "自动（优先 SPICE）", "VNC", "SPICE" }, SelectedIndex = 0 };
    private readonly Dictionary<string, ConsoleWindow> _consoles = [];
    private List<VirtualMachine> _machines = [];
    private string? _connectedUri;
    private bool _demo, _busy, _disposed;
    private string? _confirmation;
    private string SettingsPath => Path.Combine(_context.DataDirectory, "connection.json");

    public VMCubeView(IApplicationContext context)
    {
        _context = context;
        _local = new LocalVmService(context.DataDirectory);
        try
        {
            _engineDirectory.Text = QemuEngineInstaller.GetInstalledDirectory(context.DataDirectory);
            if (File.Exists(SettingsPath)) _uri.Text = JsonSerializer.Deserialize<string>(File.ReadAllText(SettingsPath)) ?? _uri.Text;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { _status.Text = "连接配置读取失败，请重新输入 URI。"; }
        _refresh = ActionButton("刷新", RefreshAsync); _refresh.IsEnabled = false;
        _checkEngine = ActionButton("检查引擎", CheckOrInstallEngineAsync);
        BuildInterface();
        _search.TextChanged += (_, _) => Filter();
        _filter.SelectionChanged += (_, _) => Filter();
        UpdateStats(); Filter(); AddLog("VMCUBE 0.6.2 已就绪");
        AttachedToVisualTree += async (_, _) => { if (!_disposed && IsLocal && _connectedUri is null && !_demo) await ExecuteAsync(ConnectAsync); };
    }

    private static TextBlock Text(string value, double size = 14, bool muted = false)
    {
        var block = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap };
        block.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(muted ? "AppMutedBrush" : "AppStrongTextBrush"));
        return block;
    }
    private static Border Card(Control content)
    {
        var border = new Border { Child = content, Padding = new Thickness(18), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
        ApplyTheme(border, Border.BackgroundProperty, "AppSurfaceBrush"); ApplyTheme(border, Border.BorderBrushProperty, "AppBorderBrush");
        return border;
    }
    private Button ActionButton(string label, Func<Task> action, bool primary = false)
    {
        var button = new Button { Content = label, Padding = new Thickness(14, 9), CornerRadius = new CornerRadius(8) };
        if (primary) button.Classes.Add("ad-primary");
        button.Click += async (_, _) => await ExecuteAsync(action);
        return button;
    }
    private Button IconActionButton(string label, string icon, Func<Task> action, bool primary = false, bool danger = false)
    {
        var button = VMCubeIcons.Button(label, icon, primary, danger);
        button.Click += async (_, _) => await ExecuteAsync(action);
        return button;
    }
    private async Task ExecuteAsync(Func<Task> action)
    {
        if (_busy || _disposed) return;
        _busy = true; _connection.IsEnabled = false; _engineControls.IsEnabled = false; _refresh.IsEnabled = false; _libraryActions.IsEnabled = false; RenderMachines();
        try { await action(); }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            if (_disposed) return;
            _confirmation = null;
            _status.Text = "操作失败 · " + ex.Message; AddLog(_status.Text); UpdateStats(); Filter();
        }
        finally
        {
            _busy = false;
            if (!_disposed) { _connection.IsEnabled = true; _engineControls.IsEnabled = true; _refresh.IsEnabled = _demo || _connectedUri is not null; _libraryActions.IsEnabled = true; RenderMachines(); }
        }
    }
    private async Task ConnectAsync()
    {
        if (_demo) { _machines.Clear(); _connectedUri = null; }
        _demo = false; _confirmation = null;
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
        if (vm is null) { RenderEmptyDetails(); return; }
        _details.Children.Clear(); _details.Children.Add(Text("当前虚拟机", 12, true));
        var name = Text(vm.Name, 20); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; ToolTip.SetTip(name, vm.Name); _details.Children.Add(name);
        _details.Children.Add(StateBadge(vm));
        _details.Children.Add(Text($"处理器   {vm.Cpus} vCPU\n最大配置内存   {vm.MemoryGiB:0.#} GiB"));
        if (_connectedUri == "local" && _local.GetDefinition(vm.Id).Imported is { } imported)
        {
            _details.Children.Add(Text("已导入 · " + (imported.FirmwareCodePath is null ? "BIOS" : "UEFI") +
                (imported.SecureBoot ? " / Secure Boot" : "") + (imported.TpmStateDirectory is null ? "" : " / TPM 2.0"), 12, true));
            _details.Children.Add(new Expander { Header = "磁盘与标识", Content = new SelectableTextBlock { Text = "磁盘\n" + imported.DiskPath + "\n\nUUID\n" + vm.Id, FontSize = 12, TextWrapping = TextWrapping.Wrap } });
        }
        var actions = new WrapPanel { IsEnabled = !_busy, ItemSpacing = 8, LineSpacing = 8 };
        if (vm.State == "shut off")
        {
            actions.Children.Add(IconActionButton("启动虚拟机", "play", () => ActAsync(vm, "start"), true));
            if (_connectedUri == "local" && _local.GetDefinition(vm.Id).IsoPath is not null) actions.Children.Add(IconActionButton("移除安装镜像", "eject", async () => { await _local.DetachIsoAsync(vm.Id, _lifetime.Token); AddLog(vm.Name + " · 已移除安装 ISO，磁盘保留"); }));
        }
        if (vm.State == "running")
        {
            actions.Children.Add(IconActionButton("连接桌面", "connect", () => ConnectConsoleAsync(vm), true));
            actions.Children.Add(IconActionButton("暂停虚拟机", "pause", () => ActAsync(vm, "suspend")));
            actions.Children.Add(IconActionButton("正常关机", "power", () => { _confirmation = vm.Id; RenderDetails(); return Task.CompletedTask; }));
        }
        if (vm.State == "paused") actions.Children.Add(IconActionButton("恢复运行", "play", () => ActAsync(vm, "resume"), true));
        if (_connectedUri == "local" && vm.State is "running" or "paused")
            actions.Children.Add(IconActionButton("强制关机（相当于断电）", "stop", () => { _confirmation = "force/" + vm.Id; RenderDetails(); return Task.CompletedTask; }, danger: true));
        if (vm.State == "running" && !_demo && _connectedUri != "local")
        {
            _details.Children.Add(_consoleProtocol);
            _details.Children.Add(_consolePassword);
            _details.Children.Add(Text("进入虚拟机 SPICE/VNC 控制台，密码仅用于本次连接。", 12, true));
        }
        _details.Children.Add(actions);
        if (_confirmation == vm.Id)
        {
            _details.Children.Add(Text("确认向此虚拟机发送正常关机请求？完成时间取决于来宾系统，请刷新确认状态。", 12));
            _details.Children.Add(ActionButton("确认关机", () => ActAsync(vm, "shutdown")));
            _details.Children.Add(ActionButton("取消", () => { _confirmation = null; RenderDetails(); return Task.CompletedTask; }));
        }
        if (_confirmation == "force/" + vm.Id)
        {
            _details.Children.Add(Text("强制关机相当于断电，未保存数据可能丢失。确认停止此虚拟机？", 12));
            _details.Children.Add(ActionButton("确认强制关机", () => ActAsync(vm, "destroy")));
            _details.Children.Add(ActionButton("取消", () => { _confirmation = null; RenderDetails(); return Task.CompletedTask; }));
        }
        if (_connectedUri == "local") _details.Children.Add(Text("关闭控制台或退出 VMCUBE 后，虚拟机会继续运行；请通过正常关机停止。", 12, true));
        _details.Children.Add(Text(_demo ? "演示操作不会连接真实服务器。" : "操作后读取实际状态。正常关机请求可能不会立即完成。", 12, true));
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
            OpenConsole(key, vm.Name, options, uri == "local" ? null : token => ConsoleTunnel.OpenAsync(uri, endpoint, token), endpoint.Protocol, endpoint.UseTls);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) { if (!_disposed) { _status.Text = "控制台连接失败 · " + ex.Message; AddLog(_status.Text); } }
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
        var dialog = new Window { Title = "创建本机虚拟机 · VMCUBE", Width = 490, Height = 620, MinWidth = 390, MinHeight = 480 };
        ConfigureTheme(dialog);
        var name = new TextBox { PlaceholderText = "虚拟机名称", Text = "我的虚拟机" };
        var cpus = new NumericUpDown { Minimum = 1, Maximum = 64, Value = 2 };
        var memory = new NumericUpDown { Minimum = 256, Maximum = 262144, Increment = 256, Value = 2048 };
        var disk = new NumericUpDown { Minimum = 1, Maximum = 2048, Value = 32 };
        var supportsSpice = await _local.SupportsSpiceAsync(_lifetime.Token);
        var protocol = new ComboBox { ItemsSource = new[] { "SPICE", "VNC" }, SelectedIndex = supportsSpice ? 0 : 1 };
        var iso = new TextBox { PlaceholderText = "安装 ISO 路径（可选）" };
        var chooseIso = new Button { Content = "选择安装镜像" };
        chooseIso.Click += async (_, _) =>
        {
            var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择安装镜像", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("ISO 镜像") { Patterns = ["*.iso"] }] });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) iso.Text = path;
        };
        var error = Text("", 12); var create = new Button { Content = "创建虚拟机" }; create.Classes.Add("ad-primary");
        create.Click += async (_, _) =>
        {
            create.IsEnabled = false;
            try
            {
                var vm = await _local.CreateAsync(name.Text ?? "", (int)(cpus.Value ?? 2), (int)(memory.Value ?? 2048), (int)(disk.Value ?? 32), iso.Text?.Trim(), protocol.SelectedIndex == 0 ? ConsoleProtocol.Spice : ConsoleProtocol.Vnc, _lifetime.Token);
                dialog.Close(vm);
            }
            catch (Exception ex) { if (!_disposed) error.Text = ex.Message; }
            finally { create.IsEnabled = true; }
        };
        dialog.Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(24), Spacing = 9, Children = {
            Text("创建本机虚拟机", 22), name, Text("处理器 / vCPU", 12, true), cpus, Text("内存 / MiB", 12, true), memory,
            Text("虚拟磁盘 / GiB（按需占用空间）", 12, true), disk, Text(supportsSpice ? "显示协议" : "显示协议（当前引擎支持 VNC）", 12, true), protocol, iso, chooseIso,
            Text("x86_64 虚拟机。优先使用系统硬件加速，无法使用时以软件模拟运行。", 12, true), error, create } } };
        var result = await _context.Windows.ShowDialogAsync<LocalVmDefinition?>(dialog);
        if (result is not null && !_disposed) { AddLog(result.Name + " · 本机虚拟机已创建"); await ConnectAsync(); }
    }
    private async Task ImportLocalVmAsync()
    {
        _local.EngineDirectory = _engineDirectory.Text?.Trim();
        var result = await _context.Windows.ShowDialogAsync<LocalVmDefinition?>(BuildImportDialog());
        if (result is not null && !_disposed) { AddLog(result.Name + " · 已导入，保留原有磁盘与固件状态"); await ConnectAsync(); }
    }
    private Window BuildImportDialog()
    {
        var dialog = new Window { Title = "导入现有虚拟机 · VMCUBE", Width = 620, Height = 800, MinWidth = 430, MinHeight = 520 };
        ConfigureTheme(dialog);
        var name = new TextBox { Name = "ImportName", Text = "现有虚拟机", PlaceholderText = "虚拟机名称" };
        var cpus = new NumericUpDown { Minimum = 1, Maximum = 64, Value = 4 };
        var memory = new NumericUpDown { Minimum = 256, Maximum = 262144, Increment = 256, Value = 8192 };
        var disk = new TextBox { Name = "ImportDisk", PlaceholderText = "现有 qcow2 磁盘路径" };
        var qemu = new TextBox { PlaceholderText = "原有 qemu-system-x86_64（可选，留空使用当前引擎）" };
        var firmware = new TextBox { PlaceholderText = "UEFI 固件 CODE 文件（BIOS 留空）" };
        var nvram = new TextBox { PlaceholderText = "原有 UEFI VARS / NVRAM 文件" };
        var tpm = new TextBox { PlaceholderText = "原有 TPM 2.0 状态目录（可选）" };
        var swtpm = new TextBox { PlaceholderText = "swtpm 程序路径（使用 TPM 时必填）" };
        var secure = new CheckBox { Content = "保留 Secure Boot" };
        var host = new CheckBox { Content = "使用宿主 CPU 型号" };
        var kvm = new CheckBox { Content = "要求 KVM 硬件加速" };
        var localtime = new CheckBox { Content = "硬件时钟使用本地时间（Windows）" };
        var network = new ComboBox { ItemsSource = new[] { "e1000", "e1000e", "virtio-net-pci" }, SelectedIndex = 0 };
        var video = new ComboBox { ItemsSource = new[] { "std", "qxl", "virtio" }, SelectedIndex = 0 };
        var mac = new TextBox { PlaceholderText = "原有 MAC 地址（可选）" };
        var guestAgent = new TextBox { PlaceholderText = "原有 QEMU Guest Agent socket 路径（可选）" };
        var protocol = new ComboBox { ItemsSource = new[] { "SPICE", "VNC" }, SelectedIndex = 0 };
        var error = Text("", 12);
        var profileSummary = Text("可直接选择磁盘填写配置，或读取 .vmcube.json 导入配置。", 12, true);
        async Task PickFileAsync(TextBox target, string title, string pattern = "*")
        {
            var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(title) { Patterns = [pattern] }] });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) target.Text = path;
        }
        Control FileField(string label, TextBox box, string pattern = "*")
        {
            var choose = new Button { Content = "选择文件", HorizontalAlignment = HorizontalAlignment.Left };
            choose.Click += async (_, _) => { try { await PickFileAsync(box, label, pattern); } catch (Exception ex) { error.Text = ex.Message; } };
            return new StackPanel { Spacing = 5, Children = { Text(label, 12, true), box, choose } };
        }
        var load = new Button { Content = "读取导入配置…" };
        load.Click += async (_, _) =>
        {
            try
            {
                var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择 VMCUBE 导入配置", AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("VMCUBE 配置") { Patterns = ["*.vmcube.json"] }] });
                if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
                var p = (await LocalVmImport.ReadProfileAsync(path, _lifetime.Token)).Validate();
                name.Text = p.Name; cpus.Value = p.Cpus; memory.Value = p.MemoryMiB; disk.Text = p.DiskPath; qemu.Text = p.QemuPath;
                firmware.Text = p.FirmwareCodePath; nvram.Text = p.NvramPath; tpm.Text = p.TpmStateDirectory; swtpm.Text = p.SwtpmPath;
                secure.IsChecked = p.SecureBoot; host.IsChecked = p.CpuHost; kvm.IsChecked = p.RequireKvm; localtime.IsChecked = p.UseLocalTime;
                network.SelectedIndex = Array.IndexOf(new[] { "e1000", "e1000e", "virtio-net-pci" }, p.NetworkModel);
                video.SelectedIndex = Array.IndexOf(new[] { "std", "qxl", "virtio" }, p.VideoModel); mac.Text = p.MacAddress; guestAgent.Text = p.GuestAgentSocket;
                profileSummary.Text = p.Summary; error.Text = "";
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        var chooseTpm = new Button { Content = "选择 TPM 状态目录" };
        chooseTpm.Click += async (_, _) =>
        {
            try
            {
                var folders = await dialog.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择原有 TPM 状态目录", AllowMultiple = false });
                if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) tpm.Text = path;
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        string? Optional(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();
        var import = new Button { Name = "ImportSubmit", Content = "导入虚拟机", HorizontalAlignment = HorizontalAlignment.Left }; import.Classes.Add("ad-primary");
        import.Click += async (_, _) =>
        {
            import.IsEnabled = false; load.IsEnabled = false;
            try
            {
                var profile = new LocalVmImport(name.Text ?? "", (int)(cpus.Value ?? 4), (int)(memory.Value ?? 8192), disk.Text?.Trim() ?? "",
                    Optional(qemu), Optional(firmware), Optional(nvram), Optional(tpm), Optional(swtpm), secure.IsChecked == true, host.IsChecked == true,
                    kvm.IsChecked == true, localtime.IsChecked == true, (string)network.SelectedItem!, (string)video.SelectedItem!, Optional(mac), Optional(guestAgent));
                var vm = await _local.ImportAsync(profile, protocol.SelectedIndex == 0 ? ConsoleProtocol.Spice : ConsoleProtocol.Vnc, _lifetime.Token);
                dialog.Close(vm);
            }
            catch (Exception ex) { error.Text = ex.Message; }
            finally { import.IsEnabled = true; load.IsEnabled = true; }
        };
        var form = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(24), Spacing = 10, Children = {
            Text("导入现有虚拟机", 22), Text("直接使用原有磁盘。请先关闭原虚拟机；导入不会格式化或重装系统。", 12, true), load, profileSummary,
            name, Text("处理器 / vCPU", 12, true), cpus, Text("内存 / MiB", 12, true), memory,
            FileField("现有虚拟磁盘", disk, "*.qcow2"), Text("桌面连接协议", 12, true), protocol,
            new Expander { Header = "原有硬件配置（UEFI / TPM / QEMU）", IsExpanded = true, Content = new StackPanel { Spacing = 10, Children = {
                Text("UEFI 或加密的 Windows 请保留原有固件、NVRAM 和 TPM 状态。", 12, true),
                FileField("原有 QEMU 程序", qemu), FileField("UEFI CODE 固件", firmware), FileField("原有 UEFI NVRAM", nvram), secure,
                tpm, chooseTpm, FileField("swtpm 程序", swtpm), host, kvm, localtime, Text("原有网卡型号", 12, true), network, mac, Text("原有显卡型号", 12, true), video, guestAgent } } } } } };
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        layout.Children.Add(form);
        var footer = new StackPanel { Margin = new Thickness(24, 8, 24, 18), Spacing = 8, Children = { error, import } };
        Grid.SetRow(footer, 1); layout.Children.Add(footer); dialog.Content = layout;
        return dialog;
    }
    private void OpenConsole(string key, string name, LocalDesktopOptions options, Func<CancellationToken, Task<ConsoleTunnel>>? tunnel = null, ConsoleProtocol consoleProtocol = ConsoleProtocol.Vnc, bool useTls = false, string? caFile = null)
    {
        if (_consoles.TryGetValue(key, out var existing))
        {
            if (!existing.Completion.IsCompleted) { existing.Activate(); return; }
            existing.Close();
        }
        var window = new ConsoleWindow(name, options, tunnel, consoleProtocol, useTls, caFile);
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
    private async Task ActAsync(VirtualMachine vm, string action)
    {
        _confirmation = null;
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
    private void AddLog(string message)
    {
        _log.Children.Insert(0, Text($"{DateTimeOffset.Now:HH:mm:ss}  {message}", 12, true));
        while (_log.Children.Count > 20) _log.Children.RemoveAt(_log.Children.Count - 1);
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
