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
    private WriteableBitmap? _trayBitmap;
    private bool _trayDisposed;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        _trayIcon = TrayIcon.GetIcons(this)?.Single()
            ?? throw new InvalidOperationException("托盘图标未完成初始化");
        _trayBitmap = TrayIconFactory.CreateApplicationIcon();
        _trayIcon.Icon = new WindowIcon(_trayBitmap);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _mainWindow = new MainWindow
            {
                Icon = _trayBitmap is null ? null : new WindowIcon(_trayBitmap)
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
