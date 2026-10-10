using System.Text.Json;
using AsterDock.Contracts;
using AsterDock.UI.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using VMCube.Module.Services;
using URemote.Core;

namespace VMCube.Module.Views;

public sealed class VMCubeView : UserControl, IDisposable
{
    private readonly IApplicationContext _context;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly LibvirtService _service = new();
    private readonly TextBox _uri = new() { Text = "qemu:///system", PlaceholderText = "libvirt 连接 URI", MinWidth = 220 };
    private readonly TextBox _search = new() { PlaceholderText = "搜索虚拟机名称", MinWidth = 180 };
    private readonly ComboBox _filter = new() { ItemsSource = new[] { "全部状态", "运行中", "已关闭", "已暂停" }, SelectedIndex = 0, MinWidth = 120 };
    private readonly ListBox _list = new() { MinHeight = 180, MaxHeight = 410 };
    private readonly StackPanel _details = new() { Spacing = 12 };
    private readonly WrapPanel _stats = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _log = new() { Spacing = 6 };
    private readonly TextBlock _status = new() { Text = "尚未连接 · 输入连接 URI，或打开演示模式", TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _connection = new() { Orientation = Orientation.Horizontal };
    private readonly Button _refresh;
    private readonly TextBox _consolePassword = new() { PasswordChar = '●', PlaceholderText = "VNC 密码（如已设置）" };
    private readonly Dictionary<string, ConsoleWindow> _consoles = [];
    private List<VirtualMachine> _machines = [];
    private string? _connectedUri;
    private bool _demo, _busy, _disposed;
    private string? _confirmation;
    private string SettingsPath => Path.Combine(_context.DataDirectory, "connection.json");

    public VMCubeView(IApplicationContext context)
    {
        _context = context;
        try
        {
            if (File.Exists(SettingsPath)) _uri.Text = JsonSerializer.Deserialize<string>(File.ReadAllText(SettingsPath)) ?? _uri.Text;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { _status.Text = "连接配置读取失败，请重新输入 URI。"; }
        _connection.Children.Add(_uri);
        _connection.Children.Add(ActionButton("连接", ConnectAsync, true));
        _connection.Children.Add(ActionButton("演示模式", DemoAsync));
        _connection.Children.Add(ActionButton("直接连接 VNC", DirectConsoleAsync));
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal };
        toolbar.Children.Add(_search); toolbar.Children.Add(_filter);
        _refresh = ActionButton("刷新", RefreshAsync); _refresh.IsEnabled = false; toolbar.Children.Add(_refresh);
        _search.TextChanged += (_, _) => Filter();
        _filter.SelectionChanged += (_, _) => Filter();
        _list.SelectionChanged += (_, _) => { _confirmation = null; RenderDetails(); };
        var workspace = new WrapPanel { Orientation = Orientation.Horizontal };
        var inventory = new StackPanel { Spacing = 12, Width = 420 };
        inventory.Children.Add(Text("虚拟机", 18)); inventory.Children.Add(toolbar); inventory.Children.Add(_list);
        workspace.Children.Add(Card(inventory));
        var detailCard = Card(_details); detailCard.Width = 300; workspace.Children.Add(detailCard);
        var body = new StackPanel { Spacing = 18, Margin = new Thickness(24) };
        body.Children.Add(Card(new StackPanel { Spacing = 10, Children = { Text("连接工作区", 16), _connection,
            Text("本机需安装 virsh；SSH 连接需预先配置密钥和主机信任。", 12, true), _status } }));
        body.Children.Add(Card(_stats)); body.Children.Add(workspace);
        body.Children.Add(Card(new StackPanel { Spacing = 10, Children = { Text("操作记录", 16), _log } }));
        var grid = new Grid { RowDefinitions = new RowDefinitions("64,*") };
        grid.Children.Add(new PageHeader { Title = "VMCUBE", Subtitle = "KVM 虚拟机工作区 · 轻量掌控每一台虚拟机" });
        var scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); grid.Children.Add(scroll); Content = grid;
        UpdateStats(); Filter(); AddLog("就绪，等待连接。");
    }

