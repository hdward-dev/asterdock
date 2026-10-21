using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NetworkAccelerator.Module.Controls;
using NetworkAccelerator.Module.Views;
using Semi.Avalonia;
using Ursa.Themes.Semi;

static class ModuleInteractionChecks
{
    public static void RunMotionClockChecks(Action<bool, string> check)
    {
        var previous = MotionClock.Manual;
        MotionClock.Manual = true;
        try
        {
            var calls = 0;
            void Tick(TimeSpan _) => calls++;
            MotionClock.Subscribe(Tick);
            MotionClock.Unsubscribe(Tick);
            MotionClock.Subscribe(Tick);
            MotionClock.Advance(TimeSpan.FromMilliseconds(16));
            MotionClock.Advance(TimeSpan.FromMilliseconds(16));
            check(calls == 2, "unsubscribe and resubscribe in one tick keeps the subscription");
            MotionClock.Unsubscribe(Tick);
            var after = calls;
            MotionClock.Advance(TimeSpan.FromMilliseconds(16));
            check(calls == after, "unsubscribe removes the subscriber immediately");

            var nested = 0;
            void Nested(TimeSpan _)
            {
                nested++;
                if (nested == 1)
                {
                    MotionClock.Unsubscribe(Nested);
                    MotionClock.Subscribe(Nested);
                }
            }

            MotionClock.Subscribe(Nested);
            MotionClock.Advance(TimeSpan.FromMilliseconds(16));
            MotionClock.Advance(TimeSpan.FromMilliseconds(16));
            check(nested == 2, "unsubscribe and resubscribe inside a tick keeps the subscription");
            MotionClock.Unsubscribe(Nested);
            var skipped = 0;
            void Later(TimeSpan _) => skipped++;
            MotionClock.Subscribe(Later);
            MotionClock.Unsubscribe(Later);
            MotionClock.Advance(TimeSpan.FromMilliseconds(16));
            check(skipped == 0, "a subscriber removed before the tick is not invoked");
        }
        finally
        {
            MotionClock.Manual = previous;
        }
    }

    public static void RunInteractionChecks(Action<bool, string> check)
    {
        var reduceMotion = DashboardMotion.ReduceMotion;
        var manual = MotionClock.Manual;
        AppBuilder.Configure<InteractionApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();
        try
        {
            DashboardMotion.ReduceMotion = false;
            MotionClock.Manual = true;
            CheckPageSurvivesDetach(check);
            CheckSwitchSurvivesDetach(check);
            CheckCustomHitTargets(check);
            CheckClosedViewCanBeCollected(check);
        }
        finally
        {
            DashboardMotion.ReduceMotion = reduceMotion;
            MotionClock.Manual = manual;
        }
    }

    private static void CheckPageSurvivesDetach(Action<bool, string> check)
    {
        var view = new NetworkAcceleratorView
        {
            Width = 1360,
            Height = 900
        };
        var window = new Window
        {
            Width = 1360,
            Height = 900,
            Content = view,
            WindowDecorations = WindowDecorations.None
        };
        window.Show();
        Settle(window);
        view.PreviewNavigate("Settings");
        var settings = view.GetVisualDescendants().OfType<ToggleSwitch>()
            .Single(control => AutomationProperties.GetName(control) == "TUN 模式");
        check(!Hits(window, settings, Center(settings)), "mid-transition settings switch is not hit-testable yet");
        window.Content = null;
        Settle(window);
        window.Content = view;
        Settle(window);
        check(Hits(window, settings, Center(settings)), "returning mid-transition leaves the selected page clickable");
        view.PreviewNavigate("Overview");
        window.Content = null;
        Settle(window);
        window.Content = view;
        Settle(window);
        var proxy = view.GetVisualDescendants().OfType<AccentSwitch>()
            .Single(control => AutomationProperties.GetName(control) == "系统代理");
        var tun = view.GetVisualDescendants().OfType<AccentSwitch>()
            .Single(control => AutomationProperties.GetName(control) == "虚拟网卡");
        var lan = view.GetVisualDescendants().OfType<AccentSwitch>()
            .Single(control => AutomationProperties.GetName(control) == "局域网代理");
        var power = view.GetVisualDescendants().OfType<PowerButton>().Single();
        check(power.Focusable && AutomationProperties.GetName(power) == "开始或停止加速", "start button is focusable and named");
        check(Hits(window, power, Center(power)), "overview start button centre resolves to the button");
        var outsideCircle = new Point(1, 1);
        check(OutsideCircle(power, outsideCircle), "start button corner sample is outside the face");
        check(!Hits(window, power, outsideCircle), "overview start button corner does not hit the button");
        var starts = 0;
        power.Click += (_, _) => starts++;
        Click(window, power);
        check(starts == 1, "overview start button click raises Click");
        Click(window, power, outsideCircle);
        check(starts == 1, "overview start button ignores a point outside the circle");
        foreach (var toggle in new[] { proxy, tun, lan })
        {
            var name = AutomationProperties.GetName(toggle);
            check(toggle.Focusable, name + " switch is focusable");
            check(Hits(window, toggle, Center(toggle)), name + " switch centre is hit-testable");
            var outsideTrack = new Point(0.5, 0.5);
            check(!Hits(window, toggle, outsideTrack), name + " switch corner outside the track is not hit-testable");
        }

        var before = proxy.IsChecked == true;
        Click(window, proxy);
        check(proxy.IsChecked == true != before, "overview system proxy toggles after the interrupted transition");
        window.Close();
        view.DisposeResources();
    }

