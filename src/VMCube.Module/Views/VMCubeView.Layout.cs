using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using VMCube.Module.Services;
using URemote.Core;

namespace VMCube.Module.Views;

public sealed partial class VMCubeView
{
    private bool _isLocal = true, _listView, _favoritesOnly, _compact;
    private int _cardColumns = 2;
    private string? _selectedVmId;
    private readonly List<Button> _navigation = [], _catalogTabs = [];
    private readonly HashSet<string> _favorites = [];
    private Control[] _pages = [];
    private string FavoritesPath => Path.Combine(_context.DataDirectory, "favorites.json");

    private static void ApplyTheme(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, new DynamicResourceExtension(key));

    private static void SetClass(Control control, string name, bool on)
    {
        if (on) { if (!control.Classes.Contains(name)) control.Classes.Add(name); }
        else control.Classes.Remove(name);
    }

    private static Control Icon(string name, double size = 20) => VMCubeIcons.Draw(name, size);

    private void WireInterface()
    {
        try { if (File.Exists(FavoritesPath)) foreach (var key in JsonSerializer.Deserialize<string[]>(File.ReadAllText(FavoritesPath)) ?? []) _favorites.Add(key); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { AddLog("收藏读取失败：" + ex.Message); }
        _navigation.AddRange([navMachines, navRemote, navDirect, navHistory, navSettings]);
        _catalogTabs.AddRange([tabAll, tabFavorites]);
        _pages = [libraryPage, libraryPage, directPage, logPage, settingsPage];
        Place(_railDetails, _details);
        workspacePath.Text = _context.DataDirectory;
        directTls.IsCheckedChanged += (_, _) => directCa.IsVisible = directProtocol.SelectedIndex == 0 && directTls.IsChecked == true;
        directProtocol.SelectionChanged += (_, _) => { directTls.IsVisible = directProtocol.SelectedIndex == 0; directCa.IsVisible = directProtocol.SelectedIndex == 0 && directTls.IsChecked == true; };
        _status.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) {
            ToolTip.SetTip(_status, _status.Text); ApplyTheme(_status, TextBlock.ForegroundProperty,
                _status.Text?.Contains("失败", StringComparison.Ordinal) == true ? "AppDangerBrush" : "AppMutedBrush"); } };
        SizeChanged += (_, e) => {
            var compact = e.NewSize.Width < 850;
            railBorder.IsVisible = !compact;
            body.ColumnDefinitions = new(compact ? "0,*,76" : e.NewSize.Width >= 1150 ? "280,*,190" : "250,*,170");
            sidebar.Margin = new Thickness(compact ? 8 : 12);
            brandTitle.Text = compact ? "VM" : "VMCUBE"; brandTitle.FontSize = compact ? 22 : 25;
            brandTitle.HorizontalAlignment = HorizontalAlignment.Center; VmBrandLogo.Width = VmBrandLogo.Height = compact ? 28 : 40;
            brandSubtitle.IsVisible = !compact; brand.Margin = new Thickness(0, 12, 0, 26);
            foreach (var button in _navigation) {
                if (button.Content is StackPanel label) foreach (var text in label.Children.OfType<TextBlock>()) text.IsVisible = !compact;
                button.Padding = compact ? new Thickness(12, 13) : new Thickness(14, 13);
                button.HorizontalContentAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            }
            _page.Margin = new Thickness(e.NewSize.Width >= 1150 ? 24 : 16);
            if (_compact != compact) {
                _railDetails.Content = null; _inlineDetails.Content = null;
                if (compact) _inlineDetails.Content = _details; else _railDetails.Content = _details;
                inlineCard.IsVisible = compact;
                _compact = compact;
            }
            var columns = e.NewSize.Width >= 1150 ? 2 : 1;
            if (columns != _cardColumns) { _cardColumns = columns; RenderMachines(); }
        };
        UpdateTabs(); ShowPage(0);
    }

    private async void VmNav_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && int.TryParse(button.Tag?.ToString(), out var index)) await ExecuteAsync(() => NavigateAsync(index));
    }
    private void ViewMode_Click(object? sender, RoutedEventArgs e)
    {
        _listView = !_listView; VmViewMode.Content = _listView ? "网格" : "列表"; RenderMachines();
    }
    private void CatalogTab_Click(object? sender, RoutedEventArgs e)
    {
        _favoritesOnly = sender == tabFavorites; UpdateTabs(); RenderMachines();
    }
    private async void DirectConnect_Click(object? sender, RoutedEventArgs e) => await ExecuteAsync(() =>
    {
        try {
            var options = new LocalDesktopOptions(LocalDesktopProtocol.Vnc, directHost.Text?.Trim() ?? "", (int)(directPort.Value ?? 5900), Password: directPassword.Text ?? ""); options.Validate();
            var endpoint = new ConsoleEndpoint(options.Host, options.Port, directProtocol.SelectedIndex == 0 ? ConsoleProtocol.Spice : ConsoleProtocol.Vnc, directProtocol.SelectedIndex == 0 && directTls.IsChecked == true);
            var ca = directCa.Text?.Trim();
            if (endpoint.UseTls && !string.IsNullOrWhiteSpace(ca) && !File.Exists(ca)) throw new ArgumentException("CA 证书文件不存在。");
            directPassword.Text = ""; directError.Text = "";
            OpenConsole($"direct/{endpoint.Protocol}/{endpoint.Host}:{endpoint.Port}/{endpoint.UseTls}", $"{endpoint.Host}:{endpoint.Port}", options, consoleProtocol: endpoint.Protocol, useTls: endpoint.UseTls, caFile: endpoint.UseTls ? ca : null);
        }
        catch (ArgumentException ex) { directError.Text = ex.Message; }
        return Task.CompletedTask;
    });

    private void UpdateTabs()
    {
        for (var i = 0; i < _catalogTabs.Count; i++) SetClass(_catalogTabs[i], "selected", _favoritesOnly == (i == 1));
    }
    private async Task NavigateAsync(int index)
    {
        if (index is 0 or 1 && IsLocal != (index == 0)) {
            _isLocal = index == 0; _machines.Clear(); _connectedUri = null; _selectedVmId = null; _demo = false; _confirmation = null;
            _consolePassword.Text = "";
            if (!IsLocal && (_uri.Text?.StartsWith("qemu:///", StringComparison.Ordinal) ?? false)) _uri.Text = "qemu+ssh://user@host/system";
        }
        ShowPage(index); UpdateStats(); Filter();
        if (index == 0 && _connectedUri is null && !_demo) await ConnectAsync();
    }
    private static void Place(ContentControl host, Control control)
    {
        if (ReferenceEquals(host.Content, control)) return;
        if (control.Parent is Panel panel) panel.Children.Remove(control);
        host.Content = control;
    }

    private void ShowPage(int index)
    {
        Place(_page, _pages[index]);
        for (var i = 0; i < _navigation.Count; i++) SetClass(_navigation[i], "selected", i == index);
        _remoteConnection.IsVisible = !IsLocal; _libraryActions.IsVisible = IsLocal; UpdateWorkspaceDisplay();
    }
    private void UpdateWorkspaceDisplay()
    {
        _workspaceName.Text = IsLocal ? "本机工作区" : "远程工作区";
        _workspaceState.Text = _demo ? "○  演示模式" : IsLocal ? (_engineStatus.Text?.Contains("已就绪", StringComparison.Ordinal) == true ? "●  QEMU 引擎已就绪" : "○  请在设置中检查引擎") : _connectedUri is null ? "○  等待连接主机" : "●  已连接主机";
        ApplyTheme(_workspaceState, TextBlock.ForegroundProperty, _demo || (!IsLocal && _connectedUri is null) ? "AppMutedBrush" : IsLocal && _engineStatus.Text?.Contains("已就绪", StringComparison.Ordinal) != true ? "AppWarningBrush" : "AppPositiveBrush");
    }
    private string FavoriteKey(VirtualMachine vm) => (_demo ? "demo" : IsLocal ? "local" : _connectedUri ?? _uri.Text) + "/" + vm.Id;
    private async Task ToggleFavoriteAsync(VirtualMachine vm)
    {
        var key = FavoriteKey(vm); if (!_favorites.Add(key)) _favorites.Remove(key);
        RenderMachines();
        try {
            Directory.CreateDirectory(_context.DataDirectory);
            var temp = FavoritesPath + ".tmp"; await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(_favorites), _lifetime.Token);
            File.Move(temp, FavoritesPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AddLog("收藏暂存于本次会话，保存失败：" + ex.Message); }
    }
    private void SelectVm(VirtualMachine vm)
    {
        _selectedVmId = vm.Id; _confirmation = null; _consolePassword.Text = ""; RenderMachines();
    }
    private void RenderMachines()
    {
        var state = _filter.SelectedIndex switch { 1 => "running", 2 => "shut off", 3 => "paused", _ => "" };
        var matches = _machines.Where(x => x.Name.Contains(_search.Text?.Trim() ?? "", StringComparison.OrdinalIgnoreCase) &&
            (state.Length == 0 || x.State == state) && (!_favoritesOnly || _favorites.Contains(FavoriteKey(x))))
            .OrderByDescending(x => x.State == "running").ThenBy(x => x.Name, StringComparer.CurrentCulture).ToArray();
        _catalogHeading.Text = (IsLocal ? "我的虚拟机" : "远程虚拟机") + (_machines.Count == 0 ? "" : "  " + matches.Length);
        _demoBanner.IsVisible = _demo;
        _previewTargets.Clear();
        _machineCards.Children.Clear();
        var columns = _listView ? 1 : _cardColumns;
        _machineCards.ColumnDefinitions = new(columns == 2 && matches.Length > 0 ? "*,*" : "*");
        _machineCards.RowDefinitions = new(string.Join(",", Enumerable.Repeat("Auto", Math.Max(1, (matches.Length + columns - 1) / columns))));
        if (!matches.Any(x => x.Id == _selectedVmId)) { _selectedVmId = matches.FirstOrDefault()?.Id; _confirmation = null; _consolePassword.Text = ""; }
        if (matches.Length == 0) {
            var empty = new StackPanel { Spacing = 12, Margin = new Thickness(0, 24), HorizontalAlignment = HorizontalAlignment.Stretch,
                Children = { Icon("cube", 42), Text(_machines.Count == 0 ? IsLocal ? "创建你的第一台虚拟机" : "连接你的 KVM 主机" : "没有符合条件的虚拟机", 18),
                Text(_machines.Count == 0 ? IsLocal ? "创建新虚拟机，或导入已有的 qcow2 磁盘。" : "填写 SSH 连接地址，查看主机上的现有虚拟机。" : "试试其他关键词，或切换到全部虚拟机。", 12, true) } };
            _machineCards.Children.Add(Card(empty));
        }
        for (var i = 0; i < matches.Length; i++) {
            var card = BuildMachineCard(matches[i]); Grid.SetRow(card, i / columns); Grid.SetColumn(card, i % columns); _machineCards.Children.Add(card);
        }
        RenderDetails(); UpdateWorkspaceDisplay();
    }
    private static Control StateBadge(VirtualMachine vm)
    {
        var label = Text("●  " + vm.StateLabel, 11);
        ApplyTheme(label, TextBlock.ForegroundProperty, vm.State == "running" ? "AppPositiveBrush" : vm.State == "paused" ? "AppWarningBrush" : "AppMutedBrush");
        var badge = new Border { Child = label, Padding = new Thickness(9, 4), CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Left };
        ApplyTheme(badge, Border.BackgroundProperty, vm.State == "running" ? "AppPositiveSurfaceBrush" : "AppSubtleBrush"); return badge;
    }
    private Control BuildMachineCard(VirtualMachine vm)
    {
        var preview = new Grid { Height = _listView ? 0 : 138, IsVisible = !_listView, ClipToBounds = true };
        ApplyTheme(preview, Panel.BackgroundProperty, "AppAccentSurfaceBrush");
        var placeholder = new StackPanel { Spacing = 9, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            Children = { Icon("desktop", 44), Text(vm.State == "running" ? "连接后显示桌面预览" : vm.State == "paused" ? "恢复后连接桌面" : "启动后连接桌面", 11, true) } };
        preview.Children.Add(placeholder); AddDesktopPreview(preview, placeholder, vm);
        var previewButton = new Button { Content = preview, Padding = new Thickness(0), Classes = { "ad-flat" },
            HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch, IsVisible = !_listView, IsEnabled = !_busy };
        previewButton.Click += async (_, _) => await ExecuteAsync(async () => { SelectVm(vm); if (vm.State == "running") await ConnectConsoleAsync(vm); });
        AutomationProperties.SetName(previewButton, "打开桌面 " + vm.Name);
        var title = Text(vm.Name, 16); title.FontWeight = FontWeight.SemiBold; title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(title, vm.Name);
        var favorite = VMCubeIcons.Button(_favorites.Contains(FavoriteKey(vm)) ? "取消收藏" : "收藏虚拟机", _favorites.Contains(FavoriteKey(vm)) ? "star-filled" : "star");
        favorite.Name = "VmFavorite"; favorite.Background = Brushes.Transparent; favorite.BorderThickness = new Thickness(0); favorite.IsEnabled = !_busy;
        favorite.Click += async (_, _) => await ExecuteAsync(() => ToggleFavoriteAsync(vm));
        var heading = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 }; heading.Children.Add(title); Grid.SetColumn(favorite, 1); heading.Children.Add(favorite);
        var protocol = _connectedUri == "local" ? _local.GetDefinition(vm.Id).Protocol.ToString().ToUpperInvariant() : "SPICE / VNC";
        var metadata = new WrapPanel { ItemSpacing = 10, LineSpacing = 5, Children = { StateBadge(vm), Text(protocol, 11, true) } };
        var info = new StackPanel { Spacing = 8, Margin = new Thickness(14, 12), Children = { heading, metadata, Text($"{vm.Cpus} vCPU  ·  {vm.MemoryGiB:0.#} GiB 内存", 12, true) } };
        var actions = new Grid { ColumnDefinitions = new("*,*,*,*"), ColumnSpacing = 6, Margin = new Thickness(8), IsEnabled = !_busy };
        var primary = IconActionButton(vm.State == "running" ? "连接桌面" : vm.State == "paused" ? "恢复运行" : "启动虚拟机", vm.State == "running" ? "connect" : "play", async () => {
            SelectVm(vm); if (vm.State == "running") await ConnectConsoleAsync(vm); else await ActAsync(vm, vm.State == "paused" ? "resume" : "start"); }, true);
        primary.Name = "VmConnect"; primary.IsEnabled = vm.State is "running" or "paused" or "shut off";
        var pause = IconActionButton("暂停虚拟机", "pause", async () => { SelectVm(vm); await ActAsync(vm, "suspend"); });
        pause.Name = "VmPause"; pause.IsEnabled = vm.State == "running";
        var power = IconActionButton("正常关机", "power", () => { SelectVm(vm); _confirmation = vm.Id; RenderDetails(); RevealDetails(); return Task.CompletedTask; });
        power.Name = "VmPower"; power.IsEnabled = vm.State == "running";
        var inspect = VMCubeIcons.Button("虚拟机详情", "info"); inspect.Name = "VmDetails";
        inspect.Click += (_, _) => { SelectVm(vm); RevealDetails(); };
        var buttons = new[] { primary, pause, power, inspect };
        for (var i = 0; i < buttons.Length; i++) { Grid.SetColumn(buttons[i], i); actions.Children.Add(buttons[i]); }
        var actionBar = new Border { Child = actions, BorderThickness = new Thickness(0, 1, 0, 0) }; ApplyTheme(actionBar, Border.BorderBrushProperty, "AppBorderBrush");
        var content = new StackPanel { Children = { previewButton, info, actionBar } };
        var card = Card(content); card.Padding = new Thickness(3); card.ClipToBounds = true; card.Tag = vm.Id;
        if (_selectedVmId == vm.Id) ApplyTheme(card, Border.BorderBrushProperty, "AppAccentBrush");
        return card;
    }
    private void RevealDetails()
    {
        if (_compact && _inlineExpander is not null) { _inlineExpander.IsExpanded = true; _inlineExpander.BringIntoView(); }
    }
    private void RenderEmptyDetails()
    {
        _details.Children.Clear(); _details.Children.Add(Text("虚拟机详情", 12, true));
        _details.Children.Add(Text("选择一台虚拟机", 18));
        _details.Children.Add(Text("在卡片中启动或连接桌面，点击“详情”查看更多操作。", 12, true));
    }
}
