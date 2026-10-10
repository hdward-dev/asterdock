using System.Reflection;
using AsterDock.Contracts;
using AsterDock.Host.Modules;
using AsterDock.Host.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
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
stopButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
Avalonia.Threading.Dispatcher.UIThread.RunJobs();
Check(!foreground.IsRunning, "close badge stops app without reopening it");
var metrics = (ISystemMetricsService)typeof(MainWindow).GetField("_systemMetrics", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
unopened.GetQuickActions(host, metrics);
Call("RefreshRunningApplications");
Check(list.ItemsSource!.Cast<LoadedApplication>().Contains(unopened), "background initialization is listed without open history");
Call("StopRunningApplication", unopened);
Check(!unopened.IsRunning, "background-only app can stop");
var count=FixtureModule.Disposals;
foreground.Stop(); Check(FixtureModule.Disposals == count, "repeated stop is idempotent");
foreach(var app in host.Applications) app.Dispose();
try { unopened.GetQuickActions(host, null!); throw new Exception("disposed app relaunched"); }
catch(ObjectDisposedException) { Console.WriteLine("PASS disposed app cannot relaunch"); }
Console.WriteLine("All host lifecycle checks passed.");

public sealed class CheckApplication : Application
{
    public override void Initialize()
    {
        Styles.Add(new Semi.Avalonia.SemiTheme());
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
    public Control CreateView() => new Border();
    public void OpenChild() { Child=new Window(); context!.Windows.Show(Child, false); }
    public void Dispose() { Stopped=true; Disposals++; }
}
