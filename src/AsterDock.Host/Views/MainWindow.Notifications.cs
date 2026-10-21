using AsterDock.Contracts;
using AsterDock.Host.Services;
using AsterDock.UI.Services;
using Avalonia.Controls;

namespace AsterDock.Host.Views;

public partial class MainWindow
{
    private void AttachSystemNotifications()
    {
        _feedbackService.Presence = new HostFeedbackPresence(this);
        _feedbackService.SystemNotifications = SystemNotificationBackends.Create(NotificationIcon.TryGetPath);
        _feedbackService.SystemNotificationActivated += OnSystemNotification;
        Activated += (_, _) => _feedbackService.RefreshDelivery();
        Deactivated += (_, _) => _feedbackService.RefreshDelivery();
    }

    private void OnSystemNotification(object? sender, SystemNotificationRequest request)
    {
        ShowAndActivate();
        if (!string.IsNullOrWhiteSpace(request.ModuleId))
            ((IApplicationShell)this).OpenApplication(request.ModuleId);
        if (!request.OpenErrorDialog) return;
        var details = string.IsNullOrEmpty(request.Details) ? null : new NotificationDetailsException(request.Details);
        var title = string.IsNullOrWhiteSpace(request.Title) ? "错误" : request.Title;
        _ = _feedbackService.ShowErrorAsync(title, request.Body, details, default, FeedbackDelivery.InApp);
    }

    private sealed class HostFeedbackPresence : IFeedbackPresence
    {
        private readonly MainWindow _window;

        public HostFeedbackPresence(MainWindow window) => _window = window;

        public bool AllowSystemNotifications => _window._uiSettings.AllowSystemNotifications;

        public bool IsWindowForeground(string? moduleId)
        {
            var owner = _window.OwnerFor(moduleId);
            return owner.IsActive && owner.IsVisible && owner.WindowState != WindowState.Minimized;
        }

        public bool IsPageVisible(string? moduleId)
        {
            if (moduleId is null)
                return _window.IsVisible && _window.WindowState != WindowState.Minimized;
            var owner = _window.OwnerFor(moduleId);
            if (!ReferenceEquals(owner, _window))
                return owner.IsVisible && owner.WindowState != WindowState.Minimized;
            if (_window.SettingsPanel.IsVisible) return false;
            if (_window._currentApplication is null
                || !string.Equals(_window._currentApplication.Manifest.Id, moduleId, StringComparison.OrdinalIgnoreCase))
                return false;
            return _window.IsVisible && _window.WindowState != WindowState.Minimized;
        }
    }

    private Window OwnerFor(string? moduleId)
    {
        if (moduleId is null) return this;
        foreach (var pair in _detachedApplications)
        {
            if (string.Equals(pair.Key.Manifest.Id, moduleId, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return this;
    }
}
