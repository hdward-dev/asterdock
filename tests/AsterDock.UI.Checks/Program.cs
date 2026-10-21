using AsterDock.UI.Checks;
using AsterDock.UI.Controls;
using AsterDock.UI.Motion;
using AsterDock.UI.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

var failed = false;
void Check(bool condition, string label)
{
    if (!condition)
    {
        failed = true;
        Console.WriteLine("FAIL " + label);
        return;
    }

    Console.WriteLine("PASS " + label);
}

RunServiceChecks(Check);
RunSchedulerChecks(Check);
RunShapeChecks(Check);
RunEasingChecks(Check);

AppBuilder.Configure<GalleryApp>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions
    {
        ShouldRenderOnUIThread = true,
        UseHeadlessDrawing = false
    })
    .UseSkia()
    .SetupWithoutStarting();
RunOverlayChecks(Check);

var exportIndex = Array.IndexOf(args, "--export");
if (exportIndex >= 0 && exportIndex + 1 < args.Length)
{
    var directory = Path.GetFullPath(args[exportIndex + 1]);
    Directory.CreateDirectory(directory);
    GalleryExport.ExportStills(directory);
    GalleryExport.ExportVideo(directory);
    Console.WriteLine("EXPORTED " + directory);
}

if (failed)
{
    Console.Error.WriteLine("Loading checks failed.");
    Environment.Exit(1);
}

Console.WriteLine("All loading checks passed.");

static void RunServiceChecks(Action<bool, string> check)
{
    var service = new LoadingService();
    var changes = 0;
    service.Changed += (_, _) => changes++;
    var outer = service.Begin("外层");
    check(service.IsLoading && service.Text == "外层" && !service.IsCancellable, "first scope is active");
    var inner = service.Begin("内层");
    check(service.Text == "内层", "latest text wins");
    var quiet = service.Begin(null);
    check(service.Text == "内层", "null text keeps the latest message");
    quiet.Dispose();
    inner.Dispose();
    check(service.IsLoading && service.Text == "外层", "ending inner scopes restores the outer text");
    outer.Dispose();
    outer.Dispose();
    check(!service.IsLoading && service.Text is null, "double dispose leaves the service idle");

    using var cancellable = service.Begin("可取消", out var token);
    check(service.IsCancellable && !token.IsCancellationRequested, "cancellable scope exposes a token");
    using var plain = service.Begin("普通");
    check(service.Text == "普通" && service.IsCancellable, "a cancellable ancestor keeps the cancel button");
    service.Cancel();
    check(token.IsCancellationRequested, "cancel signals the newest cancellable scope");
    plain.Dispose();
    cancellable.Dispose();
    check(!service.IsLoading && !service.IsCancellable, "all scopes released");

    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var running = service.RunAsync(async () =>
    {
        check(service.IsLoading && service.Text == "同步", "runasync holds a scope");
        entered.TrySetResult();
        await Task.Delay(20);
    }, "同步");
    entered.Task.GetAwaiter().GetResult();
    check(service.IsLoading, "runasync stays active until the task finishes");
    running.GetAwaiter().GetResult();
    check(!service.IsLoading, "runasync releases the scope");

    var cancelled = service.RunAsync(async ct =>
    {
        service.Cancel();
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }, "取消中");
    try
    {
        cancelled.GetAwaiter().GetResult();
        check(false, "cancellable runasync throws");
    }
    catch (OperationCanceledException)
    {
        check(!service.IsLoading, "cancellable runasync releases after cancel");
    }

    var gate = new object();
    var peak = 0;
    Parallel.For(0, 40, i =>
    {
        using var scope = service.Begin(i % 3 == 0 ? "并发" : null);
        lock (gate) peak = Math.Max(peak, service.IsLoading ? 1 : 0);
        Thread.Sleep(1);
    });
    check(peak == 1 && !service.IsLoading, "concurrent scopes stack and drain");
    check(changes > 0, "state changes are published");
}

