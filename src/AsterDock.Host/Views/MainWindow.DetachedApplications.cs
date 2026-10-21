using System.Diagnostics;
using AsterDock.Host.Modules;
using AsterDock.UI.Controls;
using AsterDock.UI.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;

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
        // The feedback layer is created with the window, before the already-measured page is moved in.
        var root = new Grid();
        root.Children.Add(new AdFeedbackLayer { Service = _feedbackService });
        window.Content = root;
        var wasShowing = ReferenceEquals(ApplicationContent.Content, view);
        if (wasShowing) ApplicationContent.Content = null;
        _detachedApplications.Add(application, window);
        application.SetWindowOwner(window);

        void Fail(Exception exception)
        {
            Trace.TraceError("独立窗口未能打开: " + exception);
            root.Children.Remove(view);
            _detachedApplications.Remove(application);
            application.SetWindowOwner(this);
            if (wasShowing && ReferenceEquals(_currentApplication, application) && view.Parent is null)
                ApplicationContent.Content = view;
            try { window.Close(); }
            catch (Exception close) when (close is not OutOfMemoryException)
            {
                Trace.TraceError("独立窗口未能关闭: " + close);
            }
        }

        window.Closing += (_, _) =>
        {
            try
            {
                if (view.Parent == root) root.Children.Remove(view);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Trace.TraceError("独立窗口移出页面失败: " + exception);
            }
        };
        window.Closed += (_, _) =>
        {
            _detachedApplications.Remove(application);
            application.SetWindowOwner(this);
            if (_closingDetachedApplications) return;
            if (!ReferenceEquals(_currentApplication, application)) return;
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (view.Parent is null && view.GetVisualParent() is null)
                        ApplicationContent.Content = view;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    Trace.TraceError("页面移回主窗口失败: " + exception);
                }
            }, DispatcherPriority.Background);
        };

        FeedbackWindow.Adopt(view, control =>
        {
            if (!_detachedApplications.TryGetValue(application, out var current) || !ReferenceEquals(current, window))
                return;
            try
            {
                root.Children.Insert(0, control);
                window.Show();
                if (wasShowing && ReferenceEquals(_currentApplication, application))
                    ApplicationContent.Content = CreateDetachedPlaceholder(application);
                HideAppInfo();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Fail(exception);
            }
        }, Fail);
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
