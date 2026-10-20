using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using VMCube.Module.Services;
using URemote.Core;

namespace VMCube.Module.Views;

public sealed partial class VMCubeView
{
    private bool _isLocal = true, _listView, _favoritesOnly, _compact;
    private int _cardColumns = 2;
    private string? _selectedVmId;
    private readonly ContentControl _page = new();
    private readonly ContentControl _railDetails = new(), _inlineDetails = new();
    private Expander? _inlineExpander;
    private readonly Grid _machineCards = new() { ColumnSpacing = 16, RowSpacing = 16 };
    private readonly TextBlock _catalogHeading = new() { FontSize = 28, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _catalogHint = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _workspaceName = new() { FontSize = 20, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _workspaceState = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _libraryActions = new() { ItemSpacing = 8, LineSpacing = 8 };
    private readonly Border _remoteConnection = new(), _demoBanner = new();
    private readonly List<Button> _navigation = [], _catalogTabs = [];
    private readonly HashSet<string> _favorites = [];
    private Control[] _pages = [];
    private string FavoritesPath => Path.Combine(_context.DataDirectory, "favorites.json");

    private static void ApplyTheme(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, new DynamicResourceExtension(key));

    private static void ConfigureTheme(Control control)
    {
        control.Resources.ThemeDictionaries[ThemeVariant.Light] = new ResourceDictionary {
            ["AppAccentBrush"] = new SolidColorBrush(Color.Parse("#007D73")),
            ["AppAccentHoverBrush"] = new SolidColorBrush(Color.Parse("#006B62")),
            ["AppAccentPressedBrush"] = new SolidColorBrush(Color.Parse("#00584F")),
            ["AppAccentSurfaceBrush"] = new SolidColorBrush(Color.Parse("#E1F2EF")),
            ["AppSelectionBrush"] = new SolidColorBrush(Color.Parse("#E1F2EF")),
            ["AppFocusBrush"] = new SolidColorBrush(Color.Parse("#008D82")),
            ["AppOnAccentBrush"] = Brushes.White };
        control.Resources.ThemeDictionaries[ThemeVariant.Dark] = new ResourceDictionary {
            ["AppAccentBrush"] = new SolidColorBrush(Color.Parse("#43CEB9")),
            ["AppAccentHoverBrush"] = new SolidColorBrush(Color.Parse("#68DDCB")),
            ["AppAccentPressedBrush"] = new SolidColorBrush(Color.Parse("#30B49F")),
            ["AppAccentSurfaceBrush"] = new SolidColorBrush(Color.Parse("#173F3A")),
            ["AppSelectionBrush"] = new SolidColorBrush(Color.Parse("#173F3A")),
            ["AppFocusBrush"] = new SolidColorBrush(Color.Parse("#43CEB9")),
            ["AppOnAccentBrush"] = new SolidColorBrush(Color.Parse("#052B25")) };
        if (control is Avalonia.Controls.Primitives.TemplatedControl templated) templated.FontFamily = new FontFamily(OperatingSystem.IsLinux() ? "Noto Sans CJK SC" : "Segoe UI");
    }

    private static Control Icon(string name, double size = 20) => VMCubeIcons.Draw(name, size);

    private void BuildInterface()
    {
        ConfigureTheme(this); ApplyTheme(this, BackgroundProperty, "AppPageBrush");
        ApplyTheme(_catalogHeading, TextBlock.ForegroundProperty, "AppStrongTextBrush");
        ApplyTheme(_catalogHint, TextBlock.ForegroundProperty, "AppMutedBrush");
        ApplyTheme(_workspaceName, TextBlock.ForegroundProperty, "AppStrongTextBrush");
        ApplyTheme(_workspaceState, TextBlock.ForegroundProperty, "AppMutedBrush");
        try { if (File.Exists(FavoritesPath)) foreach (var key in JsonSerializer.Deserialize<string[]>(File.ReadAllText(FavoritesPath)) ?? []) _favorites.Add(key); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { AddLog("收藏读取失败：" + ex.Message); }

        var brandTitle = Text("VMCUBE", 25); var brandSubtitle = Text("虚拟机工作空间", 12, true);
        var brandLogo = Icon("cube", 40); brandLogo.Name = "VmBrandLogo";
        brandTitle.HorizontalAlignment = HorizontalAlignment.Center; brandSubtitle.HorizontalAlignment = HorizontalAlignment.Center;
        var brand = new StackPanel { Spacing = 8, Margin = new Thickness(0, 12, 0, 26), Children = { brandLogo, brandTitle, brandSubtitle } };
        var navigationItems = new StackPanel { Spacing = 8 };
        foreach (var (title, icon, index) in new[] { ("我的虚拟机", "desktop", 0), ("远程主机", "server", 1),
            ("直接连接", "connect", 2), ("操作记录", "history", 3), ("引擎与设置", "settings", 4) })
        {
            var caption = Text(title, 14);
            var button = new Button { Name = "VmNav" + index, Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12,
                Children = { Icon(icon, 18), caption } }, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 13), CornerRadius = new CornerRadius(9) };
            button.Click += async (_, _) => await ExecuteAsync(() => NavigateAsync(index));
            AutomationProperties.SetName(button, title); ToolTip.SetTip(button, title); _navigation.Add(button);
            if (index < 4) navigationItems.Children.Add(button);
        }
        var sidebar = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(brand, Dock.Top); sidebar.Children.Add(brand);
        DockPanel.SetDock(_navigation[4], Dock.Bottom); sidebar.Children.Add(_navigation[4]); sidebar.Children.Add(navigationItems);
        var navigationBorder = new Border { Child = sidebar, BorderThickness = new Thickness(1, 0, 0, 0) };
        ApplyTheme(navigationBorder, Border.BorderBrushProperty, "AppBorderBrush");

        _libraryActions.Children.Add(ActionButton("创建虚拟机", CreateLocalVmAsync, true));
        _libraryActions.Children.Add(ActionButton("导入现有虚拟机", ImportLocalVmAsync));
        var toolbar = new Grid { ColumnDefinitions = new("*,Auto,Auto,Auto"), ColumnSpacing = 8 };
        _search.PlaceholderText = "搜索虚拟机名称";
        toolbar.Children.Add(_search); Grid.SetColumn(_filter, 1); toolbar.Children.Add(_filter);
        Grid.SetColumn(_refresh, 2); toolbar.Children.Add(_refresh);
        var viewMode = new Button { Name = "VmViewMode", Content = "列表", MinWidth = 50, CornerRadius = new CornerRadius(8) };
        viewMode.Click += (_, _) => { _listView = !_listView; viewMode.Content = _listView ? "网格" : "列表"; RenderMachines(); };
        Grid.SetColumn(viewMode, 3); toolbar.Children.Add(viewMode);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (label, favorite) in new[] { ("全部虚拟机", false), ("收藏", true) })
        {
            var tab = new Button { Content = label, Padding = new Thickness(16, 8), CornerRadius = new CornerRadius(18) };
            tab.Click += (_, _) => { _favoritesOnly = favorite; UpdateTabs(); RenderMachines(); };
            _catalogTabs.Add(tab); tabs.Children.Add(tab);
        }
        _uri.MinWidth = 160;
        _connection.ItemSpacing = 8; _connection.LineSpacing = 8;
        _connection.Children.Add(_uri); _connection.Children.Add(ActionButton("连接主机", ConnectAsync, true));
        _remoteConnection.Child = Card(new StackPanel { Spacing = 10, Children = { Text("连接 KVM 主机", 17),
            Text("使用 SSH 密钥连接，管理主机上的现有虚拟机。", 12, true), _connection } });
        var demoContent = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        demoContent.Children.Add(Text("演示模式 · 模拟设备与操作", 12, true));
        var exitDemo = ActionButton("退出演示", ConnectAsync); Grid.SetColumn(exitDemo, 1); demoContent.Children.Add(exitDemo);
        _demoBanner.Child = demoContent; _demoBanner.Padding = new Thickness(12, 6); _demoBanner.CornerRadius = new CornerRadius(8);
        ApplyTheme(_demoBanner, Border.BackgroundProperty, "AppAccentSurfaceBrush");
        _inlineExpander = new Expander { Header = "当前虚拟机详情", Content = _inlineDetails, IsExpanded = false };
        var inlineCard = Card(_inlineExpander); inlineCard.IsVisible = false;
        var library = new StackPanel { Spacing = 16, Children = { _catalogHeading, _catalogHint, _remoteConnection, _libraryActions,
            _demoBanner, toolbar, tabs, inlineCard, _machineCards,
            Text("连接后在独立窗口中控制桌面，画面同时显示在卡片中。预览仅保存在本次会话。", 11, true) } };

        var logPage = new StackPanel { Spacing = 16, Children = { Text("操作记录", 28),
            Text("查看本次打开 VMCUBE 后的连接、虚拟机操作与错误详情。", 13, true), Card(_log) } };
        _pages = [library, library, BuildDirectPage(), logPage, BuildSettingsPage()];
        _page.Margin = new Thickness(24);
        var center = new ScrollViewer { Content = _page, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var rail = new StackPanel { Margin = new Thickness(18, 24), Spacing = 16, Children = {
            _workspaceName, _workspaceState, new Separator(), _railDetails } };
        _railDetails.Content = _details;
        var railBorder = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Child = new ScrollViewer { Content = rail,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } };
        ApplyTheme(railBorder, Border.BackgroundProperty, "AppSurfaceBrush"); ApplyTheme(railBorder, Border.BorderBrushProperty, "AppBorderBrush");
        var body = new Grid { ColumnDefinitions = new("280,*,190"), RowDefinitions = new("*,Auto") };
        body.Children.Add(railBorder); Grid.SetColumn(center, 1); body.Children.Add(center); Grid.SetColumn(navigationBorder, 2); body.Children.Add(navigationBorder);
        _status.FontSize = 11; _status.MaxLines = 2; _status.TextTrimming = TextTrimming.CharacterEllipsis;
        ApplyTheme(_status, TextBlock.ForegroundProperty, "AppMutedBrush");
        _status.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) {
            ToolTip.SetTip(_status, _status.Text); ApplyTheme(_status, TextBlock.ForegroundProperty,
                _status.Text?.Contains("失败", StringComparison.Ordinal) == true ? "AppDangerBrush" : "AppMutedBrush"); } };
        var footerContent = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        footerContent.Children.Add(_status); var version = Text("VMCUBE 0.6.2", 11, true); Grid.SetColumn(version, 1); footerContent.Children.Add(version);
        var footer = new Border { Padding = new Thickness(18, 9), BorderThickness = new Thickness(0, 1, 0, 0), Child = footerContent };
        ApplyTheme(footer, Border.BorderBrushProperty, "AppBorderBrush"); Grid.SetRow(footer, 1); Grid.SetColumnSpan(footer, 3); body.Children.Add(footer);
        Content = body;
        SizeChanged += (_, e) => {
            var compact = e.NewSize.Width < 850;
            railBorder.IsVisible = !compact;
            body.ColumnDefinitions = new(compact ? "0,*,76" : e.NewSize.Width >= 1150 ? "280,*,190" : "250,*,170");
            sidebar.Margin = new Thickness(compact ? 8 : 12);
            brandTitle.Text = compact ? "VM" : "VMCUBE"; brandTitle.FontSize = compact ? 22 : 25;
            brandTitle.HorizontalAlignment = HorizontalAlignment.Center; brandLogo.Width = brandLogo.Height = compact ? 28 : 40;
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

    private void UpdateTabs()
    {
        for (var i = 0; i < _catalogTabs.Count; i++) ApplyTheme(_catalogTabs[i], Button.BackgroundProperty,
            _favoritesOnly == (i == 1) ? "AppAccentSurfaceBrush" : "AppSurfaceBrush");
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
    private void ShowPage(int index)
    {
        _page.Content = _pages[index];
        for (var i = 0; i < _navigation.Count; i++) ApplyTheme(_navigation[i], Button.BackgroundProperty,
            i == index ? "AppAccentSurfaceBrush" : "AppPageBrush");
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
        var previewButton = new Button { Content = preview, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent,
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
    private Control BuildSettingsPage()
    {
        _engineDirectory.MinWidth = 160;
        _engineControls.ItemSpacing = 8; _engineControls.LineSpacing = 8;
        _engineControls.Children.Add(_engineDirectory); _engineControls.Children.Add(ActionButton("选择目录", ChooseEngineAsync)); _engineControls.Children.Add(_checkEngine);
        ApplyTheme(_engineStatus, TextBlock.ForegroundProperty, "AppMutedBrush");
        return new StackPanel { Spacing = 16, Children = { Text("引擎与设置", 28), Text("配置本机虚拟化引擎与桌面连接。", 13, true),
            Card(new StackPanel { Spacing = 12, Children = { Text("本机 QEMU 引擎", 18), _engineStatus, _engineControls,
                Text("留空时自动检测应用内置引擎和系统已安装的 QEMU。", 12, true) } }),
            Card(new StackPanel { Spacing = 12, Children = { Text("本机工作区", 18),
                Text("新建虚拟机保存在应用数据目录；导入的虚拟机继续使用原有磁盘。", 13, true),
                new SelectableTextBlock { Text = _context.DataDirectory, FontSize = 12, TextWrapping = TextWrapping.Wrap } } }),
            Card(new StackPanel { Spacing = 10, Children = { Text("体验与帮助", 18), Text("无需创建虚拟机，即可查看卡片布局和操作流程。", 13, true),
                ActionButton("查看演示", DemoAsync), Text("VMCUBE 0.6.2 · 本机 QEMU / KVM · 远程 SSH · SPICE / VNC", 11, true) } }) } };
    }
    private Control BuildDirectPage()
    {
        var protocol = new ComboBox { ItemsSource = new[] { "SPICE", "VNC" }, SelectedIndex = 0 };
        var host = new TextBox { PlaceholderText = "主机名或 IP 地址" };
        var port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 5900, Increment = 1 };
        var password = new TextBox { PasswordChar = '●', PlaceholderText = "控制台密码（可留空）" };
        var tls = new CheckBox { Content = "使用 TLS 并验证服务器证书" };
        var caFile = new TextBox { PlaceholderText = "CA 证书 PEM 文件路径（可选）", IsVisible = false };
        tls.IsCheckedChanged += (_, _) => caFile.IsVisible = tls.IsChecked == true;
        protocol.SelectionChanged += (_, _) => { tls.IsVisible = protocol.SelectedIndex == 0; caFile.IsVisible = protocol.SelectedIndex == 0 && tls.IsChecked == true; };
        var error = Text("", 12); ApplyTheme(error, TextBlock.ForegroundProperty, "AppDangerBrush");
        var connect = ActionButton("连接桌面", () => {
            try {
                var options = new LocalDesktopOptions(LocalDesktopProtocol.Vnc, host.Text?.Trim() ?? "", (int)(port.Value ?? 5900), Password: password.Text ?? ""); options.Validate();
                var endpoint = new ConsoleEndpoint(options.Host, options.Port, protocol.SelectedIndex == 0 ? ConsoleProtocol.Spice : ConsoleProtocol.Vnc, protocol.SelectedIndex == 0 && tls.IsChecked == true);
                var ca = caFile.Text?.Trim();
                if (endpoint.UseTls && !string.IsNullOrWhiteSpace(ca) && !File.Exists(ca)) throw new ArgumentException("CA 证书文件不存在。");
                password.Text = ""; error.Text = "";
                OpenConsole($"direct/{endpoint.Protocol}/{endpoint.Host}:{endpoint.Port}/{endpoint.UseTls}", $"{endpoint.Host}:{endpoint.Port}", options, consoleProtocol: endpoint.Protocol, useTls: endpoint.UseTls, caFile: endpoint.UseTls ? ca : null);
            }
            catch (ArgumentException ex) { error.Text = ex.Message; }
            return Task.CompletedTask;
        }, true);
        var form = new StackPanel { Spacing = 12, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Stretch, Children = {
            Text("连接已有控制台", 18), Text("显示协议", 12, true), protocol, Text("主机地址", 12, true), host,
            Text("端口", 12, true), port, Text("访问密码", 12, true), password, tls, caFile, error, connect } };
        return new StackPanel { Spacing = 16, Children = { Text("直接连接", 28), Text("通过地址进入 SPICE / VNC 桌面，无需添加 KVM 主机。", 13, true), Card(form),
            Text("密码仅用于本次连接。未启用 TLS 时请使用可信局域网或 VPN。", 12, true) } };
    }
}
