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
using AsterDock.Host.Services;

namespace AsterDock.Host.Views;

public partial class MainWindow : Window, IApplicationShell
{
    private enum SettingsSection
    {
        Applications,
        Shortcuts
    }

    private readonly ModuleCatalog _catalog = new();
    private readonly SharedSystemMetricsService _systemMetrics = new();
    private bool _spaceKeyDown;
    private bool _appSwitcherShortcutTriggered;
    private bool _allowClose;
    private bool _applicationsLoaded;
    private readonly Dictionary<string, DateTimeOffset> _recentApplications = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<LoadedApplication> Applications { get; } = [];
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
        Opened += (_, _) => LoadApplications();
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            _catalog.Dispose();
            _systemMetrics.Dispose();
        };
        Deactivated += (_, _) => ResetShortcutState();
    }

    private string UserAppsDirectory => ApplicationPaths.UserAppsDirectory;

    private void LoadApplications(bool activateFirst = true)
    {
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
            ApplicationContent.Content = application.GetOrCreateView(this, _systemMetrics);
            EmptyState.IsVisible = false;
            WindowTitleText.Text = $"星栈  ·  {application.Name}";
            Title = $"星栈 · {application.Name}";
            _recentApplications[application.Manifest.Id] = DateTimeOffset.Now;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            ApplicationContent.Content = null;
            EmptyState.IsVisible = true;
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

    private void OpenLoadedApplication_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LoadedApplication application }) return;
        PostAfterInput(() => OpenApplication(application));
    }

    private void ShowSettings()
    {
        HideAppSwitcher();
        ApplicationContent.Content = null;
        EmptyState.IsVisible = false;
        SettingsPanel.IsVisible = true;
        ShowSettingsSection(SettingsSection.Applications);
        WindowTitleText.Text = "星栈  ·  设置";
        Title = "星栈 · 设置";
    }

    private void ApplicationsSettingsNav_Click(object? sender, RoutedEventArgs e)
        => ShowSettingsSection(SettingsSection.Applications);

    private void ShortcutsSettingsNav_Click(object? sender, RoutedEventArgs e)
        => ShowSettingsSection(SettingsSection.Shortcuts);

    private void ShowSettingsSection(SettingsSection section)
    {
        var showApplications = section == SettingsSection.Applications;
        SettingsApplicationsSection.IsVisible = showApplications;
        SettingsShortcutsSection.IsVisible = !showApplications;
        SettingsSectionSubtitle.Text = showApplications ? "应用管理" : "快捷键";
        ApplicationsSettingsNavButton.Classes.Set("selected", showApplications);
        ShortcutsSettingsNavButton.Classes.Set("selected", !showApplications);
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
        if (OperatingSystem.IsMacOS())
        {
            CustomTitleBar.IsVisible = false;
            WindowRootGrid.RowDefinitions[0].Height = new GridLength(0);
            return;
        }

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
        MaximizeIcon.Data = Geometry.Parse(WindowState == WindowState.Maximized
            ? "M5,8 L16,8 L16,19 L5,19 Z M8,5 L19,5 L19,16"
            : "M6,6 L18,6 L18,18 L6,18 Z");
    }
}
