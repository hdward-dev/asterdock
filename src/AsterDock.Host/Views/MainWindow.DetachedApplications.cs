using System.Diagnostics;
using AsterDock.Contracts;
using AsterDock.Host.Modules;
using AsterDock.UI.Controls;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
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

        var page = application.GetOrCreateView(this, _systemMetrics);
        var detachedView = application.CreateDetachedView(this, _systemMetrics);
        // A module that can build a second, not-yet-measured page keeps the original in the host.
        // A module with one live page (the remote session) is moved instead of opening a blank copy.
        var transfer = ReferenceEquals(detachedView, page) || FeedbackWindow.HasLayoutManager(detachedView);
        if (transfer && !FeedbackWindow.Detach(page))
        {
            Trace.TraceError("独立窗口需要一个尚未布局的新页面，已取消打开");
            _feedbackService.Toast(FeedbackKind.Warning, "无法打开独立窗口：该应用未能提供新页面");
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
        var hosted = transfer
            ? FeedbackWindow.Adopt(window, page, _feedbackService)
            : AssignFresh(window, detachedView);
        if (!hosted)
        {
            if (transfer && ReferenceEquals(_currentApplication, application)) ApplicationContent.Content = page;
            Trace.TraceError("独立窗口需要一个尚未布局的新页面，已取消打开");
            _feedbackService.Toast(FeedbackKind.Warning, "无法打开独立窗口：该应用未能提供新页面");
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
        if (transfer) _transferredPages[application] = page;
        application.SetWindowOwner(window);
        try
        {
            window.Show();
            if (transfer && ReferenceEquals(_currentApplication, application))
                ApplicationContent.Content = CreateDetachedPlaceholder(application);
            HideAppInfo();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceError("独立窗口未能打开: " + exception);
            _detachedApplications.Remove(application);
            _transferredPages.Remove(application);
            application.SetWindowOwner(this);
            if (transfer)
            {
                FeedbackWindow.Release(page);
                if (ReferenceEquals(_currentApplication, application)) ApplicationContent.Content = page;
            }
            try { window.Close(); }
            catch (Exception close) when (close is not OutOfMemoryException)
            {
                Trace.TraceError("独立窗口未能关闭: " + close);
            }
        }
    }

    private bool AssignFresh(Window window, Control detachedView)
    {
        window.Content = FeedbackWindow.CreateHost(detachedView, _feedbackService);
        return true;
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
