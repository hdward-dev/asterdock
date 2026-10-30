using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using AsterDock.Contracts;
using AsterDock.Host.Modules;
using AsterDock.Host.Services;
using AsterDock.Host.Views;
using AsterDock.UI.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;

AppBuilder.Configure<CheckApplication>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .UseSkia()
    .SetupWithoutStarting();
var host = new MainWindow();
CheckLinuxWindowChrome(host);
LoadedApplication App(string id) => (LoadedApplication)Activator.CreateInstance(typeof(LoadedApplication),
    BindingFlags.Instance | BindingFlags.NonPublic, null,
    [new AppManifest { Id = id, Name = id, Version = "1", EntryAssembly = "fixture", EntryType = "fixture" },
        "/tmp", new FixtureModule(), null], null)!;
void Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, args);
void Check(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS " + label); }
var home = App("home"); var foreground = App("fixture-front"); var background = App("fixture-back"); var unopened = App("fixture-unopened");
foreach (var app in new[] { home, foreground, background, unopened }) host.Applications.Add(app);
Call("OpenApplication", home);
Check(host.FindControl<Button>("CapsuleInfoButton")!.IsVisible && host.FindControl<Border>("MiniAppCapsule")!.IsVisible,
    "home exposes app info control");
Check(!host.FindControl<Button>("CapsuleHomeButton")!.IsVisible && !host.FindControl<Button>("CapsulePopOutButton")!.IsVisible,
    "home keeps return-home and pop-out controls hidden");
Call("ShowAppInfo");
Check(host.FindControl<Grid>("AppInfoOverlay")!.IsVisible, "app info opens from home");
Call("OpenApplication", background);
var first = FixtureModule.Last!;
first.OpenChild();
Call("OpenApplication", foreground);
Call("ShowAppInfo");
var list = host.FindControl<ItemsControl>("AppInfoRunningApplications")!;
Check(list.ItemsSource!.Cast<LoadedApplication>().SequenceEqual(new[] { foreground, background }), "running list excludes home and unopened apps");
Call("StopRunningApplication", background);
Check(first.Stopped && first.Child?.IsVisible == false && !background.IsRunning, "stop disposes background work and owned windows");
Check(list.ItemsSource!.Cast<LoadedApplication>().SequenceEqual(new[] { foreground }), "stopped app removed from running list");
Check(((IApplicationShell)host).RecentApplications.Any(x => x.Application.Id == background.Manifest.Id), "stop preserves history");
Call("OpenApplication", background);
Check(background.IsRunning && !ReferenceEquals(first, FixtureModule.Last), "reopening creates fresh module instance");
Call("OpenDetachedApplication", background);
var detached = (Dictionary<LoadedApplication, Window>)typeof(MainWindow).GetField("_detachedApplications", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
Check(detached.ContainsKey(background), "fixture detached");
Call("StopRunningApplication", background);
Check(!detached.ContainsKey(background) && !background.IsRunning, "stop closes detached window");
Check(ReferenceEquals(typeof(MainWindow).GetField("_currentApplication", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host), home), "stopping current app returns home");
Call("OpenApplication", foreground);
Call("ShowAppInfo");
host.Measure(new Size(1440,900)); host.Arrange(new Rect(0,0,1440,900)); host.UpdateLayout();
var stopButton = list.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => Equals(ToolTip.GetTip(b), "结束此轻应用的后台运行"));
Check(stopButton is not null, "close badge renders");
typeof(MainWindow).GetField("_applicationsLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, true);
host.Show();
stopButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
Avalonia.Threading.Dispatcher.UIThread.RunJobs();
var confirmStop = host.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DialogPrimary");
confirmStop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
Avalonia.Threading.Dispatcher.UIThread.RunJobs();
Check(!foreground.IsRunning, "close badge confirms and stops app without reopening it");
var metrics = (ISystemMetricsService)typeof(MainWindow).GetField("_systemMetrics", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
unopened.GetQuickActions(host, metrics);
Call("RefreshRunningApplications");
Check(list.ItemsSource!.Cast<LoadedApplication>().Contains(unopened), "background initialization is listed without open history");
Call("StopRunningApplication", unopened);
Check(!unopened.IsRunning, "background-only app can stop");
Call("OpenApplication", foreground);
host.Show();
host.UpdateLayout();
Avalonia.Threading.Dispatcher.UIThread.RunJobs();
var page = host.FindControl<ContentControl>("ApplicationContent")!.Content;
Check(page is not null, "measured module page is in the main window");
Call("OpenDetachedApplication", foreground);
Check(detached.ContainsKey(foreground), "shown window can open a detached window");
var popped = detached[foreground];
ExerciseLayouts(host, popped);
var pageControl = (Control)page!;
Check(popped.GetVisualDescendants().Contains(pageControl), "pop-out moves the open page into the new window");
var mainContent = host.FindControl<ContentControl>("ApplicationContent")!.Content;
Check(!ReferenceEquals(mainContent, page), "pop-out removes the open page from the main window");
Check(mainContent is Control placeholder && placeholder.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text != null && text.Text.Contains("已在独立窗口打开")),
    "main window says the app is open in its own window");
var pageButton = pageControl.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "FixtureAction");
var fixtureClicks = 0;
pageButton.Click += (_, _) => fixtureClicks++;
pageButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
Check(fixtureClicks == 1, "moved page button receives clicks");
popped.Close();
Avalonia.Threading.Dispatcher.UIThread.RunJobs();
ExerciseLayouts(host);
Check(!detached.ContainsKey(foreground) && ReferenceEquals(host.FindControl<ContentControl>("ApplicationContent")!.Content, page),
    "closing the detached window returns the page to the main window");
var pageClicks = 0;
pageButton.Click += (_, _) => pageClicks++;
pageButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
Check(pageClicks == 1, "main page button still receives clicks after pop-out closes");

LoadedApplication ModuleApp(string id, IApplicationModule module) => (LoadedApplication)Activator.CreateInstance(typeof(LoadedApplication),
    BindingFlags.Instance | BindingFlags.NonPublic, null,
    [new AppManifest { Id = id, Name = id, Version = "1", EntryAssembly = "fixture", EntryType = "fixture" },
        "/tmp", module, null], null)!;
var cachedApp = ModuleApp("cached-page", new CachedPageModule());
host.Applications.Add(cachedApp);
Call("OpenApplication", cachedApp);
host.Activate();
Dispatcher.UIThread.RunJobs();
var cachedPage = (Control)host.FindControl<ContentControl>("ApplicationContent")!.Content!;
Call("OpenDetachedApplication", cachedApp);
Dispatcher.UIThread.RunJobs();
host.UpdateLayout();
Check(detached.ContainsKey(cachedApp), "cached page transfers into a detached window");
var cachedWindow = detached[cachedApp];
cachedWindow.UpdateLayout();
Check(cachedWindow.GetVisualDescendants().Contains(cachedPage), "detached window keeps the live page");
Check(!ReferenceEquals(host.FindControl<ContentControl>("ApplicationContent")!.Content, cachedPage),
    "main window releases the transferred page");
Call("OpenApplication", home);
Call("OpenApplication", cachedApp);
Dispatcher.UIThread.RunJobs();
Check(detached.ContainsKey(cachedApp) && cachedWindow.GetVisualDescendants().Contains(cachedPage),
    "leaving and reopening the app keeps the live page in the detached window");
Check(!ReferenceEquals(host.FindControl<ContentControl>("ApplicationContent")!.Content, cachedPage),
    "reopening a transferred app does not attach the live page twice");
cachedWindow.Close();
for (var i = 0; i < 6; i++) Dispatcher.UIThread.RunJobs();
host.UpdateLayout();
Check(!detached.ContainsKey(cachedApp), "closing the detached window ends the transfer");
Check(ReferenceEquals(host.FindControl<ContentControl>("ApplicationContent")!.Content, cachedPage),
    "the live page returns to the main window");
var freshModule = new FreshPageModule();
var freshApp = ModuleApp("fresh-page", freshModule);
host.Applications.Add(freshApp);
Call("OpenApplication", freshApp);
host.UpdateLayout();
var freshPage = (Control)host.FindControl<ContentControl>("ApplicationContent")!.Content!;
Call("OpenDetachedApplication", freshApp);
Check(detached.ContainsKey(freshApp), "shared-state module opens a detached window");
var freshWindow = detached[freshApp];
freshWindow.UpdateLayout();
Check(freshWindow.GetVisualDescendants().Contains(freshPage), "shared-state module moves the open page");
Check(!ReferenceEquals(host.FindControl<ContentControl>("ApplicationContent")!.Content, freshPage),
    "shared-state module leaves the main window without that page");
var freshMoved = freshWindow.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "FreshAction");
Check(ReferenceEquals(((Border)freshMoved.Parent!).Tag, freshModule.Shared), "moved page keeps the module state");
freshWindow.Close();
Dispatcher.UIThread.RunJobs();
Call("StopRunningApplication", cachedApp);
Call("StopRunningApplication", freshApp);
Call("StopRunningApplication", foreground);
var count=FixtureModule.Disposals;
foreground.Stop(); Check(FixtureModule.Disposals == count, "repeated stop is idempotent");
var missingSetting = JsonSerializer.Deserialize<HostUiSettings>("""{"ReduceMotion":true}""")!;
Check(missingSetting.AllowSystemNotifications && missingSetting.ReduceMotion, "missing notification setting stays enabled");
var disabledSetting = JsonSerializer.Deserialize<HostUiSettings>("""{"AllowSystemNotifications":false,"ReduceMotion":true}""")!;
Check(!disabledSetting.AllowSystemNotifications && disabledSetting.ReduceMotion, "notification setting can be turned off");
disabledSetting.AllowSystemNotifications = true;
var roundTrip = JsonSerializer.Deserialize<HostUiSettings>(JsonSerializer.Serialize(disabledSetting))!;
Check(roundTrip.AllowSystemNotifications && roundTrip.ReduceMotion, "both appearance settings round-trip");
var notes = new RecordingNotifications();
host.FeedbackService.SystemNotifications = notes;
var allowSwitch = host.FindControl<ToggleSwitch>("AllowSystemNotificationsSwitch")!;
if (allowSwitch.IsChecked != true) allowSwitch.IsChecked = true;
host.Hide();
Dispatcher.UIThread.RunJobs();
host.FeedbackService.Toast(FeedbackKind.Warning, "窗口已隐藏");
Check(notes.Published.Any(item => item.Body == "窗口已隐藏" && item.ModuleId is null), "hidden window routes a host toast to the system");
host.Show();
host.WindowState = WindowState.Minimized;
allowSwitch.IsChecked = false;
Dispatcher.UIThread.RunJobs();
var publishedBefore = notes.Published.Count;
host.FeedbackService.Toast(FeedbackKind.Info, "设置关闭后排队");
Check(notes.Published.Count == publishedBefore && host.FeedbackService.DeferredCount == 1, "disabled setting queues while minimized");
host.WindowState = WindowState.Normal;
host.Activate();
Dispatcher.UIThread.RunJobs();
Check(host.FeedbackService.VisibleToasts.Any(item => item.Message == "设置关闭后排队"), "restoring the window shows the queued toast");
allowSwitch.IsChecked = true;
Call("OpenApplication", foreground);
var notifier = FixtureModule.Last!;
Call("OpenApplication", home);
notifier.NotifyFailure();
Check(notes.Published.Any(item => item.ModuleId == "fixture-front" && item.OpenErrorDialog), "hidden module routes an error to the system");
var routed = notes.Published.Last(item => item.ModuleId == "fixture-front");
notes.Raise(routed.Token);
for (var i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();
Check(ReferenceEquals(typeof(MainWindow).GetField("_currentApplication", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host), foreground),
    "notification activation opens the module");
Check(host.FeedbackService.ActiveDialog?.Details?.Contains("stack-line") == true, "notification activation opens the error");
host.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "DialogPrimary")
    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
