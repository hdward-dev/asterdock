using AsterDock.Contracts;
using Avalonia;
using AsterDock.UI.Controls;
using AsterDock.UI.Motion;
using AsterDock.UI.Services;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace AsterDock.UI.Checks;

internal static class FeedbackChecks
{
    public static void Run(Action<bool, string> check)
    {
        RunService(check);
        RunCards(check);
        RunLayer(check);
        RunAttach(check);
        RunAdopt(check);
        MotionPreferences.ReduceMotion = false;
        FeedbackMotion.Snap = false;
    }

    private static void RunService(Action<bool, string> check)
    {
        var posted = new List<Action>();
        var service = new UserFeedbackService(posted.Add)
        {
            Clock = () => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };

        var background = Task.Run(() => service.Toast(FeedbackKind.Info, "来自后台"));
        background.GetAwaiter().GetResult();
        check(posted.Count == 1 && service.VisibleToasts.Count == 1, "toast state is published through the UI marshal");
        posted[0]();
        check(service.VisibleToasts[0].Message == "来自后台", "marshalled toast keeps its message");

        service.Toast(FeedbackKind.Info, "来自后台");
        check(service.VisibleToasts.Count == 1 && service.VisibleToasts[0].RepeatCount == 2, "identical toasts collapse");
        posted.Clear();

        while (service.VisibleToasts.Count > 0) service.DismissToast(service.VisibleToasts[0].Id);
        service.MaxVisibleToasts = 2;
        service.Toast(FeedbackKind.Success, "一");
        service.Toast(FeedbackKind.Warning, "二");
        service.Toast(FeedbackKind.Info, "三");
        check(service.VisibleToasts.Count == 2 && service.QueuedToastCount == 1, "toasts beyond the cap stay queued");
        service.DismissToast(service.VisibleToasts[0].Id);
        check(service.VisibleToasts.Count == 2 && service.QueuedToastCount == 0 && service.VisibleToasts.Any(item => item.Message == "三"), "dismissing a toast promotes the queue");

        service.MaxVisibleToasts = 4;
        while (service.VisibleToasts.Count > 0) service.DismissToast(service.VisibleToasts[0].Id);
        var now = service.Clock();
        service.Toast(FeedbackKind.Error, "留下");
        service.Toast(FeedbackKind.Success, "走");
        service.Advance(now.AddHours(2));
        check(service.VisibleToasts.Count == 1 && service.VisibleToasts[0].Message == "留下", "error toasts stay until closed");

        var first = service.ShowInfoAsync("第一则", "排队");
        var second = service.ConfirmAsync("第二则", "请确认", "继续", "返回");
        check(service.ActiveDialog?.Title == "第一则" && service.QueuedDialogCount == 1 && !first.IsCompleted, "dialogs queue behind the active one");
        service.AcceptActiveDialog();
        check(first.IsCompletedSuccessfully && service.ActiveDialog?.Title == "第二则", "accepting promotes the next dialog");
        service.CancelActiveDialog();
        check(second.IsCompletedSuccessfully && !second.Result, "escape-equivalent cancel returns false");

        using var cancel = new CancellationTokenSource();
        var cancelled = service.ShowWarningAsync("取消", "不再显示", cancel.Token);
        cancel.Cancel();
        check(cancelled.IsCanceled && service.ActiveDialog is null, "cancellation removes a pending dialog");

        var failed = service.RunAsync(() => throw new InvalidOperationException("坏了"), "操作未完成");
        check(service.ActiveDialog?.Tone == DialogTone.Error && service.ActiveDialog.Details?.Contains("坏了") == true, "RunAsync opens an error dialog");
        service.AcceptActiveDialog();
        failed.GetAwaiter().GetResult();
        check(failed.IsCompletedSuccessfully, "RunAsync completes after the error dialog closes");
    }