    private static void CheckSwitchSurvivesDetach(Action<bool, string> check)
    {
        var toggle = new AccentSwitch { IsChecked = false };
        var window = new Window
        {
            Width = 200,
            Height = 120,
            Content = toggle,
            WindowDecorations = WindowDecorations.None
        };
        window.Show();
        Settle(window);
        Click(window, toggle);
        check(toggle.IsChecked == true, "accent switch toggles on click");
        window.Content = null;
        Settle(window);
        check(Math.Abs(toggle.ThumbPosition - 1) < 0.001, "detached switch snaps the thumb to the checked end");
        window.Content = toggle;
        Settle(window);
        Click(window, toggle);
        check(toggle.IsChecked == false, "accent switch toggles after detach and reattach");
        MotionClock.Advance(TimeSpan.FromMilliseconds(400));
        check(Math.Abs(toggle.ThumbPosition) < 0.001, "reattached switch thumb finishes at the off end");
        window.Close();
    }

    private static void CheckCustomHitTargets(Action<bool, string> check)
    {
        var button = new PowerButton();
        var toggle = new AccentSwitch { IsChecked = false };
        var root = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        root.Children.Add(button);
        root.Children.Add(toggle);
        var window = new Window
        {
            Width = 240,
            Height = 200,
            Content = root,
            WindowDecorations = WindowDecorations.None
        };
        window.Show();
        Settle(window);
        var outsideCircle = new Point(1, 1);
        check(button.Focusable && button.IsTabStop, "standalone start button accepts keyboard focus");
        check(AutomationProperties.GetName(button) == "开始或停止加速", "standalone start button has an accessible name");
        check(Hits(window, button, Center(button)), "standalone start button centre is hit-testable");
        check(OutsideCircle(button, outsideCircle) && !Hits(window, button, outsideCircle), "standalone start button corner is outside the hit circle");
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        Click(window, button);
        check(clicks == 1, "mouse down and up on the start circle raises Click");
        Click(window, button, outsideCircle);
        check(clicks == 1, "mouse down and up outside the start circle does not raise Click");
        check(button.Focus(), "start button takes focus");
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.None, null!);
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.None, null!);
        check(clicks == 2, "space activates the focused start button");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null!);
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null!);
        check(clicks == 3, "enter activates the focused start button");

        check(Hits(window, toggle, Center(toggle)), "accent switch centre is hit-testable");
        check(!Hits(window, toggle, new Point(0.5, 0.5)), "accent switch corner outside the track is not hit-testable");
        Click(window, toggle);
        check(toggle.IsChecked == true, "accent switch centre click toggles");
        Click(window, toggle, new Point(0.5, 0.5));
        check(toggle.IsChecked == true, "accent switch corner does not toggle");
        check(toggle.Focus(), "accent switch takes focus");
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.None, null!);
        check(toggle.IsChecked == false, "space toggles the focused accent switch");
        window.Close();
    }

    private static void CheckClosedViewCanBeCollected(Action<bool, string> check)
    {
        var weak = CreateClosedView();
        for (var tick = 0; tick < 3; tick++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            Dispatcher.UIThread.RunJobs(DispatcherPriority.SystemIdle);
        }

        Collect();
        check(!weak.IsAlive, "a closed network accelerator view can be collected");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Collect()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateClosedView()
    {
        var view = new NetworkAcceleratorView { Width = 1360, Height = 900 };
        var window = new Window
        {
            Width = 1360,
            Height = 900,
            Content = view,
            WindowDecorations = WindowDecorations.None
        };
        window.Show();
        Settle(window);
        view.PreviewNavigate("Proxies");
        window.Content = null;
        window.Close();
        view.DisposeResources();
        return new WeakReference(view);
    }

    private static Point Center(Control control) => new(control.Bounds.Width / 2, control.Bounds.Height / 2);

    private static bool OutsideCircle(Control control, Point local)
    {
        var radius = Math.Min(control.Bounds.Width, control.Bounds.Height) / 2;
        var dx = local.X - control.Bounds.Width / 2;
        var dy = local.Y - control.Bounds.Height / 2;
        return new Rect(control.Bounds.Size).Contains(local) && dx * dx + dy * dy > radius * radius;
    }

    private static void Click(Window window, Control control) => Click(window, control, Center(control));

    private static void Click(Window window, Control control, Point local)
    {
        var point = control.TranslatePoint(local, window) ?? local;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    private static bool Hits(Window window, Control control, Point local)
    {
        var point = control.TranslatePoint(local, window);
        if (point is null) return false;
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
        var hit = window.InputHitTest(point.Value);
        return hit is Visual found && (ReferenceEquals(found, control) || control.IsVisualAncestorOf(found));
    }

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }
}

file sealed class InteractionApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new SemiTheme());
        Styles.Add(new UrsaSemiTheme());
        Styles.Add(new StyleInclude(new Uri("avares://AsterDock.UI/"))
        {
            Source = new Uri("avares://AsterDock.UI/Themes/AsterDockTheme.axaml")
        });
    }
}
