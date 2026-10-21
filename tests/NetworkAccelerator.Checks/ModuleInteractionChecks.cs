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
        check(!CanHit(settings), "mid-transition settings switch is not hit-testable yet");
        window.Content = null;
        Settle(window);
        window.Content = view;
        Settle(window);
        check(CanHit(settings), "returning mid-transition leaves the selected page clickable");
        view.PreviewNavigate("Overview");
        window.Content = null;
        Settle(window);
        window.Content = view;
        Settle(window);
        var proxy = view.GetVisualDescendants().OfType<AccentSwitch>()
            .Single(control => AutomationProperties.GetName(control) == "系统代理");
        var power = view.GetVisualDescendants().OfType<PowerButton>().Single();
        check(CanHit(proxy) && CanHit(power), "returning to the overview leaves the switch and start button clickable");
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

    private static void Click(Window window, Control control)
    {
        var center = new Point(control.Bounds.Width / 2, control.Bounds.Height / 2);
        var point = control.TranslatePoint(center, window) ?? center;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    private static bool CanHit(Visual visual)
    {
        for (var node = visual; node is not null; node = node.GetVisualParent())
        {
            if (!node.IsVisible) return false;
            if (node is InputElement input && !input.IsHitTestVisible) return false;
        }

        return true;
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