static void RunSchedulerChecks(Action<bool, string> check)
{
    var scheduler = new LoadingVisibilityScheduler
    {
        Delay = TimeSpan.FromMilliseconds(150),
        MinimumVisible = TimeSpan.FromMilliseconds(300)
    };
    var start = DateTimeOffset.Parse("2026-10-20T00:00:00Z");
    check(!scheduler.SetRequested(true, start) && !scheduler.IsPresented, "request does not show before the delay");
    check(!scheduler.Advance(start.AddMilliseconds(149)) && !scheduler.IsPresented, "149ms is still waiting");
    check(scheduler.Advance(start.AddMilliseconds(150)) && scheduler.IsPresented, "150ms presents the overlay");

    var hidden = new LoadingVisibilityScheduler { Delay = TimeSpan.FromMilliseconds(150), MinimumVisible = TimeSpan.FromMilliseconds(300) };
    hidden.SetRequested(true, start);
    hidden.SetRequested(false, start.AddMilliseconds(40));
    check(!hidden.IsPresented && hidden.NextTransitionAt is null, "hiding before show cancels the delay");

    scheduler.SetRequested(false, start.AddMilliseconds(150));
    check(scheduler.IsPresented && scheduler.NextTransitionAt == start.AddMilliseconds(450), "minimum visibility keeps the overlay");
    check(!scheduler.Advance(start.AddMilliseconds(449)) && scheduler.IsPresented, "minimum visibility has not elapsed");
    check(scheduler.Advance(start.AddMilliseconds(450)) && !scheduler.IsPresented, "overlay hides after the minimum");

    var restart = new LoadingVisibilityScheduler { Delay = TimeSpan.FromMilliseconds(150), MinimumVisible = TimeSpan.FromMilliseconds(300) };
    restart.SetRequested(true, start);
    restart.Advance(start.AddMilliseconds(150));
    restart.SetRequested(false, start.AddMilliseconds(160));
    restart.SetRequested(true, start.AddMilliseconds(200));
    check(restart.IsPresented && restart.NextTransitionAt is null, "a new request cancels a pending hide");

    var immediate = new LoadingVisibilityScheduler { Delay = TimeSpan.Zero, MinimumVisible = TimeSpan.Zero };
    check(immediate.SetRequested(true, start) && immediate.IsPresented, "zero delay presents immediately");
    check(immediate.SetRequested(false, start) && !immediate.IsPresented, "zero minimum hides immediately");
}

static void RunShapeChecks(Action<bool, string> check)
{
    var rest = SpinnerMotion.Sample(TimeSpan.Zero, reduceMotion: false);
    var peak = SpinnerMotion.Sample(SpinnerMotion.PeakAt, false);
    var loop = SpinnerMotion.Sample(TimeSpan.FromSeconds(SpinnerMotion.CycleSeconds), false);
    check(peak.Sweep > rest.Sweep * 4, "arc grows through the cycle");
    check(peak.Sweep > Math.Tau * 0.55 && peak.Sweep < Math.Tau * 0.8, "peak arc stays an open stroke");
    check(peak.EchoSweep > 0 && peak.Sweep + peak.EchoSweep < Math.Tau * 0.92, "the trail follows without closing the ring");
    check(Math.Abs(loop.Sweep - rest.Sweep) < 0.02, "sweep returns at the end of the cycle");
    var beforeLoop = SpinnerMotion.Sample(TimeSpan.FromSeconds(SpinnerMotion.CycleSeconds * 0.99), false);
    var traveled = beforeLoop.Start + beforeLoop.Sweep - (rest.Start + rest.Sweep);
    check(traveled > Math.Tau * 1.5, "spinner travels more than one turn per cycle");
    check(peak.RingScale > rest.RingScale && peak.RingScale <= 1 && peak.DiscScale <= 1, "pulse grows toward the resting bounds");

    var forward = true;
    var previous = rest.Start + rest.Sweep;
    for (var i = 1; i <= 64; i++)
    {
        var pose = SpinnerMotion.Sample(TimeSpan.FromSeconds(SpinnerMotion.CycleSeconds * i / 64d), false);
        var head = pose.Start + pose.Sweep;
        var delta = head - previous;
        var visual = delta - (Math.Tau * Math.Round(delta / Math.Tau));
        if (visual < -0.02) forward = false;
        previous = head;
    }

    check(forward, "arc head keeps moving forward");
    var reduced = SpinnerMotion.Sample(TimeSpan.FromSeconds(1.4), true);
    var later = SpinnerMotion.Sample(TimeSpan.FromSeconds(2.1), true);
    check(Math.Abs(reduced.Start - later.Start) < 0.001 && Math.Abs(reduced.Sweep - later.Sweep) < 0.001, "reduced motion does not spin");
    check(reduced.EchoSweep == 0 && Math.Abs(reduced.RingScale - 1) < 0.001, "reduced motion keeps a single static arc");
    check(reduced.Opacity > 0.45 && reduced.Opacity <= 1 && later.Opacity != reduced.Opacity, "reduced motion only pulses opacity");
}

