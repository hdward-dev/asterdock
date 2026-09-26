using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using AsterDock.Host.Views;

namespace AsterDock.Host;

public partial class App : Application
{
    private MainWindow? _mainWindow;
    private TrayIcon? _trayIcon;
    private Bitmap? _trayBitmap;
    private bool _trayDisposed;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        _trayIcon = TrayIcon.GetIcons(this)?.SingleOrDefault()
            ?? throw new InvalidOperationException("托盘图标未完成初始化");
        _trayBitmap = TrayIconFactory.CreateApplicationIcon();
        _trayIcon.Icon = new WindowIcon(_trayBitmap);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avalonia creates the tray icon object on every platform, but the icon
            // only reaches the user interface when the desktop actually hosts a
            // StatusNotifierItem or AppIndicator service. GNOME and bare X11 sessions
            // frequently have no such host, so hiding the window on close there would
            // leave the container running with no way to bring it back.
            var hideOnClose = !OperatingSystem.IsLinux();
            desktop.ShutdownMode = hideOnClose
                ? ShutdownMode.OnExplicitShutdown
                : ShutdownMode.OnMainWindowClose;
            _mainWindow = new MainWindow
            {
                Icon = _trayBitmap is null ? null : new WindowIcon(_trayBitmap),
                HideOnClose = hideOnClose
            };
            desktop.MainWindow = _mainWindow;
            desktop.Exit += (_, _) => DisposeTrayIcon();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void TrayIcon_Clicked(object? sender, EventArgs e) => ShowMainWindow();

    private void OpenAsterDock_Click(object? sender, EventArgs e) => ShowMainWindow();

    private void ToggleDeviceWidget_Click(object? sender, EventArgs e)
    {
        if (_mainWindow?.TryExecuteApplicationAction("device-information", "toggle-desktop-widget") != true)
            ShowMainWindow();
    }

    private void ExitApplication_Click(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        _mainWindow?.PrepareForShutdown();
        if (_trayIcon is not null) _trayIcon.IsVisible = false;
        desktop.Shutdown();
    }

    private void ShowMainWindow() => _mainWindow?.ShowAndActivate();

    private void DisposeTrayIcon()
    {
        if (_trayDisposed) return;
        _trayDisposed = true;
        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _trayBitmap?.Dispose();
        _trayBitmap = null;
    }
}