    private static void RunCards(Action<bool, string> check)
    {
        FeedbackMotion.Snap = true;
        foreach (var tone in new[] { DialogTone.Info, DialogTone.Success, DialogTone.Warning, DialogTone.Error, DialogTone.Confirm })
        {
            var dialog = new FeedbackDialog
            {
                Tone = tone,
                Title = tone + " 标题",
                Message = tone + " 正文",
                Details = tone == DialogTone.Error ? "System.InvalidOperationException: 示例" : null,
                PrimaryText = tone == DialogTone.Confirm ? "删除" : "确定",
                SecondaryText = tone == DialogTone.Confirm ? "取消" : null,
                Danger = tone == DialogTone.Confirm
            };
            var card = new AdDialogCard(dialog);
            var window = new Window { Width = 640, Height = 420, Content = card };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            check(card.Opacity == 1, tone + " card is visible when motion snaps");
            check(card.GetVisualDescendants().OfType<TextBlock>().Any(item => item.Name == "DialogMessage" && item.Text == dialog.Message), tone + " message renders");
            var primary = card.GetVisualDescendants().OfType<Button>().Single(item => item.Name == "DialogPrimary");
            check(primary.Content?.ToString() == dialog.PrimaryText, tone + " primary button is named");
            if (tone == DialogTone.Error)
            {
                var toggle = card.GetVisualDescendants().OfType<Button>().Single(item => item.Name == "DialogDetailsToggle");
                var details = card.GetLogicalDescendants().OfType<SelectableTextBlock>().Single(item => item.Name == "DialogDetails");
                check(!details.IsVisible, "error details start collapsed");
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(details.IsVisible && details.Text!.Contains("示例"), "error details expand");
            }

            var accepted = false;
            var cancelled = false;
            card.Accepted += (_, _) => accepted = true;
            card.Cancelled += (_, _) => cancelled = true;
            card.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = card });
            check(cancelled, tone + " escape cancels");
            card.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = card });
            check(accepted, tone + " enter confirms");
            if (tone == DialogTone.Confirm)
            {
                primary.Focus();
                card.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab, Source = card });
                var focused = window.FocusManager?.GetFocusedElement() as Control;
                check(focused?.Name is "DialogSecondary" or "DialogPrimary", "tab keeps focus inside the dialog");
            }
            window.Close();
        }

        MotionPreferences.ReduceMotion = true;
        check(!FeedbackMotion.AnimationsEnabled, "reduce motion disables feedback animation");
        MotionPreferences.ReduceMotion = false;
        FeedbackMotion.Snap = false;
    }

    private static void RunLayer(Action<bool, string> check)
    {
        FeedbackMotion.Snap = true;
        var service = new UserFeedbackService(action => Dispatcher.UIThread.Post(action));
        var layer = new AdFeedbackLayer { Width = 880, Height = 560, Service = service };
        var window = new Window { Width = 880, Height = 560, Content = layer };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        service.Toast(FeedbackKind.Success, "已保存");
        service.Toast(FeedbackKind.Error, "未能连接");
        var dialog = service.ShowInfoAsync("通知", "这是一条说明");
        for (var i = 0; i < 8 && service.ActiveDialog is not null && !window.GetVisualDescendants().Any(item => item is TextBlock { Name: "DialogMessage" }); i++)
            Dispatcher.UIThread.RunJobs();
        check(window.GetVisualDescendants().OfType<TextBlock>().Any(item => item.Name == "ToastMessage" && item.Text == "已保存"), "toast stack renders on the layer");
        check(window.GetVisualDescendants().OfType<TextBlock>().Any(item => item.Name == "DialogMessage" && item.Text == "这是一条说明"), "overlay dialog renders on the layer");
        window.GetVisualDescendants().OfType<Button>().Single(item => item.Name == "DialogPrimary").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        check(dialog.IsCompletedSuccessfully, "overlay primary button completes the dialog");
        window.Close();
        FeedbackMotion.Snap = false;
    }

    private static void RunAttach(Action<bool, string> check)
    {
        FeedbackMotion.Snap = true;
        var service = new UserFeedbackService(action => Dispatcher.UIThread.Post(action));
        var clicks = 0;
        var button = new Button { Name = "PageAction", Content = "动作" };
        button.Click += (_, _) => clicks++;
        var window = new Window { Width = 480, Height = 320, Content = button };
        FeedbackWindow.Attach(window, service);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(clicks == 1 && button.Parent is not null, "feedback attach leaves the page button clickable");

        var lateClicks = 0;
        var late = new Button { Name = "LateAction", Content = "稍后" };
        late.Click += (_, _) => lateClicks++;
        var shown = new Window { Width = 480, Height = 320, Content = late };
        shown.Show();
        shown.Measure(new Size(480, 320));
        shown.Arrange(new Rect(0, 0, 480, 320));
        shown.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        FeedbackWindow.Attach(shown, service);
        for (var i = 0; i < 4; i++) Dispatcher.UIThread.RunJobs();
        late.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(lateClicks == 1 && ReferenceEquals(shown.Content, late), "attach after measure does not throw and the button still clicks");
        window.Close();
        shown.Close();
        FeedbackMotion.Snap = false;
    }

    private static void RunAdopt(Action<bool, string> check)
    {
        FeedbackMotion.Snap = true;
        var service = new UserFeedbackService(action => Dispatcher.UIThread.Post(action));
        var clicks = 0;
        var button = new Button { Name = "LiveAction", Content = "当前会话" };
        button.Click += (_, _) => clicks++;
        var source = new Window { Width = 640, Height = 480, Content = button };
        source.Show();
        source.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        check(FeedbackWindow.Detach(button), "live page detaches from its window");
        var target = new Window { Width = 640, Height = 480 };
        check(FeedbackWindow.Adopt(target, button, service), "live page is adopted with the feedback layer");
        target.Show();
        target.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        service.Toast(FeedbackKind.Success, "已转移");
        for (var i = 0; i < 6; i++) Dispatcher.UIThread.RunJobs();
        check(clicks == 1, "adopted page button still clicks");
        check(target.GetVisualDescendants().OfType<AdFeedbackLayer>().Any(), "adopted window hosts the feedback layer");
        check(target.GetVisualDescendants().OfType<TextBlock>().Any(item => item.Name == "ToastMessage" && item.Text == "已转移"),
            "toast renders on the adopted window");
        target.Close();
        for (var i = 0; i < 4; i++) Dispatcher.UIThread.RunJobs();
        check(FeedbackWindow.Release(button), "adopted page can be released");
        source.Content = button;
        source.UpdateLayout();
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(clicks == 2 && ReferenceEquals(source.Content, button), "released page returns and still clicks");
        source.Close();
        FeedbackMotion.Snap = false;
    }
}
