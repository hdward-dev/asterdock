using System.Diagnostics;
using AsterDock.Contracts;
using AsterDock.UI.Services;
using Tmds.DBus.Protocol;

namespace AsterDock.UI.Checks;

internal static class SystemNotificationChecks
{
    public static void Run(Action<bool, string> check)
    {
        RunRouter(check);
        RunService(check);
        RunContent(check);
        RunPlatforms(check);
        RunDBus(check);
    }

    private static void RunRouter(Action<bool, string> check)
    {
        check(FeedbackRouter.Decide(FeedbackDelivery.Auto, false, false, false, true) == FeedbackRoute.InAppImmediate,
            "auto without presence stays in the window");
        check(FeedbackRouter.Decide(FeedbackDelivery.Auto, true, true, true, true) == FeedbackRoute.InAppImmediate,
            "foreground visible page stays in the window");
        check(FeedbackRouter.Decide(FeedbackDelivery.Auto, true, false, true, true) == FeedbackRoute.SystemOnly,
            "unfocused window routes to the system");
        check(FeedbackRouter.Decide(FeedbackDelivery.Auto, true, true, false, true) == FeedbackRoute.SystemOnly,
            "hidden module page routes to the system");
        check(FeedbackRouter.Decide(FeedbackDelivery.Auto, true, false, false, false) == FeedbackRoute.InAppDeferred,
            "unavailable system queues until the page is shown");
        check(FeedbackRouter.Decide(FeedbackDelivery.System, true, true, true, false) == FeedbackRoute.InAppImmediate,
            "system delivery shows immediately when the page is already visible");
        check(FeedbackRouter.Decide(FeedbackDelivery.System, true, true, false, false) == FeedbackRoute.InAppDeferred,
            "system delivery queues when the page is hidden");
        check(FeedbackRouter.Decide(FeedbackDelivery.InApp, true, false, false, true) == FeedbackRoute.InAppImmediate,
            "in-app delivery ignores focus");
        check(FeedbackRouter.Decide(FeedbackDelivery.Both, true, false, false, true) == FeedbackRoute.BothDeferred,
            "both delivery defers the window prompt when the page is hidden");
        check(FeedbackRouter.Decide(FeedbackDelivery.Both, true, true, true, true) == FeedbackRoute.BothImmediate,
            "both delivery shows the window prompt when the page is visible");
    }

