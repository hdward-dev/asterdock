using System.Diagnostics;
using AsterDock.Host.Modules;
using AsterDock.UI.Controls;
using Avalonia.Controls;
using Avalonia.Interactivity;

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
            if (!existing.IsVisible) existing.Show();
            existing.Activate();
            return;
        }

        // The page already measured inside the main window stays there. The independent
        // window gets a new view so the two windows never share a layout manager.
        var detachedView = application.CreateDetachedView(this, _systemMetrics);
        var page = application.GetOrCreateView(this, _systemMetrics);
        if (ReferenceEquals(detachedView, page) || FeedbackWindow.HasLayoutManager(detachedView))
        {
            Trace.TraceError("独立窗口需要一个尚未布局的新页面，已取消打开");
            return;
        }

        var window = new Window
        {
            Title = $"{application.Name} · 星栈",
            Width = Math.Max(900, Bounds.Width - 80),
            Height = Math.Max(640, Bounds.Height - 40),
            MinWidth = 640,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = FeedbackWindow.CreateHost(detachedView, _feedbackService)
        };
        window.Closed += (_, _) =>
        {
            _detachedApplications.Remove(application);
            if (!_closingDetachedApplications) application.SetWindowOwner(this);
        };
        _detachedApplications.Add(application, window);
        application.SetWindowOwner(window);
        try
        {
            window.Show();
            HideAppInfo();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceError("独立窗口未能打开: " + exception);
            _detachedApplications.Remove(application);
            application.SetWindowOwner(this);
            try { window.Close(); }
            catch (Exception close) when (close is not OutOfMemoryException)
            {
                Trace.TraceError("独立窗口未能关闭: " + close);
            }
        }
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
