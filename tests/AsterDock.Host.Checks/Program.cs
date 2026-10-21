using System.Reflection;
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
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

AppBuilder.Configure<CheckApplication>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
var host = new MainWindow();
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
Check(ReferenceEquals(host.FindControl<ContentControl>("ApplicationContent")!.Content, page), "pop-out leaves the measured page in the main window");
var pageControl = (Control)page!;
var fixtureAction = popped.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "FixtureAction");
var pageButton = pageControl.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "FixtureAction");
Check(!ReferenceEquals(fixtureAction, pageButton), "detached window uses a new page instance");
var fixtureClicks = 0;
fixtureAction.Click += (_, _) => fixtureClicks++;
fixtureAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
Check(fixtureClicks == 1, "detached page button receives clicks");
popped.Close();
Avalonia.Threading.Dispatcher.UIThread.RunJobs();
ExerciseLayouts(host);
Check(!detached.ContainsKey(foreground) && ReferenceEquals(host.FindControl<ContentControl>("ApplicationContent")!.Content, page),
    "closing the detached window leaves the main page in place");
var pageClicks = 0;
pageButton.Click += (_, _) => pageClicks++;
pageButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
Check(pageClicks == 1, "main page button still receives clicks after pop-out closes");
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