    private static void RunService(Action<bool, string> check)
    {
        var posted = new List<Action>();
        var clock = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var backend = new FakeBackend();
        var presence = new FakePresence { Foreground = true, Page = true };
        var service = new UserFeedbackService(posted.Add)
        {
            Clock = () => clock,
            Presence = presence,
            SystemNotifications = backend,
            SystemNotificationThrottle = TimeSpan.FromSeconds(8)
        };
        var activated = new List<SystemNotificationRequest>();
        service.SystemNotificationActivated += (_, request) => activated.Add(request);

        service.Toast(FeedbackKind.Success, "已保存");
        check(backend.Published.Count == 0 && service.VisibleToasts.Count == 1, "active visible page keeps the toast in-app");

        presence.Page = false;
        service.Toast(FeedbackKind.Warning, "连接已断开", "串口");
        check(backend.Published.Count == 1 && backend.Published[0].ModuleId is null && service.VisibleToasts.Count == 1,
            "hidden page routes a host toast to the system");
        service.Toast(FeedbackKind.Warning, "连接已断开", "串口");
        check(backend.Published.Count == 1 && service.DeferredCount == 0, "identical system notifications are throttled");
        clock = clock.AddSeconds(9);
        var scoped = new ModuleFeedbackService(service, "serial");
        scoped.Toast(FeedbackKind.Warning, "连接已断开", "串口");
        check(backend.Published.Count == 2 && backend.Published[1].ModuleId == "serial", "another module is not throttled");

        presence.AllowSystemNotifications = false;
        service.Toast(FeedbackKind.Info, "仍要留下", delivery: FeedbackDelivery.System);
        check(backend.Published.Count == 2 && service.DeferredCount == 1, "disabled setting queues instead of notifying");
        presence.Page = true;
        service.RefreshDelivery();
        check(service.DeferredCount == 0 && service.VisibleToasts.Any(item => item.Message == "仍要留下"),
            "queued toast appears when the page is shown");

        presence.Page = false;
        presence.AllowSystemNotifications = true;
        backend.Available = false;
        service.ShowInfoAsync("排队", "服务不在", delivery: FeedbackDelivery.System);
        check(service.DeferredCount == 1, "missing backend queues the dialog");
        backend.Available = true;
        presence.Page = false;
        service.Toast(FeedbackKind.Error, "页面藏着也要看见", delivery: FeedbackDelivery.InApp);
        check(service.VisibleToasts.Any(item => item.Message == "页面藏着也要看见"), "in-app delivery is immediate while hidden");
        var before = backend.Published.Count;
        service.Toast(FeedbackKind.Success, "两边都要", delivery: FeedbackDelivery.Both);
        check(backend.Published.Count == before + 1 && service.DeferredCount == 2, "both publishes and queues while hidden");

        presence.Foreground = false;
        var error = service.ShowErrorFor("serial", "订阅失败", "未能更新", new InvalidOperationException("stack-line"), default, FeedbackDelivery.System);
        check(error.IsCompletedSuccessfully && backend.Published[^1].OpenErrorDialog, "system error completes when posted");
        check(backend.Published[^1].Details!.Contains("stack-line", StringComparison.Ordinal), "system error keeps the exception text");
        backend.Raise(backend.Published[^1].Token);
        posted.ForEach(action => action());
        posted.Clear();
        check(activated.Count == 1 && activated[0].ModuleId == "serial" && activated[0].OpenErrorDialog, "activation returns the module and error");
        var replay = new NotificationDetailsException(activated[0].Details!);
        check(replay.ToString().Contains("stack-line", StringComparison.Ordinal), "replayed error uses the stored details");

        var confirm = service.ConfirmAsync("确认", "继续");
        check(!confirm.IsCompleted && service.ActiveDialog?.Tone == DialogTone.Confirm && backend.Published.Count == before + 2,
            "confirmation stays in the window");
        service.CancelActiveDialog();
    }

    private static void RunContent(Action<bool, string> check)
    {
        var error = new SystemNotificationRequest
        {
            Token = "abc&x",
            Kind = FeedbackKind.Error,
            Title = "失败 <a>",
            Body = "详情",
            OpenErrorDialog = true
        };
        var toast = WindowsToastContent.Create(error, "/tmp/asterdock-notification.png");
        check(toast.Contains("launch=\"token=abc&amp;x\"", StringComparison.Ordinal) && toast.Contains("activationType=\"foreground\"", StringComparison.Ordinal),
            "windows toast carries the activation token");
        check(toast.Contains("duration=\"long\"", StringComparison.Ordinal) && toast.Contains("Notification.Reminder", StringComparison.Ordinal),
            "windows error toast is persistent");
        check(toast.Contains("失败 &lt;a&gt;", StringComparison.Ordinal) && toast.Contains("appLogoOverride", StringComparison.Ordinal),
            "windows toast escapes text and sets the icon");
        var info = WindowsToastContent.Create(new SystemNotificationRequest { Token = "i", Kind = FeedbackKind.Info, Body = "仅正文" }, null);
        check(info.Contains("silent=\"true\"", StringComparison.Ordinal) && info.Contains(">仅正文<", StringComparison.Ordinal),
            "windows info toast is silent");
        check(WindowsToastBackend.ReadActivationToken("token=abc&x") == "abc", "windows activation reads the token");

        var message = FreedesktopNotifyMessage.Create(error, "/tmp/asterdock-notification.png");
        check(message.AppName == "星栈" && message.Urgency == 2 && message.ExpireTimeout == 0, "linux error is critical and persistent");
        check(message.DesktopEntry == "asterdock" && message.Actions.SequenceEqual(["default", "打开"]), "linux action and desktop entry");
        check(message.ImagePath == "/tmp/asterdock-notification.png" && message.Summary == "失败 <a>", "linux icon and summary");
        check(FreedesktopNotifyMessage.UrgencyFor(FeedbackKind.Info) == 0 && FreedesktopNotifyMessage.ExpireFor(FeedbackKind.Warning) == 8000,
            "linux urgency and expiry follow the kind");
    }

