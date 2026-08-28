using AsterDock.Host.Modules;
using AsterDock.Contracts;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.Diagnostics;
using AsterDock.Host.Services;

namespace AsterDock.Host.Views;

public partial class MainWindow : Window, IApplicationShell
{
    private const double CollapsedCapsuleWidth = 124;
    private const double HostBackCapsuleWidth = 176;
    private const double ExpandedCapsuleWidth = 218;
    private const double ExpandedBackCapsuleWidth = 261;
    private enum SettingsSection
    {
        Applications,
        Discover,
        Shortcuts,
        Update
    }

    private readonly ModuleCatalog _catalog = new();
    private readonly SharedSystemMetricsService _systemMetrics = new();
    private readonly GitHubUpdateService _updateService = new();
    private readonly GitHubApplicationDiscoveryService _discoveryService = new();
    private readonly CancellationTokenSource _updateCancellation = new();
    private bool _spaceKeyDown;
    private bool _appSwitcherShortcutTriggered;
    private bool _allowClose;
    private bool _applicationsLoaded;
    private int _capsuleAnimationVersion;
    private int _appInfoAnimationVersion;
    private LoadedApplication? _currentApplication;
    private ApplicationUpdate? _availableUpdate;
    private bool _discoveryLoaded;
    private readonly Dictionary<string, DateTimeOffset> _recentApplications = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<LoadedApplication> Applications { get; } = [];
    public ObservableCollection<DiscoverableApplication> DiscoverableApplications { get; } = [];
    public ObservableCollection<string> AppCategories { get; } = [];
    public event EventHandler? StateChanged;

    IReadOnlyList<ApplicationSummary> IApplicationShell.Applications =>
        Applications.Select(ToApplicationSummary).ToList();

    IReadOnlyList<RecentApplication> IApplicationShell.RecentApplications => GetRecentApplications();

    public MainWindow()
    {
        InitializeComponent();
        ConfigureWindowChrome();
        DataContext = this;
        AddHandler(InputElement.KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
        AddHandler(InputElement.KeyUpEvent, Window_KeyUp, RoutingStrategies.Tunnel);
        Opened += MainWindow_Opened;
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            DetachApplicationNavigation();
            _catalog.Dispose();
            _systemMetrics.Dispose();
            _updateCancellation.Cancel();
            _updateCancellation.Dispose();
            _updateService.Dispose();
            _discoveryService.Dispose();
        };
        Deactivated += (_, _) => ResetShortcutState();
        CurrentVersionText.Text = $"v{GitHubUpdateService.CurrentVersion}";
    }

    private async void MainWindow_Opened(object? sender, EventArgs e)
    {
        LoadApplications();
        await CheckForUpdatesAsync(silent: true);
    }

    private string UserAppsDirectory => ApplicationPaths.UserAppsDirectory;

    private void LoadApplications(bool activateFirst = true)
    {
        DetachApplicationNavigation();
        _currentApplication = null;
        ApplicationContent.Content = null;
        var bundledAppsDirectory = Path.Combine(AppContext.BaseDirectory, "Apps");
        var packageCacheDirectory = Path.Combine(ApplicationPaths.ProductDataDirectory, "AppCache");
        var result = _catalog.Load([bundledAppsDirectory, UserAppsDirectory], packageCacheDirectory);
        _applicationsLoaded = true;
        Applications.Clear();
        foreach (var application in result.Applications) Applications.Add(application);
        AppCategories.Clear();
        AppCategories.Add("全部");
        foreach (var category in Applications.Select(application => application.Category)
                     .Where(category => !string.IsNullOrWhiteSpace(category))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(category => category))
            AppCategories.Add(category);
        StateChanged?.Invoke(this, EventArgs.Empty);

        EmptyState.IsVisible = Applications.Count == 0;
        if (Applications.Count == 0)
        {
            EmptyMessage.Text = result.Failures.Count == 0
                ? $"请把应用目录或 .appbundle 放入 {UserAppsDirectory} 后重新启动"
                : result.Failures[0].Message;
            return;
        }

        if (activateFirst) OpenApplication(Applications[0]);
    }

