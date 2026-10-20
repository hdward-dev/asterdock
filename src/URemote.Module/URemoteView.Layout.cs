using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using URemote.Core;
using URemote.Host;
using URemote.Linux;

namespace URemote.Module;

public sealed partial class URemoteView
{
    private void UpdateAccountDisplay(SavedHostIdentity? saved)
    {
        var authenticated = saved?.State.IsAuthenticated == true;
        accountState.Text = authenticated ? "●  已登录" : "○  尚未登录";
        ApplyTheme(accountState, TextBlock.ForegroundProperty, authenticated ? "AppPositiveBrush" : "AppMutedBrush");
        accountHeading.Text = authenticated ? "当前登录账号" : "登录 UU 账号";
        accountDescription.Text = authenticated ? "使用同一 UU 账号，即可连接你的其他设备。" : "登录后同步设备，并开启本机远程控制。";
        accountIdentity.Text = authenticated ? string.IsNullOrEmpty(saved!.LoginAccount)
            ? "UU 账号 ID：" + saved.State.UserId : "UU 账号：" + saved.LoginAccount : "";
        accountIdentity.IsVisible = authenticated;
        logoutHint.IsVisible = authenticated;
        logout.IsVisible = authenticated; loginPanel.IsVisible = !authenticated;
    }
    private int deviceColumns = 2;
    private bool listView;
    private int deviceTab;
    private readonly List<Button> navigation = [];
    private string? selectedDeviceId;
    private readonly Dictionary<string, RemoteDesktopWindow> remoteWindows = [];
    private readonly Dictionary<string, RemoteToolsWindow> toolWindows = [];
    private Control[] pages = [];
    private IReadOnlyList<UuDevice> devices = [];
    private HashSet<string> favorites = [];
    private string favoriteFile = "";
    private bool refreshing;
    private DispatcherTimer? deviceTimer;

