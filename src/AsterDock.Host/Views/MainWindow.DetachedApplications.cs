using System.Diagnostics;
using AsterDock.Contracts;
using AsterDock.Host.Modules;
using AsterDock.UI.Controls;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AsterDock.Host.Views;

public partial class MainWindow
{
    private readonly Dictionary<LoadedApplication, Window> _detachedApplications = [];
    private readonly Dictionary<LoadedApplication, Control> _transferredPages = [];
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
            if (!existing.IsVisible) existing.Show();
            existing.Activate();
            return;
        }

        // Pop-out moves the page the user is already looking at. A second page would
        // leave the same app on screen twice.
        var page = application.GetOrCreateView(this, _systemMetrics);
        if (!FeedbackWindow.Detach(page))
        {
            Trace.TraceError("当前页面无法从主窗口移出，已取消打开独立窗口");
            _feedbackService.Toast(FeedbackKind.Warning, "无法打开独立窗口：当前页面无法移出主窗口");
            return;
        }

        var window = new Window
        {
            Title = $"{application.Name} · 星栈",
            Width = Math.Max(900, Bounds.Width - 80),
            Height = Math.Max(640, Bounds.Height - 40),
            MinWidth = 640,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        if (!FeedbackWindow.Adopt(window, page, _feedbackService))
        {
            if (ReferenceEquals(_currentApplication, application)) ApplicationContent.Content = page;
            Trace.TraceError("当前页面无法放进独立窗口，已取消打开");
            _feedbackService.Toast(FeedbackKind.Warning, "无法打开独立窗口：当前页面无法移出主窗口");
            return;
        }

        window.Activated += (_, _) => _feedbackService.RefreshDelivery();
        window.PropertyChanged += (_, args) =>
        {
            if (args.Property == Window.WindowStateProperty || args.Property == Window.IsVisibleProperty)
                _feedbackService.RefreshDelivery();
        };
        window.Closed += (_, _) =>
        {
            _detachedApplications.Remove(application);
            var moved = _transferredPages.Remove(application, out var live);
            if (!_closingDetachedApplications) application.SetWindowOwner(this);
            _feedbackService.RefreshDelivery();
            if (!moved || live is null) return;
            var restore = !_closingDetachedApplications && ReferenceEquals(_currentApplication, application);
            Dispatcher.UIThread.Post(() =>
            {
                if (!FeedbackWindow.Release(live)) return;
                if (restore && ReferenceEquals(_currentApplication, application))
                    ApplicationContent.Content = live;
            }, DispatcherPriority.Background);
        };
        _detachedApplications.Add(application, window);
        _transferredPages[application] = page;
        application.SetWindowOwner(window);
        try
        {
            window.Show();
            if (ReferenceEquals(_currentApplication, application))
                ApplicationContent.Content = CreateDetachedPlaceholder(application);
            HideAppInfo();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceError("独立窗口未能打开: " + exception);
            _detachedApplications.Remove(application);
            _transferredPages.Remove(application);
            application.SetWindowOwner(this);
            FeedbackWindow.Release(page);
            if (ReferenceEquals(_currentApplication, application)) ApplicationContent.Content = page;
            try { window.Close(); }
            catch (Exception close) when (close is not OutOfMemoryException)
            {
                Trace.TraceError("独立窗口未能关闭: " + close);
            }
        }
    }

    private Control CreateDetachedPlaceholder(LoadedApplication application)
    {
        var show = new Button
        {
            Content = "显示独立窗口",
            MinWidth = 148,
            MinHeight = 40
        };
        show.Classes.Add("accent");
        show.Click += (_, _) => OpenDetachedApplication(application);
        var restore = new Button
        {
            Content = "移回主窗口",
            MinWidth = 148,
            MinHeight = 40
        };
        restore.Classes.Add("ad-outline");
        restore.Click += (_, _) =>
        {
            if (_detachedApplications.TryGetValue(application, out var window)) window.Close();
        };

        return new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 18,
            Children =
            {
                new TextBlock
                {
                    Text = $"{application.Name}已在独立窗口打开",
                    FontSize = 20,
                    FontWeight = FontWeight.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children = { show, restore }
                }
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