for (var i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();
host.WindowState = WindowState.Minimized;
var confirm = host.FeedbackService.ConfirmAsync("仍要确认", "继续吗");
Button? confirmButton = null;
for (var i = 0; i < 16 && confirmButton is null; i++)
{
    Dispatcher.UIThread.RunJobs();
    host.UpdateLayout();
    confirmButton = host.GetVisualDescendants().OfType<TextBlock>()
        .Where(text => text.Text == "继续吗")
        .SelectMany(text => text.GetVisualAncestors())
        .SelectMany(ancestor => ancestor.GetVisualDescendants().OfType<Button>())
        .FirstOrDefault(button => button.Name == "DialogPrimary");
}
Check(confirmButton is not null && !confirm.IsCompleted, "confirmation stays in the window while minimized");
confirmButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
for (var i = 0; i < 4; i++) Dispatcher.UIThread.RunJobs();
Check(confirm.IsCompletedSuccessfully && confirm.Result, "minimized confirmation still returns the choice");
host.WindowState = WindowState.Normal;
foreach(var app in host.Applications) app.Dispose();
try { unopened.GetQuickActions(host, null!); throw new Exception("disposed app relaunched"); }
catch(ObjectDisposedException) { Console.WriteLine("PASS disposed app cannot relaunch"); }
Console.WriteLine("All host lifecycle checks passed.");

static void ExerciseLayouts(params Window[] windows)
{
    for (var frame = 0; frame < 4; frame++)
    {
        foreach (var window in windows)
        {
            window.InvalidateMeasure();
            window.InvalidateArrange();
            window.UpdateLayout();
        }

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }
}

void CheckLinuxWindowChrome(MainWindow window)
{
    var linux = MainWindowChrome.Resolve(linux: true, windows: false, WindowState.Normal);
    Check(linux.Decorations == WindowDecorations.None && linux.TransparentSurface && linux.ClipToRoundedBounds
          && linux.CornerRadius == new CornerRadius(MainWindowChrome.CornerRadiusDip)
          && linux.CornerRadius.TopLeft == linux.CornerRadius.BottomLeft
          && linux.CornerRadius.TopRight == linux.CornerRadius.BottomRight
          && !linux.WindowsCornerPreference,
        "linux chrome rounds every corner without a window-manager frame");
    var maximized = MainWindowChrome.Resolve(linux: true, windows: false, WindowState.Maximized);
    var fullscreen = MainWindowChrome.Resolve(linux: true, windows: false, WindowState.FullScreen);
    Check(maximized.CornerRadius == default && fullscreen.CornerRadius == default && maximized.TransparentSurface,
        "maximized and fullscreen linux windows meet the screen edges");
    var windows = MainWindowChrome.Resolve(linux: false, windows: true, WindowState.Normal);
    Check(windows.Decorations == WindowDecorations.BorderOnly && windows.WindowsCornerPreference
          && !windows.TransparentSurface && windows.CornerRadius == default,
        "windows keeps the system corner preference instead of a transparent clip");
    const double shapeScale = 1.5;
    var outerNormal = X11RoundedWindowShape.ForClient(320, 200, linux.CornerRadius.TopLeft, shapeScale);
    var outerMaximized = X11RoundedWindowShape.ForClient(320, 200, maximized.CornerRadius.TopLeft, shapeScale);
    Check(linux.CornerRadius.TopLeft == linux.CornerRadius.TopRight
          && linux.CornerRadius.TopLeft == linux.CornerRadius.BottomLeft
          && linux.CornerRadius.TopLeft == linux.CornerRadius.BottomRight
          && outerNormal.Radius == X11RoundedWindowShape.DeviceRadius(linux.CornerRadius.BottomRight, shapeScale)
          && outerNormal.Radius == (int)Math.Round(MainWindowChrome.CornerRadiusDip * shapeScale, MidpointRounding.AwayFromZero),
        "outer silhouette radius matches the inner clip on every corner");
    Check(outerMaximized.Radius == 0 && X11RoundedWindowShape.Contains(outerMaximized.Width, outerMaximized.Height, outerMaximized.Radius, 0, 0),
        "maximized outer silhouette is the full rectangle, matching the square clip");
    Check(!X11RoundedWindowShape.Contains(outerNormal.Width, outerNormal.Height, outerNormal.Radius, 0, 0)
          && X11RoundedWindowShape.Contains(outerNormal.Width, outerNormal.Height, outerNormal.Radius, outerNormal.Radius, outerNormal.Height / 2),
        "outer mask follows the same circular corner as the clip");
    var maskAgrees = true;
    for (var y = 0; y < 36 && maskAgrees; y++)
    {
        for (var x = 0; x < 48; x++)
        {
            var inside = X11RoundedWindowShape.Contains(48, 36, 12, x, y);
            var covered = false;
            foreach (var row in X11RoundedWindowShape.Build(48, 36, 12))
            {
                if (x >= row.X && y >= row.Y && x < row.X + row.Width && y < row.Y + row.Height)
                    covered = true;
            }
            var onCircle = RoundedByCircle(48, 36, 12, x, y);
            if (inside != covered || inside != onCircle)
                maskAgrees = false;
        }
    }
    Check(maskAgrees, "outer mask scanlines are the circular corner of the clip radius");
    var capsuleMargin = window.FindControl<Border>("WindowControlCapsule")!;
    Check(capsuleMargin.VerticalAlignment == VerticalAlignment.Top
          && capsuleMargin.Margin.Top == capsuleMargin.Margin.Right
          && capsuleMargin.Margin.Top == MainWindowChrome.TitleBarInsetDip
          && window.FindControl<Border>("CustomTitleBar")!.Padding == default,
        "caption top inset equals the right inset");
    if (!OperatingSystem.IsLinux())
    {
        Check(window.WindowDecorations == WindowDecorations.BorderOnly, "non-linux window keeps border decorations");
        return;
    }

    var chrome = window.FindControl<Border>("WindowChrome")!;
    Check(window.WindowDecorations == WindowDecorations.None, "running window clears linux decorations");
    Check(window.Background is ISolidColorBrush { Color.A: 0 }, "linux window surface is transparent");
    Check(window.TransparencyLevelHint.Contains(WindowTransparencyLevel.Transparent), "linux window asks for a transparent visual");
    Check(chrome.CornerRadius.BottomLeft == MainWindowChrome.CornerRadiusDip
          && chrome.CornerRadius.BottomRight == MainWindowChrome.CornerRadiusDip
          && chrome.CornerRadius.TopLeft == chrome.CornerRadius.BottomLeft,
        "live chrome uses the same radius on the bottom corners");
    Check(chrome.ClipToBounds && chrome.BorderThickness == default && chrome.BoxShadow == default && chrome.BorderBrush is null,
        "live chrome has no square stroke and no drop shadow");
    var grips = (Grid)typeof(MainWindow).GetField("_resizeGrips", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    Check(grips.IsVisible && grips.Children.Count == 8, "linux keeps invisible resize grips");
    double GripSize(WindowEdge edge, bool width)
    {
        foreach (var child in grips.Children)
        {
            if (child is Border { Tag: WindowEdge gripEdge } border && gripEdge == edge)
                return width ? border.Width : border.Height;
        }
        return -1;
    }
    Check(GripSize(WindowEdge.North, false) == MainWindowChrome.TitleBarInsetDip
          && GripSize(WindowEdge.East, true) == MainWindowChrome.TitleBarInsetDip
          && GripSize(WindowEdge.NorthEast, true) == MainWindowChrome.TitleBarInsetDip
          && GripSize(WindowEdge.NorthEast, false) == MainWindowChrome.TitleBarInsetDip,
        "top and right resize bands stay inside the caption inset");
    window.WindowState = WindowState.Maximized;
    Check(chrome.CornerRadius == default && !grips.IsVisible, "maximized window drops the radius and the grips");
    window.WindowState = WindowState.Normal;
    Check(chrome.CornerRadius.BottomLeft == MainWindowChrome.CornerRadiusDip && grips.IsVisible,
        "restoring the window rounds the bottom corners again");

    const int width = 1100;
    const int height = 700;
    var opened = typeof(MainWindow).GetMethod("MainWindow_Opened", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var openedHandler = (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window, opened);
    window.Opened -= openedHandler;
    window.Width = width;
    window.Height = height;
    window.Show();
    window.Measure(new Size(width, height));
    window.Arrange(new Rect(0, 0, width, height));
    window.UpdateLayout();
    var capsule = window.FindControl<Border>("WindowControlCapsule")!;
    var capsuleOrigin = capsule.TranslatePoint(new Point(0, 0), window);
    Check(capsuleOrigin.HasValue, "caption capsule is laid out in the window");
    var origin = capsuleOrigin!.Value;
    var topInset = origin.Y;
    var rightInset = window.Bounds.Width - origin.X - capsule.Bounds.Width;
    Check(Math.Abs(topInset - rightInset) <= 0.5
          && Math.Abs(topInset - MainWindowChrome.TitleBarInsetDip) <= 0.5
          && Math.Abs(rightInset - MainWindowChrome.TitleBarInsetDip) <= 0.5,
        $"caption top inset {topInset:0.##} equals right inset {rightInset:0.##}");
    var liveScale = window.RenderScaling <= 0 ? 1 : window.RenderScaling;
    var liveOuter = X11RoundedWindowShape.ForClient(chrome.Bounds.Width, chrome.Bounds.Height, chrome.CornerRadius.TopLeft, liveScale);
    Check(liveOuter.Radius == X11RoundedWindowShape.DeviceRadius(chrome.CornerRadius.BottomLeft, liveScale)
          && liveOuter.Radius == X11RoundedWindowShape.DeviceRadius(chrome.CornerRadius.TopRight, liveScale)
          && liveOuter.Radius == X11RoundedWindowShape.DeviceRadius(chrome.CornerRadius.BottomRight, liveScale)
          && liveOuter.Radius > 0
          && !X11RoundedWindowShape.Contains(liveOuter.Width, liveOuter.Height, liveOuter.Radius, 0, 0),
        "live outer radius matches the clipped chrome on every corner");
    using var bitmap = window.CaptureRenderedFrame();
    if (bitmap is null)
        throw new Exception($"chrome frame was not rendered (bounds {window.Bounds}, client {window.ClientSize})");
    var pngPath = Path.Combine(Path.GetTempPath(), "asterdock-chrome-frame.png");
    bitmap.Save(pngPath, PngBitmapEncoderOptions.Default);
    var pngLength = new FileInfo(pngPath).Length;
    if (pngLength < 32)
        throw new Exception($"chrome frame did not encode ({pngLength} bytes, bitmap {bitmap.PixelSize}, bounds {window.Bounds})");
    using var pixels = SKBitmap.Decode(pngPath);
    Check(pixels is not null && pixels.Width == width && pixels.Height == height,
        $"chrome frame rendered (png {pngLength} bytes, bitmap {bitmap.PixelSize}, decoded {(pixels is null ? "null" : pixels.Width + "x" + pixels.Height)})");
    var radius = (int)MainWindowChrome.CornerRadiusDip;
    var frameWidth = pixels!.Width;
    var frameHeight = pixels.Height;
    static int ChannelGap(SKColor left, SKColor right) =>
        Math.Abs(left.Red - right.Red) + Math.Abs(left.Green - right.Green) + Math.Abs(left.Blue - right.Blue);
    static bool Unpainted(SKColor color) => color.Alpha <= 12 || color.Red + color.Green + color.Blue <= 18;
    bool Outside(int x, int y) => Unpainted(pixels.GetPixel(x, y));
    Check(Outside(0, 0) && Outside(2, 0) && Outside(0, 2), "top-left corner is outside the rounded window");
    Check(Outside(frameWidth - 1, 0) && Outside(frameWidth - 3, 0), "top-right corner is outside the rounded window");
    Check(Outside(0, frameHeight - 1) && Outside(2, frameHeight - 1) && Outside(0, frameHeight - 3),
        "bottom-left corner is outside the rounded window");
    Check(Outside(frameWidth - 1, frameHeight - 1) && Outside(frameWidth - 3, frameHeight - 1) && Outside(frameWidth - 1, frameHeight - 3),
        "bottom-right corner is outside the rounded window");
    var leftEdge = pixels.GetPixel(0, frameHeight / 2);
    var leftInside = pixels.GetPixel(40, frameHeight / 2);
    var bottomEdge = pixels.GetPixel(frameWidth / 2, frameHeight - 1);
    var bottomInside = pixels.GetPixel(frameWidth / 2, frameHeight - 24);
    Check(leftEdge.Alpha >= 250 && bottomEdge.Alpha >= 250 && pixels.GetPixel(frameWidth / 2, 0).Alpha >= 250,
        "straight edges are painted");
    Check(ChannelGap(leftEdge, leftInside) <= 12 && ChannelGap(bottomEdge, bottomInside) <= 12,
        "edges are the page fill, not a frame line or shadow");
    Check(pixels.GetPixel(radius, 1).Alpha >= 250 && pixels.GetPixel(1, frameHeight - 1 - radius).Alpha >= 250,
        "pixels inside the radius stay filled");
    window.Width = 1440;
    window.Height = 900;
    OuterShapeInstallCheck.Run();
}

static bool RoundedByCircle(int width, int height, int radius, int x, int y)
{
    var px = x + 0.5;
    var py = y + 0.5;
    var cx = Math.Clamp(px, radius, width - radius);
    var cy = Math.Clamp(py, radius, height - radius);
    var dx = px - cx;
    var dy = py - cy;
    return dx * dx + dy * dy <= radius * (double)radius + 1e-9;
}

static class OuterShapeInstallCheck
{
    public static void Run()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var display = Native.XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            Console.WriteLine("SKIP outer shape install (no X display)");
            return;
        }

        var root = Native.XDefaultRootWindow(display);
        var window = Native.XCreateSimpleWindow(display, root, 0, 0, 80, 60, 0, 0, 0);
        if (window == IntPtr.Zero)
            throw new Exception("outer shape install could not create an X window");
        try
        {
            Native.XSync(display, 0);
            X11RoundedWindowShape.Apply(window, 80, 60, 12);
            Native.XSync(display, 0);
            var rectangles = Native.XShapeGetRectangles(display, window, 0, out var count, out _);
            if (rectangles == IntPtr.Zero || count <= 1)
                throw new Exception($"outer shape was not installed (count {count})");
            var covered = new bool[80 * 60];
            var rectSize = Marshal.SizeOf<Native.Rectangle>();
            for (var i = 0; i < count; i++)
            {
                var rect = Marshal.PtrToStructure<Native.Rectangle>(rectangles + i * rectSize);
                for (var y = rect.Y; y < rect.Y + rect.Height; y++)
                {
                    for (var x = rect.X; x < rect.X + rect.Width; x++)
                    {
                        if ((uint)x < 80 && (uint)y < 60)
                            covered[y * 80 + x] = true;
                    }
                }
            }

            Native.XFree(rectangles);
            if (covered[0] || !covered[12 * 80 + 12] || !covered[30 * 80])
                throw new Exception("installed outer shape does not follow the clip radius");
            X11RoundedWindowShape.Apply(window, 80, 60, 0);
            Native.XSync(display, 0);
            var cleared = Native.XShapeGetRectangles(display, window, 0, out var clearedCount, out _);
            if (cleared == IntPtr.Zero || clearedCount != 1)
                throw new Exception($"cleared outer shape was not rectangular (count {clearedCount})");
            var full = Marshal.PtrToStructure<Native.Rectangle>(cleared);
            Native.XFree(cleared);
            if (full.X != 0 || full.Y != 0 || full.Width != 80 || full.Height != 60)
                throw new Exception($"cleared outer shape is {full.X},{full.Y} {full.Width}x{full.Height}");
            Console.WriteLine("PASS installed outer shape matches the clip radius");
        }
        finally
        {
            Native.XDestroyWindow(display, window);
            Native.XCloseDisplay(display);
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rectangle
        {
            public short X;
            public short Y;
            public ushort Width;
            public ushort Height;
        }

        [DllImport("libX11.so.6")]
        internal static extern IntPtr XOpenDisplay(IntPtr display);

        [DllImport("libX11.so.6")]
        internal static extern int XCloseDisplay(IntPtr display);

        [DllImport("libX11.so.6")]
        internal static extern IntPtr XDefaultRootWindow(IntPtr display);

        [DllImport("libX11.so.6")]
        internal static extern IntPtr XCreateSimpleWindow(IntPtr display, IntPtr parent, int x, int y, uint width, uint height, uint border, ulong borderPixel, ulong background);

        [DllImport("libX11.so.6")]
        internal static extern int XDestroyWindow(IntPtr display, IntPtr window);

        [DllImport("libX11.so.6")]
        internal static extern int XSync(IntPtr display, int discard);

        [DllImport("libX11.so.6")]
        internal static extern int XFree(IntPtr data);

        [DllImport("libXext.so.6")]
        internal static extern IntPtr XShapeGetRectangles(IntPtr display, IntPtr window, int kind, out int count, out int ordering);
    }
}

public sealed class CheckApplication : Application
{
    public override void Initialize()
    {
        Styles.Add(new Semi.Avalonia.SemiTheme());
        Styles.Add(new Ursa.Themes.Semi.UrsaSemiTheme());
        Styles.Add(new StyleInclude(new Uri("avares://AsterDock.UI/")) { Source=new Uri("avares://AsterDock.UI/Themes/AsterDockTheme.axaml") });
    }
}
public sealed class FixtureModule : IApplicationModule, IApplicationContextAware
{
    public static FixtureModule? Last;
    public static int Disposals;
    public bool Stopped;
    public Window? Child;
    private IApplicationContext? context;
    public void Initialize(IApplicationContext value) { context=value; Last=this; }
    public Control CreateView() => new Border { Child = new Button { Name = "FixtureAction", Content = "动作" } };
    public void OpenChild() { Child=new Window(); context!.Windows.Show(Child, false); }
    public void NotifyFailure() => context!.Feedback.ShowErrorAsync("订阅失败", "未能更新", new InvalidOperationException("stack-line"));
    public void Dispose() { Stopped=true; Disposals++; }
}

sealed class CachedPageModule : IApplicationModule
{
    private Control? _view;
    public Control CreateView() => _view ??= new Border { Child = new TextBlock { Text = "同一页" } };
    public void Dispose() { }
}

sealed class FreshPageModule : IApplicationModule
{
    public object Shared { get; } = new();
    public Control CreateView() => new Border { Tag = Shared, Child = new Button { Name = "FreshAction", Content = "新页面" } };
    public void Dispose() { }
}

sealed class RecordingNotifications : ISystemNotificationBackend
{
    public bool IsAvailable => true;
    public List<SystemNotificationRequest> Published { get; } = [];
    public event EventHandler<SystemNotificationActivation>? Activated;
    public bool TryPublish(SystemNotificationRequest request)
    {
        Published.Add(request);
        return true;
    }

    public void Raise(string token) => Activated?.Invoke(this, new SystemNotificationActivation { Token = token });
}