    private void OpenApplication(LoadedApplication application)
    {
        HideAppSwitcher();
        try
        {
            SettingsPanel.IsVisible = false;
            HideAppInfo();
            ApplicationContent.Content = application.GetOrCreateView(this, _systemMetrics);
            EmptyState.IsVisible = false;
            SetCurrentApplication(application);
            UpdateCapsuleState();
            WindowTitleText.Text = $"星栈  ·  {application.Name}";
            Title = $"星栈 · {application.Name}";
            _recentApplications[application.Manifest.Id] = DateTimeOffset.Now;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            ApplicationContent.Content = null;
            EmptyState.IsVisible = true;
            DetachApplicationNavigation();
            _currentApplication = null;
            UpdateCapsuleState();
            EmptyMessage.Text = $"{application.Name} 加载失败：{exception.GetBaseException().Message}";
        }
    }

    public void ShowAndActivate()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    public bool TryExecuteApplicationAction(string applicationId, string actionId)
    {
        if (!_applicationsLoaded) LoadApplications();
        var application = Applications.FirstOrDefault(item =>
            string.Equals(item.Manifest.Id, applicationId, StringComparison.OrdinalIgnoreCase));
        if (application is null) return false;

        try
        {
            var action = application.GetQuickActions(this, _systemMetrics).FirstOrDefault(item =>
                string.Equals(item.Id, actionId, StringComparison.OrdinalIgnoreCase));
            if (action is null) return false;
            action.Execute();
            return true;
        }
        catch
        {
            return false;
        }
    }

    void IApplicationShell.OpenApplication(string applicationId)
    {
        PostAfterInput(() =>
        {
            var application = Applications.FirstOrDefault(item =>
                string.Equals(item.Manifest.Id, applicationId, StringComparison.OrdinalIgnoreCase));
            if (application is not null) OpenApplication(application);
        });
    }

    void IApplicationShell.ShowSettings() => PostAfterInput(ShowSettings);

    void IApplicationShell.ShowApplicationSwitcher() => ShowAppSwitcher();

    public void PrepareForShutdown() => _allowClose = true;

    private static ApplicationSummary ToApplicationSummary(LoadedApplication application) => new(
        application.Manifest.Id,
        application.Name,
        application.Description,
        application.Version,
        application.Manifest.Icon,
        application.Category);

    private IReadOnlyList<RecentApplication> GetRecentApplications()
    {
        var recent = new List<RecentApplication>();
        foreach (var item in _recentApplications.OrderByDescending(item => item.Value))
        {
            var application = Applications.FirstOrDefault(candidate =>
                string.Equals(candidate.Manifest.Id, item.Key, StringComparison.OrdinalIgnoreCase));
            if (application is not null)
                recent.Add(new RecentApplication(ToApplicationSummary(application), item.Value));
        }
        return recent;
    }