static void RunEasingChecks(Action<bool, string> check)
{
    check(EmphasizedEasing.Ease(0) == 0 && EmphasizedEasing.Ease(1) == 1, "emphasized easing is normalized");
    var previous = -1d;
    var increasing = true;
    for (var i = 0; i <= 20; i++)
    {
        var value = EmphasizedEasing.Ease(i / 20d);
        if (value + 1e-6 < previous) increasing = false;
        previous = value;
    }

    check(increasing, "emphasized easing is monotonic");
    check(EmphasizedEasing.Ease(0.35) > 0.35, "emphasized easing settles toward the destination");
}

static void RunOverlayChecks(Action<bool, string> check)
{
    var card = new Border
    {
        Background = Brushes.White,
        CornerRadius = new CornerRadius(18),
        Child = new TextBlock { Text = "设备列表", Margin = new Thickness(16) }
    };
    var overlay = new AdLoadingOverlay
    {
        Width = 320,
        Height = 180,
        DelayMs = 5000,
        MinimumVisibleMs = 300,
        Content = card
    };
    var window = new Window { Width = 320, Height = 180, Content = overlay, Background = Brushes.White };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    overlay.IsLoading = true;
    Dispatcher.UIThread.RunJobs();
    check(!overlay.IsPresented, "overlay waits out DelayMs");
    var waitingIndicator = overlay.GetVisualDescendants().OfType<AdLoadingIndicator>().FirstOrDefault();
    waitingIndicator?.NudgeClock();
    LoadingClock.TickAll();
    check(waitingIndicator?.IsClockActive == true, "indicator stays on the clock while the scrim is delayed");
    overlay.DelayMs = 0;
    Dispatcher.UIThread.RunJobs();
    check(overlay.IsPresented, "zero DelayMs presents on the next request update");
    var scrim = overlay.GetVisualDescendants().OfType<Border>().FirstOrDefault(item => item.Name == "PART_Scrim");
    check(scrim is { IsVisible: true }, "scrim is shown with the overlay");
    check(overlay.CornerRadius == new CornerRadius(18), "overlay corner follows the wrapped card");
    check(overlay.IsHitTestVisible && scrim is { IsHitTestVisible: true }, "scrim captures input while the overlay stays hit-testable");
    overlay.IsCancellable = true;
    var clicks = 0;
    overlay.CancelRequested += (_, _) => clicks++;
    var cancel = overlay.GetVisualDescendants().OfType<Button>().FirstOrDefault(item => Equals(item.Content, "取消"));
    check(cancel is { IsVisible: true }, "cancel button is available");
    cancel?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    check(clicks == 1, "cancel button raises CancelRequested");
    overlay.MinimumVisibleMs = 0;
    overlay.IsLoading = false;
    Dispatcher.UIThread.RunJobs();
    check(!overlay.IsPresented, "zero minimum ends presentation immediately");
    LoadingClock.Freeze(LoadingClock.Now + TimeSpan.FromMilliseconds(200));
    LoadingClock.TickAll();
    check(scrim is { IsVisible: false }, "fade completes and hides the scrim");
    LoadingClock.Resume();
    check(overlay.IsHitTestVisible, "hidden overlay leaves children hit-testable");
    window.Close();

    var held = new AdLoadingOverlay { Width = 120, Height = 80, DelayMs = 0, MinimumVisibleMs = 300, Content = new Border() };
    var host = new Window { Width = 120, Height = 80, Content = held };
    host.Show();
    Dispatcher.UIThread.RunJobs();
    held.IsLoading = true;
    Dispatcher.UIThread.RunJobs();
    check(held.IsPresented, "delay of zero shows before the minimum window");
    held.IsLoading = false;
    Dispatcher.UIThread.RunJobs();
    check(held.IsPresented, "minimum visibility keeps a shown overlay");
    host.Close();
}

public sealed class GalleryApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new Semi.Avalonia.SemiTheme());
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://AsterDock.UI/"))
        {
            Source = new Uri("avares://AsterDock.UI/Themes/AsterDockTheme.axaml")
        });
    }
}