    private static TextBlock Text(string value, double size = 14, bool muted = false)
    {
        var block = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap };
        block.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(muted ? "AppMutedBrush" : "AppStrongTextBrush"));
        return block;
    }
    private static Border Card(Control content)
    {
        var border = new Border { Child = content, Padding = new Thickness(18), Margin = new Thickness(0, 0, 12, 0) };
        border.Classes.Add("ad-card"); return border;
    }
    private Button ActionButton(string label, Func<Task> action, bool primary = false)
    {
        var button = new Button { Content = label, Margin = new Thickness(4), Padding = new Thickness(14, 8) };
        if (primary) button.Classes.Add("ad-primary");
        button.Click += async (_, _) => await ExecuteAsync(action);
        return button;
    }
    private async Task ExecuteAsync(Func<Task> action)
    {
        if (_busy || _disposed) return;
        _busy = true; _connection.IsEnabled = false; _refresh.IsEnabled = false; RenderDetails();
        try { await action(); }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            if (_disposed) return;
            _machines.Clear(); _connectedUri = null; _confirmation = null;
            _status.Text = "操作失败 · " + ex.Message; AddLog(_status.Text); UpdateStats(); Filter();
        }
        finally
        {
            _busy = false;
            if (!_disposed) { _connection.IsEnabled = true; _refresh.IsEnabled = _demo || _connectedUri is not null; RenderDetails(); }
        }
    }
    private async Task ConnectAsync()
    {
        _demo = false; _connectedUri = null; _machines.Clear(); UpdateStats(); Filter();
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
        _status.Text = "演示模式 · 所有数据和操作均为模拟"; AddLog("进入演示模式"); UpdateStats(); Filter(); return Task.CompletedTask;
    }
    private async Task RefreshAsync()
    {
        if (!_demo && _connectedUri is not null)
        {
            var machines = await _service.ReadAsync(_connectedUri, _lifetime.Token);
            if (_disposed) return;
            _machines = machines;
        }
        UpdateStats(); Filter(); AddLog("列表已刷新" + (_demo ? "（演示）" : ""));
    }
    private void UpdateStats()
    {
        _stats.Children.Clear();
        foreach (var (label, value) in new[] { ("全部虚拟机", _machines.Count.ToString()), ("运行中", _machines.Count(x => x.State == "running").ToString()),
            ("已关闭", _machines.Count(x => x.State == "shut off").ToString()), ("配置内存总和", $"{_machines.Sum(x => x.MemoryGiB):0.#} GiB") })
            _stats.Children.Add(new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 24, 8), Children = { Text(label, 12, true), Text(value, 24) } });
    }
    private void Filter()
    {
        var selected = (_list.SelectedItem as ListBoxItem)?.Tag as VirtualMachine;
        var state = _filter.SelectedIndex switch { 1 => "running", 2 => "shut off", 3 => "paused", _ => "" };
        var visible = _machines.Where(x => x.Name.Contains(_search.Text ?? "", StringComparison.OrdinalIgnoreCase) && (state == "" || x.State == state)).ToArray();
        var items = visible.Select(vm => new ListBoxItem { Tag = vm, Content = new StackPanel { Spacing = 5, Margin = new Thickness(6), Children = { Text(vm.Name, 15), Text(vm.Summary, 12, true) } } }).ToArray();
        _list.ItemsSource = items;
        _list.SelectedItem = items.FirstOrDefault(x => ((VirtualMachine)x.Tag!).Id == selected?.Id) ?? items.FirstOrDefault();
        if (items.Length == 0) { _details.Children.Clear(); _details.Children.Add(Text(_connectedUri is null && !_demo ? "连接你的第一台 KVM 主机" : _machines.Count == 0 ? "此连接下还没有虚拟机" : "没有匹配的虚拟机", 18)); }
        else RenderDetails();
    }
    private void RenderDetails()
    {
        var vm = (_list.SelectedItem as ListBoxItem)?.Tag as VirtualMachine;
        if (vm is null) return;
        _details.Children.Clear(); _details.Children.Add(Text("虚拟机详情", 12, true)); _details.Children.Add(Text(vm.Name, 22));
        _details.Children.Add(Text(vm.StateLabel, 16)); _details.Children.Add(Text("UUID\n" + vm.Id, 12, true));
        _details.Children.Add(Text($"处理器   {vm.Cpus} vCPU\n最大配置内存   {vm.MemoryGiB:0.#} GiB"));
        var actions = new WrapPanel { IsEnabled = !_busy };
        if (vm.State == "shut off") actions.Children.Add(ActionButton("启动", () => ActAsync(vm, "start"), true));
        if (vm.State == "running")
        {
            actions.Children.Add(ActionButton("连接桌面", () => ConnectConsoleAsync(vm), true));
            actions.Children.Add(ActionButton("暂停", () => ActAsync(vm, "suspend")));
            actions.Children.Add(ActionButton("正常关机", () => { _confirmation = vm.Id; RenderDetails(); return Task.CompletedTask; }));
        }
        if (vm.State == "paused") actions.Children.Add(ActionButton("恢复运行", () => ActAsync(vm, "resume"), true));
        if (vm.State == "running" && !_demo)
        {
            _details.Children.Add(_consolePassword);
            _details.Children.Add(Text("进入虚拟机 VNC 控制台，无需安装来宾代理。密码仅用于本次连接。", 12, true));
        }
        _details.Children.Add(actions);
        if (_confirmation == vm.Id)
        {
            _details.Children.Add(Text("确认向此虚拟机发送正常关机请求？完成时间取决于来宾系统，请刷新确认状态。", 12));
            _details.Children.Add(ActionButton("确认关机", () => ActAsync(vm, "shutdown")));
            _details.Children.Add(ActionButton("取消", () => { _confirmation = null; RenderDetails(); return Task.CompletedTask; }));
        }
        _details.Children.Add(Text(_demo ? "演示操作不会连接真实服务器。" : "操作后读取实际状态。正常关机请求可能不会立即完成。", 12, true));
    }
    private async Task ConnectConsoleAsync(VirtualMachine vm)
    {
        if (_demo) { AddLog("演示虚拟机没有真实桌面，请连接 KVM 主机或使用直接连接 VNC。"); return; }
        if (_connectedUri is not { } uri) return;
        var key = uri + "/" + vm.Id;
        if (_consoles.TryGetValue(key, out var existing) && existing.IsConnected) { existing.Activate(); return; }
        try
        {
            var endpoint = await _service.GetConsoleAsync(uri, vm.Id, _lifetime.Token);
            if (_disposed) return;
            var options = new LocalDesktopOptions(LocalDesktopProtocol.Vnc, endpoint.Host, endpoint.Port, Password: _consolePassword.Text ?? "");
            _consolePassword.Text = "";
            OpenConsole(key, vm.Name, options, token => ConsoleTunnel.OpenAsync(uri, endpoint, token));
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) { if (!_disposed) { _status.Text = "控制台连接失败 · " + ex.Message; AddLog(_status.Text); } }
    }
    private async Task DirectConsoleAsync()
    {
        var dialog = new Window { Title = "直接连接 VNC · VMCUBE", Width = 440, Height = 390, MinWidth = 360, MinHeight = 340 };
        var host = new TextBox { PlaceholderText = "主机名或 IP 地址" };
        var port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 5900, Increment = 1 };
        var password = new TextBox { PasswordChar = '●', PlaceholderText = "VNC 访问密码（可留空）" };
        var error = Text("", 12);
        var connect = new Button { Content = "连接桌面" }; connect.Classes.Add("ad-primary");
        connect.Click += (_, _) =>
        {
            try
            {
                var options = new LocalDesktopOptions(LocalDesktopProtocol.Vnc, host.Text?.Trim() ?? "", (int)(port.Value ?? 5900), Password: password.Text ?? "");
                options.Validate(); password.Text = ""; dialog.Close(options);
            }
            catch (ArgumentException ex) { error.Text = ex.Message; }
        };
        dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 12, Children = {
            Text("直接进入远程桌面", 20), host, port, password,
            Text("连接到已启用 VNC 的主机。密码不保存；直接 VNC 连接请使用可信局域网或 VPN。", 12, true), error, connect } };
        var options = await _context.Windows.ShowDialogAsync<LocalDesktopOptions?>(dialog);
        if (options is not null && !_disposed) OpenConsole($"direct/{options.Host}:{options.Port}", $"{options.Host}:{options.Port}", options);
    }
    private void OpenConsole(string key, string name, LocalDesktopOptions options, Func<CancellationToken, Task<ConsoleTunnel>>? tunnel = null)
    {
        if (_consoles.TryGetValue(key, out var existing))
        {
            if (!existing.Completion.IsCompleted) { existing.Activate(); return; }
            existing.Close();
        }
        var window = new ConsoleWindow(name, options, tunnel);
        _consoles[key] = window;
        window.Closed += (_, _) => { if (_consoles.GetValueOrDefault(key) == window) _consoles.Remove(key); };
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
        _consoles.Clear(); _consolePassword.Text = "";
        _lifetime.Cancel(); _lifetime.Dispose();
    }
}