    private void TitleLogo_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        if (e.ClickCount >= 3) FlyoutBase.ShowAttachedFlyout(TitleLogo);
    }

    private void SettingsMenuItem_Click(object? sender, RoutedEventArgs e) => PostAfterInput(ShowSettings);

    private void ApplicationSwitcherMenuItem_Click(object? sender, RoutedEventArgs e) => PostAfterInput(ShowAppSwitcher);

    private void CapsuleMore_Click(object? sender, RoutedEventArgs e) => PostAfterInput(ShowAppInfo);

    private void CapsuleBack_Click(object? sender, RoutedEventArgs e)
    {
        if (SettingsPanel.IsVisible)
        {
            if (_currentApplication is not null) PostAfterInput(() => OpenApplication(_currentApplication));
            return;
        }

        _currentApplication?.Navigation?.GoBack();
    }

    private void CapsuleHome_Click(object? sender, RoutedEventArgs e)
    {
        var home = Applications.FirstOrDefault(application =>
            string.Equals(application.Manifest.Id, "home", StringComparison.OrdinalIgnoreCase));
        if (home is not null) PostAfterInput(() => OpenApplication(home));
    }

    private async void ShowAppInfo()
    {
        if (_currentApplication is null ||
            string.Equals(_currentApplication.Manifest.Id, "home", StringComparison.OrdinalIgnoreCase)) return;
        CapsuleInfoButton.Classes.Set("selected", true);
        AppInfoIcon.Data = _currentApplication.IconGeometry;
        AppInfoName.Text = _currentApplication.Name;
        AppInfoDescription.Text = _currentApplication.Description;
        AppInfoVersion.Text = _currentApplication.Version;
        AppInfoCategory.Text = _currentApplication.Category;
        var recentApplications = _recentApplications
            .Where(item => !string.Equals(item.Key, _currentApplication.Manifest.Id, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Value)
            .Select(item => Applications.FirstOrDefault(application =>
                string.Equals(application.Manifest.Id, item.Key, StringComparison.OrdinalIgnoreCase)))
            .Where(application => application is not null)
            .Cast<LoadedApplication>()
            .Take(4)
            .ToList();
        AppInfoRecentApplications.ItemsSource = recentApplications;
        AppInfoRecentApplications.IsVisible = recentApplications.Count > 0;
        AppInfoRecentEmpty.IsVisible = recentApplications.Count == 0;
        var animationVersion = ++_appInfoAnimationVersion;
        AppInfoOverlay.IsVisible = true;
        await Task.Delay(20);
        if (animationVersion != _appInfoAnimationVersion) return;
        AppInfoOverlay.Opacity = 1;
        if (AppInfoDrawer.RenderTransform is TranslateTransform transform) transform.X = 0;
    }

    private async void HideAppInfo()
    {
        CapsuleInfoButton.Classes.Set("selected", false);
        if (!AppInfoOverlay.IsVisible) return;
        var animationVersion = ++_appInfoAnimationVersion;
        AppInfoOverlay.Opacity = 0;
        if (AppInfoDrawer.RenderTransform is TranslateTransform transform) transform.X = 380;
        await Task.Delay(230);
        if (animationVersion == _appInfoAnimationVersion) AppInfoOverlay.IsVisible = false;
    }

    private void CloseAppInfo_Click(object? sender, RoutedEventArgs e) => PostAfterInput(HideAppInfo);

    private void AppInfoRecentApplication_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LoadedApplication application }) return;
        HideAppInfo();
        PostAfterInput(() => OpenApplication(application));
    }

    private void AppInfoOverlay_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PostAfterInput(HideAppInfo);
        e.Handled = true;
    }

    private void AppInfoDrawer_PointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private void SetCurrentApplication(LoadedApplication application)
    {
        if (ReferenceEquals(_currentApplication, application)) return;
        DetachApplicationNavigation();
        _currentApplication = application;
        if (_currentApplication.Navigation is not null)
            _currentApplication.Navigation.NavigationStateChanged += ApplicationNavigation_StateChanged;
    }

    private void DetachApplicationNavigation()
    {
        if (_currentApplication?.Navigation is not null)
            _currentApplication.Navigation.NavigationStateChanged -= ApplicationNavigation_StateChanged;
    }

    private void ApplicationNavigation_StateChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(UpdateCapsuleState);

    private void UpdateCapsuleState()
    {
        var hostCanGoBack = SettingsPanel.IsVisible;
        var isHome = _currentApplication is null ||
                     string.Equals(_currentApplication.Manifest.Id, "home", StringComparison.OrdinalIgnoreCase);
        var applicationCanGoBack = !hostCanGoBack && _currentApplication?.Navigation?.CanGoBack == true;

        CapsuleBackButton.IsVisible = hostCanGoBack || applicationCanGoBack;
        CapsuleBackSeparator.IsVisible = applicationCanGoBack;
        CapsuleInfoButton.IsVisible = !hostCanGoBack && !isHome;
        CapsuleInfoHomeSeparator.IsVisible = !hostCanGoBack && !isHome;
        CapsuleHomeButton.IsVisible = !hostCanGoBack && !isHome;

        var visible = hostCanGoBack || !isHome;
        var width = hostCanGoBack
            ? HostBackCapsuleWidth
            : applicationCanGoBack ? ExpandedBackCapsuleWidth : ExpandedCapsuleWidth;
        SetMiniAppCapsuleVisible(visible, width);
    }

    private async void SetMiniAppCapsuleVisible(bool visible, double expandedWidth = ExpandedCapsuleWidth)
    {
        var animationVersion = ++_capsuleAnimationVersion;
        if (visible)
        {
            MiniAppCapsule.IsVisible = true;
            WindowControlCapsule.Width = expandedWidth;
            await Task.Delay(35);
            if (animationVersion == _capsuleAnimationVersion) MiniAppCapsule.Opacity = 1;
            return;
        }

        MiniAppCapsule.Opacity = 0;
        WindowControlCapsule.Width = CollapsedCapsuleWidth;
        await Task.Delay(250);
        if (animationVersion == _capsuleAnimationVersion) MiniAppCapsule.IsVisible = false;
    }

    private void OpenLoadedApplication_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LoadedApplication application }) return;
        PostAfterInput(() => OpenApplication(application));
    }

    private void ShowSettings()
    {
        HideAppSwitcher();
        HideAppInfo();
        ApplicationContent.Content = null;
        EmptyState.IsVisible = false;
        SettingsPanel.IsVisible = true;
        UpdateCapsuleState();
        ShowSettingsSection(SettingsSection.Applications);
        WindowTitleText.Text = "星栈  ·  设置";
        Title = "星栈 · 设置";
    }

    private void ApplicationsSettingsNav_Click(object? sender, RoutedEventArgs e)
        => ShowSettingsSection(SettingsSection.Applications);

    private void InstalledApplicationsTab_Click(object? sender, RoutedEventArgs e)
        => ShowSettingsSection(SettingsSection.Applications);

    private async void DiscoverApplicationsTab_Click(object? sender, RoutedEventArgs e)
    {
        ShowSettingsSection(SettingsSection.Discover);
        if (!_discoveryLoaded) await RefreshDiscoveryAsync();
    }

    private void ShortcutsSettingsNav_Click(object? sender, RoutedEventArgs e)
        => ShowSettingsSection(SettingsSection.Shortcuts);

    private void UpdateSettingsNav_Click(object? sender, RoutedEventArgs e)
        => ShowSettingsSection(SettingsSection.Update);

    private void ShowSettingsSection(SettingsSection section)
    {
        var showApplications = section == SettingsSection.Applications;
        var showDiscover = section == SettingsSection.Discover;
        var showApplicationManagement = showApplications || showDiscover;
        var showShortcuts = section == SettingsSection.Shortcuts;
        var showUpdate = section == SettingsSection.Update;
        SettingsApplicationsSection.IsVisible = showApplications;
        SettingsDiscoverSection.IsVisible = showDiscover;
        SettingsShortcutsSection.IsVisible = showShortcuts;
        SettingsUpdateSection.IsVisible = showUpdate;
        ApplicationManagementTabs.IsVisible = showApplicationManagement;
        SettingsSectionSubtitle.Text = showApplications ? "应用管理 · 已安装" :
            showDiscover ? "应用管理 · 发现" : showShortcuts ? "快捷键" : "软件更新";
        ApplicationsSettingsNavButton.Classes.Set("selected", showApplicationManagement);
        ShortcutsSettingsNavButton.Classes.Set("selected", showShortcuts);
        UpdateSettingsNavButton.Classes.Set("selected", showUpdate);
        InstalledApplicationsTabButton.Classes.Set("selected", showApplications);
        DiscoverApplicationsTabButton.Classes.Set("selected", showDiscover);
    }

    private async void RefreshDiscovery_Click(object? sender, RoutedEventArgs e) => await RefreshDiscoveryAsync();

    private async Task RefreshDiscoveryAsync()
    {
        RefreshDiscoveryButton.IsEnabled = false;
        DiscoveryStatusText.Text = "正在从 GitHub 获取轻应用目录…";
        try
        {
            var applications = await _discoveryService.GetCatalogAsync(_updateCancellation.Token);
            DiscoverableApplications.Clear();
            foreach (var application in applications) DiscoverableApplications.Add(application);
            _discoveryLoaded = true;
            DiscoveryEmptyState.IsVisible = applications.Count == 0;
            DiscoveryStatusText.Text = applications.Count == 0
                ? "目录已刷新。"
                : $"已找到 {applications.Count} 个可下载的轻应用。";
        }
        catch (OperationCanceledException) when (_updateCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            DiscoveryStatusText.Text = $"获取轻应用失败：{exception.GetBaseException().Message}";
        }
        finally
        {
            RefreshDiscoveryButton.IsEnabled = true;
        }
    }

    private async void InstallDiscoveredApplication_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DiscoverableApplication application } button) return;
        button.IsEnabled = false;
        RefreshDiscoveryButton.IsEnabled = false;
        DiscoveryProgress.Value = 0;
        DiscoveryProgress.IsVisible = true;
        DiscoveryStatusText.Text = $"正在下载 {application.Name} {application.Version}…";
        try
        {
            var progress = new Progress<double>(value => DiscoveryProgress.Value = value * 100);
            var packagePath = await _discoveryService.DownloadAsync(application, progress, _updateCancellation.Token);
            var installedPath = ApplicationInstaller.InstallPackage(packagePath, UserAppsDirectory);
            LoadApplications(activateFirst: false);
            ShowSettings();
            ShowSettingsSection(SettingsSection.Discover);
            DiscoveryStatusText.Text = $"{application.Name} {application.Version} 已安装：{installedPath}";
        }
        catch (OperationCanceledException) when (_updateCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            DiscoveryStatusText.Text = $"安装失败：{exception.GetBaseException().Message}";
        }
        finally
        {
            button.IsEnabled = true;
            RefreshDiscoveryButton.IsEnabled = true;
            DiscoveryProgress.IsVisible = false;
        }
    }

    private async void CheckUpdate_Click(object? sender, RoutedEventArgs e)
        => await CheckForUpdatesAsync(silent: false);

    private async Task CheckForUpdatesAsync(bool silent)
    {
        if (silent && !GitHubUpdateService.IsAutomaticCheckDue()) return;
        CheckUpdateButton.IsEnabled = false;
        if (!silent) UpdateStatusText.Text = "正在检查 GitHub Releases…";
        try
        {
            _availableUpdate = await _updateService.CheckAsync(_updateCancellation.Token);
            GitHubUpdateService.MarkCheckCompleted();
            if (_availableUpdate is null)
            {
                UpdateStatusText.Text = $"当前已是最新版本（v{GitHubUpdateService.CurrentVersion}）。";
                UpdateActionPanel.IsVisible = false;
                ReleaseNotesCard.IsVisible = false;
                UpdateSettingsNavText.Text = "软件更新";
                return;
            }

            UpdateStatusText.Text = $"发现新版本 {_availableUpdate.DisplayVersion}，可下载适用于当前设备的安装包。";
            ReleaseNameText.Text = _availableUpdate.ReleaseName;
            ReleaseNotesText.Text = string.IsNullOrWhiteSpace(_availableUpdate.ReleaseNotes)
                ? "此版本没有发布说明。"
                : _availableUpdate.ReleaseNotes;
            UpdateActionPanel.IsVisible = true;
            ReleaseNotesCard.IsVisible = true;
            UpdateSettingsNavText.Text = "软件更新 · 新版本";
        }
        catch (OperationCanceledException) when (_updateCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!silent) UpdateStatusText.Text = $"检查更新失败：{exception.GetBaseException().Message}";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async void DownloadUpdate_Click(object? sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null) return;
        DownloadUpdateButton.IsEnabled = false;
        CheckUpdateButton.IsEnabled = false;
        UpdateProgress.Value = 0;
        UpdateProgress.IsVisible = true;
        UpdateStatusText.Text = $"正在从 GitHub 下载 {_availableUpdate.AssetName}…";
        try
        {
            var progress = new Progress<double>(value => UpdateProgress.Value = value * 100);
            var path = await _updateService.DownloadAsync(_availableUpdate, progress, _updateCancellation.Token);
            UpdateStatusText.Text = "下载完成，正在打开安装包。安装时请按系统提示操作。";
            GitHubUpdateService.OpenInstaller(path);
        }
        catch (OperationCanceledException) when (_updateCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            UpdateStatusText.Text = $"下载更新失败：{exception.GetBaseException().Message}";
            DownloadUpdateButton.IsEnabled = true;
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
            UpdateProgress.IsVisible = false;
        }
    }

    private void OpenReleasePage_Click(object? sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null) return;
        Process.Start(new ProcessStartInfo(_availableUpdate.ReleasePage.AbsoluteUri) { UseShellExecute = true });
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            _spaceKeyDown = true;
            return;
        }

        if (e.Key == Key.A && _spaceKeyDown && !_appSwitcherShortcutTriggered)
        {
            _appSwitcherShortcutTriggered = true;
            ShowAppSwitcher();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && AppInfoOverlay.IsVisible)
        {
            PostAfterInput(HideAppInfo);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && AppSwitcherOverlay.IsVisible)
        {
            PostAfterInput(HideAppSwitcher);
            e.Handled = true;
        }
    }

    private void Window_KeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) _spaceKeyDown = false;
        if (e.Key is Key.Space or Key.A) _appSwitcherShortcutTriggered = false;
    }

    private void ResetShortcutState()
    {
        _spaceKeyDown = false;
        _appSwitcherShortcutTriggered = false;
    }

    private void ShowAppSwitcher()
    {
        ApplyAppCategory("全部");
        AppSwitcherOverlay.IsVisible = true;
    }

    private void AppCategory_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string category }) ApplyAppCategory(category);
    }

    private void ApplyAppCategory(string category)
    {
        var applications = category == "全部"
            ? Applications.ToList()
            : Applications.Where(application =>
                string.Equals(application.Category, category, StringComparison.OrdinalIgnoreCase)).ToList();
        AppSwitcherItems.ItemsSource = applications;
        AppSwitcherEmptyText.IsVisible = applications.Count == 0;
        AppSwitcherItems.IsVisible = applications.Count > 0;
    }

    private void HideAppSwitcher() => AppSwitcherOverlay.IsVisible = false;

    private void OpenSwitcherApplication_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LoadedApplication application })
            PostAfterInput(() => OpenApplication(application));
    }

    private void CloseAppSwitcher_Click(object? sender, RoutedEventArgs e) => PostAfterInput(HideAppSwitcher);

    private void AppSwitcherOverlay_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PostAfterInput(HideAppSwitcher);
        e.Handled = true;
    }

    private void AppSwitcherCard_PointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private async void LoadApplicationPackage_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择应用包",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("应用包") { Patterns = ["*.appbundle"] }]
        });
        var path = files.FirstOrDefault()?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        await InstallApplicationAsync(() => ApplicationInstaller.InstallPackage(path, UserAppsDirectory));
    }

    private async void LoadApplicationDirectory_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择包含 app.json 的应用目录",
            AllowMultiple = false
        });
        var path = folders.FirstOrDefault()?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        await InstallApplicationAsync(() => ApplicationInstaller.InstallDirectory(path, UserAppsDirectory));
    }

    private async Task InstallApplicationAsync(Func<string> install)
    {
        LoadPackageButton.IsEnabled = false;
        LoadDirectoryButton.IsEnabled = false;
        InstallProgress.IsVisible = true;
        SettingsStatusText.Text = "正在加载应用…";

        string status;
        try
        {
            ApplicationContent.Content = null;
            _catalog.Dispose();
            Applications.Clear();
            var installedPath = await Task.Run(install);
            status = $"应用已加载：{installedPath}";
        }
        catch (Exception exception)
        {
            status = $"加载失败：{exception.GetBaseException().Message}";
        }
        finally
        {
            LoadApplications(activateFirst: false);
            ShowSettings();
            InstallProgress.IsVisible = false;
            LoadPackageButton.IsEnabled = true;
            LoadDirectoryButton.IsEnabled = true;
        }

        SettingsStatusText.Text = status;
    }

    private void ConfigureWindowChrome()
    {
        WindowDecorations = WindowDecorations.BorderOnly;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 40;
        if (OperatingSystem.IsWindows())
            TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.Blur];
        base.PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty) UpdateMaximizeGlyph();
        };
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) ToggleMaximize();
        else BeginMoveDrag(e);
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object? sender, RoutedEventArgs e) => PostAfterInput(Hide);

    private static void PostAfterInput(Action action) =>
        Dispatcher.UIThread.Post(action, DispatcherPriority.Background);

    private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
            return;
        e.Cancel = true;
        Hide();
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaximizeGlyph();
    }

    private void UpdateMaximizeGlyph()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        MaximizeButton.Classes.Set("selected", isMaximized);
        ToolTip.SetTip(MaximizeButton, isMaximized ? "还原" : "最大化");
        MaximizeIcon.Data = Geometry.Parse(isMaximized
            ? "M5,8 L16,8 L16,19 L5,19 Z M8,5 L19,5 L19,16"
            : "M6,6 L18,6 L18,18 L6,18 Z");
    }
}
