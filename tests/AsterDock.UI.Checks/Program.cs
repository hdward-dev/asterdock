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
    var buffer = new float[LoadingShapes.Samples];
    LoadingShapes.Interpolate(TimeSpan.Zero, reduceMotion: false, buffer);
    check(buffer.SequenceEqual(LoadingShapes.Frames[0]), "morph starts on the cookie");
    check(PeakCount(LoadingShapes.Frames[0]) >= 10, "cookie has twelve lobes");
    check(LoadingShapes.Frames[0].Min() > 0.84, "cookie lobes stay soft");
    check(LoadingShapes.Frames[1].Min() > 0.68, "clover stays plump");
    check(LoadingShapes.Frames[2].Min() > 0.75, "pentagon stays rounded");
    check(PeakCount(LoadingShapes.Frames[1]) == 4, "clover has four petals");
    check(PeakCount(LoadingShapes.Frames[2]) == 5, "pentagon has five corners");
    check(LoadingShapes.Frames[3].All(value => Math.Abs(value - 1) < 0.001), "circle is even");
    check(LoadingShapes.Frames.All(frame => frame.Length == LoadingShapes.Samples && frame.All(value => value is > 0 and <= 1)),
        "every shape uses the same normalized samples");
    LoadingShapes.Interpolate(TimeSpan.FromMilliseconds(200), false, buffer);
    var next = new float[LoadingShapes.Samples];
    LoadingShapes.Interpolate(TimeSpan.FromMilliseconds(216), false, next);
    var jump = buffer.Zip(next).Max(pair => Math.Abs(pair.First - pair.Second));
    check(jump < 0.2, "adjacent morph frames stay continuous");
    LoadingShapes.Interpolate(TimeSpan.FromSeconds(1), true, buffer);
    check(buffer.SequenceEqual(LoadingShapes.Frames[3]), "reduced motion uses a static circle");
    check(Math.Abs(LoadingShapes.Rotation(TimeSpan.FromSeconds(2), false) - Math.Tau) < 0.001, "rotation completes one turn in two seconds");
    check(LoadingShapes.Rotation(TimeSpan.FromSeconds(2), true) == 0, "reduced motion does not rotate");
    var pulse = LoadingShapes.PulseOpacity(TimeSpan.FromSeconds(0.8));
    check(pulse is > 0.58 and <= 1, "reduced motion pulse stays gentle");
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

static int PeakCount(float[] radii)
{
    var count = 0;
    var window = Math.Max(1, radii.Length / 96);
    for (var i = 0; i < radii.Length; i++)
    {
        var previous = radii[(i - window + radii.Length) % radii.Length];
        var next = radii[(i + window) % radii.Length];
        var left = radii[(i + radii.Length - 1) % radii.Length];
        var right = radii[(i + 1) % radii.Length];
        if (radii[i] >= left && radii[i] >= right && radii[i] > previous + 0.0005 && radii[i] > next + 0.0005 && radii[i] > 0.9)
            count++;
    }

    return count;
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
    overlay.DelayMs = 0;
    Dispatcher.UIThread.RunJobs();
    check(overlay.IsPresented, "zero DelayMs presents on the next request update");
    var scrim = overlay.GetVisualDescendants().OfType<Border>().FirstOrDefault(item => item.Name == "PART_Scrim");
    check(scrim is { IsVisible: true }, "scrim is shown with the overlay");
    check(overlay.CornerRadius == new CornerRadius(18), "overlay corner follows the wrapped card");
    check(overlay.IsHitTestVisible, "visible overlay blocks input");
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
    check(!overlay.IsHitTestVisible, "hidden overlay releases input");
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