    private static void RunPlatforms(Action<bool, string> check)
    {
        var request = new SystemNotificationRequest { Token = "t", Kind = FeedbackKind.Info, Body = "x" };
        var windows = new WindowsToastBackend();
        var mac = new MacNotificationBackend();
        check(!windows.IsAvailable && !windows.TryPublish(request), "windows toast is unavailable on this host");
        check(!mac.IsAvailable && !mac.TryPublish(request), "macos notification is unavailable on this host");
        using var missing = new FreedesktopNotificationBackend(() => null);
        check(!missing.IsAvailable && !missing.TryPublish(request), "missing session bus does not throw");
    }

    private static void RunDBus(Action<bool, string> check)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { RunDBusCore(check); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void RunDBusCore(Action<bool, string> check)
    {
        var socket = "/tmp/ad-note";
        try { File.Delete(socket); } catch (IOException) { }
        using var daemon = Process.Start(new ProcessStartInfo("dbus-daemon", $"--session --nofork --print-address --address=unix:path={socket}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        });
        if (daemon is null)
        {
            check(false, "dbus-daemon started");
            return;
        }

        try
        {
            var read = daemon.StandardOutput.ReadLineAsync();
            if (!read.Wait(TimeSpan.FromSeconds(3)) || string.IsNullOrWhiteSpace(read.Result))
            {
                check(false, "dbus-daemon printed an address: " + daemon.StandardError.ReadToEnd());
                return;
            }

            var address = read.Result.Trim();
            var absent = new FreedesktopNotificationBackend(() => address);
            var sample = new SystemNotificationRequest { Token = "gone", Kind = FeedbackKind.Info, Title = "不在", Body = "没有服务" };
            check(!absent.IsAvailable && !absent.TryPublish(sample), "missing notification service is unavailable");
            absent.Dispose();

            using var server = new DBusConnection(address);
            server.ConnectAsync().AsTask().GetAwaiter().GetResult();
            var handler = new NotifyService();
            server.AddMethodHandler(handler);
            server.RequestNameAsync(FreedesktopNotifyMessage.Service, RequestNameOptions.ReplaceExisting).GetAwaiter().GetResult();

            var activated = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var backend = new FreedesktopNotificationBackend(() => address, () => "/tmp/asterdock-notification.png");
            backend.Activated += (_, args) => activated.TrySetResult(args.Token);
            check(backend.IsAvailable, "private notification service is available");
            var request = new SystemNotificationRequest
            {
                Token = "token-1",
                ModuleId = "serial",
                Kind = FeedbackKind.Error,
                Title = "发送失败",
                Body = "请重新连接",
                OpenErrorDialog = true
            };
            var published = backend.TryPublish(request);
            if (!published)
            {
                try
                {
                    using var probe = new DBusConnection(address);
                    probe.ConnectAsync().AsTask().GetAwaiter().GetResult();
                    var message = FreedesktopNotifyMessage.Create(request, "/tmp/asterdock-notification.png");
                    var sent = probe.CallMethodAsync(
                        FreedesktopNotificationBackend.CreateNotifyMessage(probe, message),
                        static (Message reply, object? _) => reply.GetBodyReader().ReadUInt32(),
                        null);
                    sent.GetAwaiter().GetResult();
                }
                catch (Exception probeException)
                {
                    check(false, "notify call succeeded: " + (probeException.InnerException ?? probeException).Message);
                    return;
                }
            }

            check(published, "notify call succeeded");
            check(handler.AppName == "星栈", "dbus app name is 星栈, was " + handler.AppName);
            check(handler.Urgency == 2 && handler.Expire == 0, "dbus urgency is critical");
            check(handler.Actions.SequenceEqual(["default", "打开"]), "dbus actions are default/打开");
            check(handler.DesktopEntry == "asterdock" && handler.ImagePath == "/tmp/asterdock-notification.png",
                "dbus desktop-entry and image-path");
            handler.EmitClosed(2);
            Thread.Sleep(150);
            handler.EmitAction("default");
            check(activated.Task.Wait(TimeSpan.FromSeconds(2)) && activated.Task.Result == "token-1",
                "action after dismiss still activates the token");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            check(false, "dbus notification round-trip: " + exception.GetType().Name + " " + exception.Message);
        }
        finally
        {
            try { daemon.Kill(entireProcessTree: true); } catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
    }

    private sealed class FakeBackend : ISystemNotificationBackend
    {
        public bool Available { get; set; } = true;
        public bool IsAvailable => Available;
        public List<SystemNotificationRequest> Published { get; } = [];
        public event EventHandler<SystemNotificationActivation>? Activated;
        public bool TryPublish(SystemNotificationRequest request)
        {
            if (!Available) return false;
            Published.Add(request);
            return true;
        }

        public void Raise(string token) => Activated?.Invoke(this, new SystemNotificationActivation { Token = token });
    }

    private sealed class FakePresence : IFeedbackPresence
    {
        public bool AllowSystemNotifications { get; set; } = true;
        public bool Foreground { get; set; }
        public bool Page { get; set; }
        public bool IsWindowForeground(string? moduleId) => Foreground;
        public bool IsPageVisible(string? moduleId) => Page;
    }

    private sealed class NotifyService : IPathMethodHandler
    {
        public string Path => FreedesktopNotifyMessage.Path;
        public bool HandlesChildPaths => false;
        public string? AppName { get; private set; }
        public byte Urgency { get; private set; }
        public string? DesktopEntry { get; private set; }
        public string? ImagePath { get; private set; }
        public int Expire { get; private set; }
        public List<string> Actions { get; } = [];
        public uint Id { get; private set; }
        public string? Client { get; private set; }
        public DBusConnection? Connection { get; set; }

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            try
            {
                Connection ??= context.Connection;
                if (context.Request.MemberAsString != "Notify")
                {
                    context.ReplyUnknownMethodError();
                    return default;
                }

                var reader = context.Request.GetBodyReader();
                AppName = reader.ReadString();
                _ = reader.ReadUInt32();
                var icon = reader.ReadString();
                _ = reader.ReadString();
                _ = reader.ReadString();
                var end = reader.ReadArrayStart(DBusType.String);
                Actions.Clear();
                while (reader.HasNext(end)) Actions.Add(reader.ReadString());
                var hints = reader.ReadDictionaryOfStringToVariantValue();
                Urgency = hints["urgency"].GetByte();
                DesktopEntry = hints["desktop-entry"].GetString();
                ImagePath = hints.TryGetValue("image-path", out var image) ? image.GetString() : icon;
                Expire = reader.ReadInt32();
                Id = 7;
                Client = context.Request.SenderAsString;
                using var writer = context.CreateReplyWriter("u");
                writer.WriteUInt32(Id);
                context.Reply(writer.CreateMessage());
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                if (!context.ReplySent) context.ReplyError("org.freedesktop.DBus.Error.Failed", exception.Message);
            }

            return default;
        }

        public void EmitClosed(uint reason)
        {
            var writer = BeginSignal("NotificationClosed", "uu");
            try
            {
                writer.WriteUInt32(Id);
                writer.WriteUInt32(reason);
                Send(ref writer);
            }
            finally { writer.Dispose(); }
        }

        public void EmitAction(string key)
        {
            var writer = BeginSignal("ActionInvoked", "us");
            try
            {
                writer.WriteUInt32(Id);
                writer.WriteString(key);
                Send(ref writer);
            }
            finally { writer.Dispose(); }
        }

        private MessageWriter BeginSignal(string name, string signature)
        {
            var connection = Connection ?? throw new InvalidOperationException("no connection");
            var writer = connection.GetMessageWriter();
            writer.WriteSignalHeader(Client, Path, FreedesktopNotifyMessage.Interface, name, signature);
            return writer;
        }

        private void Send(ref MessageWriter writer)
        {
            var connection = Connection ?? throw new InvalidOperationException("no connection");
            if (!connection.TrySendMessage(writer.CreateMessage()))
                throw new InvalidOperationException("signal was not sent");
        }
    }
}