    private static void ApplyTheme(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, new DynamicResourceExtension(key));
    private static void SetClass(Control control, string name, bool on)
    {
        if (on) { if (!control.Classes.Contains(name)) control.Classes.Add(name); }
        else control.Classes.Remove(name);
    }
    private static TextBlock Text(string value, double size = 13, bool muted = false)
    {
        var text = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        ApplyTheme(text, TextBlock.ForegroundProperty, muted ? "AppMutedBrush" : "AppStrongTextBrush"); return text;
    }
    private static Border Card(Control child, int padding = 20)
    {
        var border = new Border { Child = child, Padding = new Thickness(padding) };
        border.Classes.Add("ad-card");
        return border;
    }
    private static PathIcon DeviceIcon(bool mobile = false)
    {
        var icon = new PathIcon
        {
            Width = 22, Height = 22,
            Data = Geometry.Parse(mobile ? "M7,2 H17 V22 H7 Z M9,4 V18 H15 V4 Z M11,20 H13 V21 H11 Z" : "M2,3 H22 V17 H13 V20 H18 V22 H6 V20 H11 V17 H2 Z M4,5 V15 H20 V5 Z")
        };
        ApplyTheme(icon, PathIcon.ForegroundProperty, "AppMutedBrush");
        return icon;
    }
    private void WireShell(string dataDirectory)
    {
        favoriteFile = Path.Combine(dataDirectory, "device-favorites.json");
        try { if (File.Exists(favoriteFile)) favorites = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(favoriteFile)) ?? []; }
        catch (Exception e) when (e is IOException or JsonException) { }
        metrics.FontSize = 12; metrics.Text = OperatingSystem.IsMacOS() ? "多屏桌面 · 键鼠控制" : "双屏桌面 · 远程终端";
        ApplyTheme(metrics, TextBlock.ForegroundProperty, "AppMutedBrush");
        detail.IsVisible = !string.IsNullOrWhiteSpace(detail.Text);
        detail.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) detail.IsVisible = !string.IsNullOrWhiteSpace(detail.Text); };
        status.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) UpdateHostBadge(); };
        navigation.AddRange([navDevices, navHost, navAccount, navAssist, navFiles, navLocal]);
        pages = [devicePage, hostPage, accountPage, assistancePage, filePage, localPage];
        hostIntro.Text = $"在其他设备的 UU 官方客户端中，选择「{LinuxDeviceProfile.DeviceName}」连接。";
        footerText.Text = $"U远程 · 星栈远程控制插件  |  桌面环境：{DesktopEnvironmentInfo.Current.DisplayName}";
        desktopEnvironment.Text = DesktopEnvironmentInfo.Current.DisplayName;
        transferPath.Text = HostTransferPaths.SharedDirectory;
        search.TextChanged += (_, _) => RenderDevices();
        filter.SelectionChanged += (_, _) => RenderDevices();
        accountPage.SizeChanged += (_, e) =>
        {
            var compact = e.NewSize.Width < 440;
            accountHeadingGrid.ColumnDefinitions = new(compact ? "Auto,*" : "Auto,*,Auto");
            accountHeadingGrid.RowDefinitions = new(compact ? "Auto,Auto" : "Auto");
            Grid.SetColumn(logout, compact ? 1 : 2); Grid.SetRow(logout, compact ? 1 : 0);
            logout.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            logout.Margin = new Thickness(0, compact ? 12 : 0, 0, 0);
        };
        BuildEncoderSettings();
        WireAssistanceCard();
        WireConnectionCard();
        WireAssistanceConnect();
        InitializeLocalConnection(dataDirectory);
        SizeChanged += (_, e) =>
        {
            var wide = e.NewSize.Width >= 1150;
            body.ColumnDefinitions = new(wide ? "300,*,190" : "270,*,150");
            page.Margin = new Thickness(wide ? 24 : 14);
            if (e.NewSize.Width < 850)
            {
                body.ColumnDefinitions = new("0,*,130"); hostBorder.IsVisible = false;
                hostPanel.Margin = new Thickness(0);
                if (!hostPage.Children.Contains(hostPanel)) { hostScroll.Content = null; hostPage.Children.Insert(2, hostPanel); }
            }
            else
            {
                if (hostPage.Children.Contains(hostPanel)) { hostPage.Children.Remove(hostPanel); hostScroll.Content = hostPanel; }
                hostPanel.Margin = new Thickness(14, 12);
                hostBorder.IsVisible = true;
            }
            var columns = e.NewSize.Width >= 1150 ? 2 : 1;
            if (columns != deviceColumns) { deviceColumns = columns; RenderDevices(); }
        };
        ShowPage(0); RenderDevices();
        try { UpdateAccountDisplay(DesktopHostSession.ReadIdentity(identity.Text ?? "")); }
        catch { UpdateAccountDisplay(null); }
        deviceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        deviceTimer.Tick += async (_, _) => { if (IsVisible && page.Content == pages[0] && Environment.GetEnvironmentVariable("UREMOTE_NO_AUTO_START") != "1") await RefreshDevicesAsync(); };
        deviceTimer.Start();
    }
    private void Nav_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && int.TryParse(button.Tag?.ToString(), out var index)) ShowPage(index);
    }
    private void ScreenLink_Click(object? sender, RoutedEventArgs e) => ShowPage(1);
    private async void RefreshDevices_Click(object? sender, RoutedEventArgs e) => await RefreshDevicesAsync();
    private void ViewMode_Click(object? sender, RoutedEventArgs e)
    {
        listView = !listView; viewMode.Content = listView ? "网格" : "列表"; RenderDevices();
    }
    private void DeviceTab_Click(object? sender, RoutedEventArgs e)
    {
        deviceTab = ReferenceEquals(sender, deviceTabFav) ? 1 : 0;
        SetClass(deviceTabAll, "selected", deviceTab == 0); SetClass(deviceTabFav, "selected", deviceTab == 1);
        RenderDevices();
    }
    private void OpenTransfer_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(HostTransferPaths.SharedDirectory);
            var launch = new System.Diagnostics.ProcessStartInfo("xdg-open") { UseShellExecute = false };
            launch.ArgumentList.Add(HostTransferPaths.SharedDirectory); System.Diagnostics.Process.Start(launch)?.Dispose();
        }
        catch { transferState.Text = "无法打开目录，请手动打开下方路径。"; }
    }
    private async void AddTransfer_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions { Title = "选择供 UU 官方客户端取出的文件", AllowMultiple = true });
        if (files.Count == 0) return; addTransfer.IsEnabled = false;
        try
        {
            Directory.CreateDirectory(HostTransferPaths.SharedDirectory);
            foreach (var file in files)
            {
                var name = Path.GetFileName(file.Name); var target = Path.Combine(HostTransferPaths.SharedDirectory, name);
                if (File.Exists(target)) target = Path.Combine(HostTransferPaths.SharedDirectory, Path.GetFileNameWithoutExtension(name) + "-" + Guid.NewGuid().ToString("N")[..8] + Path.GetExtension(name));
                var temporary = Path.Combine(HostTransferPaths.SharedDirectory, ".uremote-" + Guid.NewGuid().ToString("N") + ".part");
                try
                {
                    await using (var source = await file.OpenReadAsync())
                    await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                        await source.CopyToAsync(destination, lifetime.Token);
                    File.Move(temporary, target, false);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            transferState.Text = $"已添加 {files.Count} 个文件，可在官方客户端刷新目录后取出。";
        }
        catch { transferState.Text = "添加未完成，请检查文件和磁盘空间。"; }
        finally { addTransfer.IsEnabled = true; }
    }
    private static void Place(ContentControl host, Control control)
    {
        if (ReferenceEquals(host.Content, control)) return;
        if (control.Parent is Panel panel) panel.Children.Remove(control);
        host.Content = control;
    }

    private void ShowPage(int index)
    {
        if (pages.Length == 0) return;
        Place(page, pages[index]);
        for (var i = 0; i < navigation.Count; i++) SetClass(navigation[i], "selected", i == index);
    }
    private async Task RefreshDevicesAsync()
    {
        if (disposed || loggingOut || refreshing) return;
        var generation = accountGeneration;
        refreshing = true; refresh.IsEnabled = false; refresh.Content = "正在同步…";
        try
        {
            var saved = DesktopHostSession.ReadIdentity(identity.Text ?? "");
            var state = saved.State;
            if (!state.IsAuthenticated) throw new InvalidOperationException();
            using var api = new UuMacHostApi(state);
            var loaded = await api.GetDevicesAsync(lifetime.Token);
            if (disposed || loggingOut || generation != accountGeneration) return;
            devices = loaded; Console.WriteLine("device-catalog-loaded;count=" + devices.Count); UpdateAccountDisplay(saved);
            catalogHint.Text = $"{devices.Count} 台设备 · {devices.Count(x => x.Online)} 台在线 · 更新于 {DateTime.Now:HH:mm:ss}";
            if (selectedDeviceId is { } selected && devices.FirstOrDefault(x => x.Id == selected) is { } updated) ShowDevice(updated);
            else { selectedDeviceId = null; deviceDetail.Children.Clear(); deviceDetail.IsVisible = false; }
            RenderDevices();
            await Task.WhenAll(RefreshPreviewsAsync(), RefreshAssistanceAsync());
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch
        {
            if (!disposed && !loggingOut && generation == accountGeneration) catalogHint.Text = devices.Count > 0 ? "刷新失败，保留上次设备列表。请检查网络后重试。" : "暂时无法获取设备，请在“账号与设置”检查登录状态，或点击刷新重试。";
        }
        finally { refreshing = false; if (!disposed) { refresh.IsEnabled = true; refresh.Content = "刷新"; } }
    }
    private void RenderDevices()
    {
        deviceRows.Children.Clear();
        deviceRows.ColumnDefinitions = new("*"); deviceRows.RowDefinitions = new("Auto");
        var query = search.Text?.Trim() ?? "";
        var matches = devices.Where(d => (query.Length == 0 || (d.Name + " " + d.PlatformName + " " + d.Id).Contains(query, StringComparison.OrdinalIgnoreCase))
            && (deviceTab == 0 || favorites.Contains(d.Id)) && (filter.SelectedIndex switch { 1 => d.Online, 2 => favorites.Contains(d.Id), 3 => d.Category == "desktop", 4 => d.Category == "mobile", _ => true }))
            .OrderByDescending(d => d.IsCurrent).ThenByDescending(d => d.Online).ThenBy(d => d.Name, StringComparer.CurrentCulture).ToArray();
        catalogCount.Text = devices.Count > 0 ? $"我的设备  {matches.Length}" : "我的设备";
        if (matches.Length == 0)
        {
            deviceRows.Children.Add(Card(new StackPanel { Spacing = 10, Margin = new Thickness(0, 20), Children = {
                Text(devices.Count == 0 ? "还没有加载设备" : "没有符合条件的设备", 18),
                Text(devices.Count == 0 ? "登录后点击刷新，即可查看同一 UU 账号下的电脑和移动设备。" : "试试其他关键词，或将筛选切换为“全部设备”。", 13, true) } })); return;
        }
        var columns = listView ? 1 : deviceColumns;
        deviceRows.ColumnDefinitions = new ColumnDefinitions(columns == 2 ? "*,*" : "*");
        deviceRows.RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", (matches.Length + columns - 1) / columns)));
        for (var i = 0; i < matches.Length; i++)
        {
            var card = BuildPreviewCard(matches[i]);
            Grid.SetColumn(card, i % columns); Grid.SetRow(card, i / columns); deviceRows.Children.Add(card);
        }
    }
    private void OpenRemote(UuDevice device)
    {
        if (toolWindows.ContainsKey(device.Id)) { catalogHint.Text = "请先关闭此设备的终端或文件窗口，再发起桌面连接。"; return; }
        if (remoteWindows.TryGetValue(device.Id, out var existing)) { existing.Activate(); return; }
        if (device.IsCurrent || !device.Online || !device.Controllable || !device.ControlledSupport) return;
        try
        {
            var state = DesktopHostSession.ReadIdentity(identity.Text ?? "").State;
            if (!state.IsAuthenticated || !File.Exists(appliedEncoder)) throw new InvalidOperationException();
            var window = new RemoteDesktopWindow(device, state, appliedEncoder);
            remoteWindows.Add(device.Id, window);
            window.Closed += async (_, _) => { try { await window.Completion; } finally { remoteWindows.Remove(device.Id); } };
            window.Show();
        }
        catch { catalogHint.Text = "无法打开远控窗口，请检查登录状态与视频解码器路径。"; }
    }
    private void OpenTool(UuDevice device, bool terminal)
    {
        if (remoteWindows.ContainsKey(device.Id)) { catalogHint.Text = "请先断开此设备的桌面，再打开终端或文件传输。"; return; }
        if (toolWindows.TryGetValue(device.Id, out var existing)) { existing.Activate(); catalogHint.Text = "请先关闭该设备已有的工具窗口，再切换连接类型。"; return; }
        if (device.IsCurrent || !device.Online || !device.Controllable || !device.ControlledSupport) return;
        try
        {
            var state = DesktopHostSession.ReadIdentity(identity.Text ?? "").State;
            if (!state.IsAuthenticated) throw new InvalidOperationException();
            var window = new RemoteToolsWindow(device, state, terminal); toolWindows.Add(device.Id, window);
            window.Closed += async (_, _) => { try { await window.Completion; } finally { toolWindows.Remove(device.Id); } };
            window.Show();
        }
        catch { catalogHint.Text = "无法打开工具窗口，请检查登录状态。"; }
    }
    private void SaveFavorites()
    {
        try { File.WriteAllText(favoriteFile, JsonSerializer.Serialize(favorites)); if (OperatingSystem.IsLinux()) File.SetUnixFileMode(favoriteFile, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (IOException) { catalogHint.Text = "收藏已暂存，但未能写入本机设置。"; }
    }
    private void ShowDevice(UuDevice device)
    {
        selectedDeviceId = device.Id;
        deviceDetail.IsVisible = true;
        deviceDetail.Children.Clear();
        var copy = new Button { Content = "复制设备编号" };
        copy.Click += async (_, _) =>
        {
            try { if (TopLevel.GetTopLevel(this)?.Clipboard is { } board) { await board.SetTextAsync(device.Id); copy.Content = "已复制"; } }
            catch { copy.Content = "复制失败"; }
        };
        var close = new Button { Content = "收起" }; close.Click += (_, _) => { selectedDeviceId = null; deviceDetail.Children.Clear(); deviceDetail.IsVisible = false; RenderDevices(); };
        deviceDetail.Children.Add(Card(new StackPanel { Spacing = 10, Children = {
            Text(device.Name, 18), Text($"{device.PlatformName} · {device.StatusName}"),
            Text("设备编号  " + device.Id, 12, true), Text("客户端版本  " + (device.Version.Length == 0 ? "未提供" : device.Version), 12, true),
            Text(device.IsCurrent ? "这是本机。可在其他设备的 UU 官方客户端中发起桌面或终端连接。" : "可通过设备列表中的远程控制，在独立窗口中连接。", 13, true),
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { copy, close } } } }));
    }
}
