using AsterDock.Host.Modules;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace AsterDock.Host.Views;

public partial class MainWindow
{
    private readonly Dictionary<LoadedApplication, Window> _detachedApplications = [];
    private bool _closingDetachedApplications;

    private void CapsulePopOut_Click(object? sender, RoutedEventArgs e)
    {
        var application = _currentApplication;
        if (application is null || SettingsPanel.IsVisible || application.Manifest.Id == "home") return;
        PostAfterInput(() => OpenDetachedApplication(application));
    }

    private void OpenDetachedApplication(LoadedApplication application)
    {
        if (_detachedApplications.TryGetValue(application, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var view = application.GetOrCreateView(this, _systemMetrics);
        var window = new Window
        {
            Title = $"{application.Name} · 星栈",
            Width = Math.Max(900, Bounds.Width - 80),
            Height = Math.Max(640, Bounds.Height - 40),
            MinWidth = 640,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        if (ReferenceEquals(ApplicationContent.Content, view)) ApplicationContent.Content = null;
        window.Content = view;
        _detachedApplications.Add(application, window);
        application.SetWindowOwner(window);
        window.Closed += (_, _) =>
        {
            window.Content = null;
            _detachedApplications.Remove(application);
            application.SetWindowOwner(this);
            if (_closingDetachedApplications) return;
            if (ReferenceEquals(_currentApplication, application))
                ApplicationContent.Content = view;
        };

        try
        {
            window.Show();
            if (ReferenceEquals(_currentApplication, application))
                ApplicationContent.Content = CreateDetachedPlaceholder(application);
            HideAppInfo();
        }
        catch
        {
            window.Content = null;
            _detachedApplications.Remove(application);
            application.SetWindowOwner(this);
            if (ReferenceEquals(_currentApplication, application)) ApplicationContent.Content = view;
            window.Close();
        }
    }

    private Control CreateDetachedPlaceholder(LoadedApplication application)
    {
        var activate = new Button { Content = "显示独立窗口", HorizontalAlignment = HorizontalAlignment.Center };
        activate.Classes.Add("accent");
        activate.Click += (_, _) => OpenDetachedApplication(application);
        var restore = new Button { Content = "移回主窗口", HorizontalAlignment = HorizontalAlignment.Center };
        restore.Click += (_, _) =>
        {
            if (_detachedApplications.TryGetValue(application, out var window)) window.Close();
        };
        return new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = $"{application.Name}已在独立窗口打开", FontSize = 20 },
                activate,
                restore
            }
        };
    }

    private void CloseDetachedApplications()
    {
        _closingDetachedApplications = true;
        try
        {
            foreach (var window in _detachedApplications.Values.ToArray()) window.Close();
            _detachedApplications.Clear();
        }
        finally
        {
            _closingDetachedApplications = false;
        }
    }
}
