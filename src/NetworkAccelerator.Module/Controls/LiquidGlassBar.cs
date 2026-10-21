using System.Collections.Specialized;
using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using Avalonia.Threading;
using Fluid.Avalonia.Acrylic;

namespace NetworkAccelerator.Module.Controls;

/// <summary>
/// Floating capsule drawn over the page. Prefers a Fluid <see cref="AcrylicInteractiveSurface"/>
/// with the library refraction (height 12, amount 24) and the demo-card frost, sampling
/// whatever page pixels sit behind it. Falls back to a page snapshot blurred with
/// <see cref="BlurEffect"/> when software Skia or the frame budget is too slow, and stays
/// there until <see cref="ResetSessionChoice"/>.
/// </summary>
public class LiquidGlassBar : Decorator
{
    public const double Corner = 24;
    public const double BackdropBlurRadius = 8;
    public const double SlowBackdropBlurRadius = 5;

    /// <summary>Fluid demo cards with a 24 corner: blur 14, vibrancy 1.4, brightness 0.06.</summary>
    public const double AcrylicBlurRadius = 14;

    public const double AcrylicVibrancy = 1.4;
    public const double AcrylicBrightness = 0.06;

    /// <summary>Library defaults. The lens sample is the one that zeros refraction; this nav does not.</summary>
    public const double AcrylicRefractionHeight = 12;

    public const double AcrylicRefractionAmount = 24;

    /// <summary>
    /// Theme wash over the sampled page. Alpha stays low: the shader fills with
    /// tint alpha × 0.75, and a heavier veil turns the frost into a solid plate.
    /// </summary>
    public const byte AcrylicLightTintAlpha = 0x40;

    public const byte AcrylicDarkTintAlpha = 0x50;

    /// <summary>Library highlight at rest. Hover and press raise it so the rim reads.</summary>
    public const double AcrylicHighlightRest = 0.5;

    public const double AcrylicHighlightHover = 0.95;

    private const double Pad = 16;
    private const double HoverScale = 1.015;
    private const double PressScale = 1.02;
    private const double AcrylicHoverScale = 1.05;
    private const double AcrylicPressScale = 1.07;

    private static readonly List<WeakReference<LiquidGlassBar>> LiveBars = new();
    private static NavGlassPath _session = NavGlassPath.Pending;
    private static bool _softwareStaticKnown;
    private static bool? _softwareStatic;

    private static readonly MethodInfo? RenderVisual = typeof(RenderTargetBitmap).Assembly
        .GetType("Avalonia.Rendering.ImmediateRenderer")
        ?.GetMethod(
            "Render",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(DrawingContext), typeof(Visual)],
            modifiers: null);

    private ScrollViewer? _page;
    private RenderTargetBitmap? _frame;
    private IBrush _pageBrush = Brushes.White;
    private IBrush _fill = new SolidColorBrush(Color.FromArgb(153, 255, 255, 255));
    private IBrush _sheen = Brushes.Transparent;
    private IPen? _rim;
    private BoxShadows _inset;
    private BoxShadows _outer;
    private Border? _chrome;
    private bool _hovered;
    private bool _pressed;
    private bool _attached;
    private double _frameWidth;
    private double _frameHeight;
    private bool _queued;
    private bool _capturing;
    private int _captureGeneration;
    private double _radius = BackdropBlurRadius;
    private double _scaleCap = 1;
    private Control? _payload;
    private AcrylicSurface? _acrylic;
    private NavGlassPath _path = NavGlassPath.Pending;
    private bool _probeDone;
    private bool _probeFailed;
    private double _probeMs;
    private bool _rendererKnown;
    private bool? _software;
    private bool _rendererGracePosted;
    private bool _adoptPosted;
    private bool _watching;
    private int _watchSerial;
    private int _slowStreak;
    private int _watchedFrames;
    private long _surfaceFailuresAtWatch;
    private TimeSpan? _lastFrame;

    /// <summary>Process-wide glass path. Approximate stays put until <see cref="ResetSessionChoice"/>.</summary>
    public static NavGlassPath SessionPath => _session;

    /// <summary>
    /// When set, the next undecided bar uses this probe instead of measuring.
    /// Ignored once the session has already chosen. Cleared by <see cref="ResetSessionChoice"/>.
    /// </summary>
    public static NavGlassProbe? ProbeOverride { get; set; }

    public static void ResetSessionChoice()
    {
        _session = NavGlassPath.Pending;
        ProbeOverride = null;
        _softwareStaticKnown = false;
        _softwareStatic = null;
    }

    public NavGlassPath ActivePath => _path;

    public bool? SoftwareRenderer => _software;

    public bool RendererKnown => _rendererKnown;

    /// <summary>
    /// Light wash over AppPageBrush (#F5F7FA) and the same slate the approximate veil uses in the dark theme.
    /// </summary>
    public static Color CreateAcrylicTint(bool dark) =>
        dark
            ? Color.FromArgb(AcrylicDarkTintAlpha, 0x2A, 0x3A, 0x52)
            : Color.FromArgb(AcrylicLightTintAlpha, 0xF5, 0xF7, 0xFA);

    public LiquidGlassBar()
    {
        AddHandler(PointerPressedEvent, OnNavPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnNavReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        SizeChanged += (_, _) => Schedule();
        ActualThemeVariantChanged += (_, _) =>
        {
            ApplyInteraction();
            Schedule();
        };
        ApplyChrome();
    }

    /// <summary>
    /// One outer drop: blur 24, offset (0, 4), black at about 10%.
    /// Hover and press raise alpha only. Dark uses a modest bump so the same drop still reads.
    /// </summary>
    public static BoxShadows CreateOuterShadow(bool dark, bool hovered, bool pressed)
    {
        byte alpha = dark
            ? pressed ? (byte)0x66 : hovered ? (byte)0x4D : (byte)0x33
            : pressed ? (byte)0x4D : hovered ? (byte)0x33 : (byte)0x1A;
        return new BoxShadows(new BoxShadow
        {
            OffsetY = 4,
            Blur = 24,
            Color = Color.FromArgb(alpha, 0, 0, 0)
        });
    }

    public bool BackdropBlurActive { get; private set; }

    public double SnapshotMilliseconds { get; private set; }

    public string SnapshotNote { get; private set; } = "pending";

    public ScrollViewer? Backdrop
    {
        get => _page;
        set
        {
            if (ReferenceEquals(_page, value)) return;
            if (_page is not null)
            {
                _page.PropertyChanged -= OnPageProperty;
                _page.SizeChanged -= OnPageSized;
            }

            _page = value;
            if (_page is not null)
            {
                _page.PropertyChanged += OnPageProperty;
                _page.SizeChanged += OnPageSized;
            }

            Schedule();
        }
    }

    public void Refresh() => Schedule();

    public override void Render(DrawingContext context)
    {
        if (_attached && ProbeOverride is null && !_rendererKnown && _session == NavGlassPath.Pending)
            context.Custom(new RendererProbe(this, Bounds.Size));
        if (_path == NavGlassPath.Acrylic) return;

        var rect = new Rect(Bounds.Size);
        if (rect.Width < 1 || rect.Height < 1) return;
        var shape = new RoundedRect(rect, Corner);
        using (context.PushClip(shape))
        {
            context.FillRectangle(_pageBrush, rect);
            DrawBackdrop(context, rect);
            context.FillRectangle(_fill, rect);
            context.FillRectangle(_sheen, rect);
        }

        if (_rim is not null)
        {
            var stroke = rect.Inflate(-0.5);
            context.DrawRectangle(null, _rim, new RoundedRect(stroke, Math.Max(0, Corner - 0.5)));
        }

        context.DrawRectangle(null, null, shape, _inset);
        if (!AncestorCastsOuterShadow())
            context.DrawRectangle(null, null, shape, _outer);
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _hovered = true;
        ApplyChrome();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hovered = false;
        _pressed = false;
        NavChrome()?.Classes.Remove("down");
        ApplyChrome();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        DashboardMotion.Changed += OnMotionChanged;
        WatchChrome();
        lock (LiveBars) LiveBars.Add(new WeakReference<LiquidGlassBar>(this));
        if (_softwareStaticKnown)
        {
            _rendererKnown = true;
            _software = _softwareStatic;
        }

        if (_session == NavGlassPath.Approximate)
            EnterApproximate();
        else if (_session == NavGlassPath.Acrylic)
            PostAdoptAcrylic();
        else if (ProbeOverride is { } probe)
            ApplyProbe(probe);
        else
            Schedule();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _watching = false;
        _watchSerial++;
        _adoptPosted = false;
        DashboardMotion.Changed -= OnMotionChanged;
        _hovered = false;
        _pressed = false;
        ReleaseChrome();
        _captureGeneration++;
        _queued = false;
        lock (LiveBars)
        {
            LiveBars.RemoveAll(weak => !weak.TryGetTarget(out var bar) || ReferenceEquals(bar, this));
        }
        Backdrop = null;
        _frame?.Dispose();
        _frame = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnPageProperty(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ScrollViewer.OffsetProperty)
            Schedule();
    }

    private void OnPageSized(object? sender, SizeChangedEventArgs e) => Schedule();

    private void Schedule()
    {
        if (_path == NavGlassPath.Acrylic || _session == NavGlassPath.Acrylic) return;
        if (!_attached || _queued) return;
        _queued = true;
        var generation = _captureGeneration;
        var weak = new WeakReference<LiquidGlassBar>(this);
        Dispatcher.UIThread.Post(() =>
        {
            if (!weak.TryGetTarget(out var bar) || !bar._attached || bar._captureGeneration != generation)
                return;
            bar._queued = false;
            bar.CaptureNow();
        }, DispatcherPriority.Background);
    }

    private void CaptureNow()
    {
        if (_capturing || _page is null || Bounds.Width < 2 || Bounds.Height < 2)
            return;
        if (RenderVisual is null)
        {
            BackdropBlurActive = false;
            SnapshotNote = "no in-window blur API";
            InvalidateVisual();
            FinishProbe(0, failed: true);
            return;
        }

        var origin = this.TranslatePoint(new Point(-Pad, -Pad), _page);
        if (origin is null) return;

        _capturing = true;
        var started = Stopwatch.GetTimestamp();
        RenderTargetBitmap? sharp = null;
        RenderTargetBitmap? blurred = null;
        try
        {
            var dipW = Bounds.Width + Pad * 2;
            var dipH = Bounds.Height + Pad * 2;
            var scale = ScaleFor(dipW, dipH);
            sharp = CreateTarget(dipW, dipH, scale);
            using (var context = sharp.CreateDrawingContext())
            using (context.PushTransform(Matrix.CreateTranslation(-origin.Value.X, -origin.Value.Y)))
                RenderVisual.Invoke(null, [context, _page]);

            blurred = CreateTarget(dipW, dipH, scale);
            var host = new Image
            {
                Source = sharp,
                Stretch = Stretch.Fill,
                Width = dipW,
                Height = dipH,
                Effect = new BlurEffect { Radius = _radius }
            };
            host.Measure(new Size(dipW, dipH));
            host.Arrange(new Rect(0, 0, dipW, dipH));
            blurred.Render(host);

            var previous = _frame;
            _frame = blurred;
            _frameWidth = dipW;
            _frameHeight = dipH;
            blurred = null;
            previous?.Dispose();
            BackdropBlurActive = true;
            SnapshotMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            SnapshotNote = $"blur radius {_radius:0.#} scale {scale:0.00} in {SnapshotMilliseconds:0.0} ms";
            if (SnapshotMilliseconds > NavGlassChoice.SlowMilliseconds && _radius > SlowBackdropBlurRadius)
            {
                _radius = SlowBackdropBlurRadius;
                SnapshotNote += " (next snapshot uses radius 5)";
            }
            else if (SnapshotMilliseconds > NavGlassChoice.SlowMilliseconds && _scaleCap > 0.6)
            {
                _scaleCap = 0.6;
                SnapshotNote += " (next snapshot scale capped)";
            }

            InvalidateVisual();
            FinishProbe(SnapshotMilliseconds, failed: false);
        }
        catch (Exception ex)
        {
            BackdropBlurActive = false;
            SnapshotNote = ex.GetBaseException().Message;
            InvalidateVisual();
            FinishProbe(SnapshotMilliseconds, failed: true);
        }
        finally
        {
            sharp?.Dispose();
            blurred?.Dispose();
            _capturing = false;
        }
    }

    private double ScaleFor(double dipW, double dipH)
    {
        var scale = Math.Min(_scaleCap, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
        const double maxPixels = 120_000;
        var pixels = dipW * dipH * scale * scale;
        if (pixels > maxPixels)
            scale *= Math.Sqrt(maxPixels / pixels);
        return Math.Max(0.4, scale);
    }

    private static RenderTargetBitmap CreateTarget(double dipW, double dipH, double scale)
    {
        var pixels = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(dipW * scale)),
            Math.Max(1, (int)Math.Ceiling(dipH * scale)));
        var dpi = 96 * scale;
        return new RenderTargetBitmap(pixels, new Vector(dpi, dpi));
    }

    private void ApplyChrome()
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        if (this.TryFindResource("AppPageBrush", ActualThemeVariant, out var page) && page is IBrush pageBrush)
            _pageBrush = pageBrush;
        else
            _pageBrush = new SolidColorBrush(dark ? Color.Parse("#111318") : Color.Parse("#F5F7FA"));

        var veil = (byte)(_pressed ? 96 : _hovered ? 112 : 132);
        _fill = new SolidColorBrush(dark
            ? Color.FromArgb(veil, 0x2A, 0x3A, 0x52)
            : Color.FromArgb((byte)(veil + 8), 255, 255, 255));
        var sheenAlpha = (byte)(_pressed ? 204 : _hovered ? 170 : 128);
        _sheen = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(sheenAlpha, 255, 255, 255), 0),
                new GradientStop(Colors.Transparent, 1)
            }
        };
        var rim = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)(_pressed ? 0xF5 : _hovered ? 0xE0 : 0xCC), 255, 255, 255), 0),
                new GradientStop(dark ? Color.Parse("#18FFFFFF") : Color.Parse("#38FFFFFF"), 1)
            }
        };
        _rim = new Pen(rim, 1);
        var lip = (byte)(_pressed ? 0xC0 : _hovered ? 0xA0 : 0x80);
        _inset = new BoxShadows(
            new BoxShadow
            {
                IsInset = true,
                OffsetY = 1,
                Blur = 12,
                Color = Color.FromArgb(dark ? (byte)0x55 : (byte)0x22, 0, 0, 0)
            },
            [
                new BoxShadow
                {
                    IsInset = true,
                    OffsetY = -1,
                    Blur = 2,
                    Color = Color.FromArgb(lip, 255, 255, 255)
                }
            ]);
        _outer = CreateOuterShadow(dark, _hovered, _pressed);
        if (_acrylic is not null)
            _acrylic.TintColor = CreateAcrylicTint(dark);
        InvalidateVisual();
    }

    private void OnNavPressed(object? sender, PointerPressedEventArgs e)
    {
        var chrome = NavChrome();
        if (chrome is null)
        {
            _pressed = true;
            ApplyChrome();
            return;
        }

        chrome.Classes.Add("down");
    }

    private void OnNavReleased(object? sender, PointerReleasedEventArgs e)
    {
        var chrome = NavChrome();
        if (chrome is null)
        {
            _pressed = false;
            ApplyChrome();
            return;
        }

        chrome.Classes.Remove("down");
    }

    private void WatchChrome()
    {
        var chrome = NavChrome();
        if (ReferenceEquals(_chrome, chrome)) return;
        ReleaseChrome();
        _chrome = chrome;
        if (_chrome is null) return;
        _chrome.Classes.CollectionChanged += OnChromeClasses;
        _chrome.ActualThemeVariantChanged += OnChromeTheme;
        HoldTransitionsForMotion();
        ApplyInteraction();
    }

    private void OnChromeTheme(object? sender, EventArgs e) => ApplyInteraction();

    private void ReleaseChrome()
    {
        if (_chrome is null) return;
        _chrome.Classes.CollectionChanged -= OnChromeClasses;
        _chrome.ActualThemeVariantChanged -= OnChromeTheme;
        _chrome.Classes.Remove("down");
        _chrome.ClearValue(Border.BoxShadowProperty);
        _chrome.ClearValue(Visual.RenderTransformProperty);
        _chrome.ClearValue(Animatable.TransitionsProperty);
        _chrome = null;
    }

    private void OnMotionChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            var weak = new WeakReference<LiquidGlassBar>(this);
            Dispatcher.UIThread.Post(() =>
            {
                if (weak.TryGetTarget(out var bar)) bar.OnMotionChanged();
            });
            return;
        }

        HoldTransitionsForMotion();
    }

    private void HoldTransitionsForMotion()
    {
        if (_chrome is null) return;
        if (DashboardMotion.ReduceMotion)
        {
            if (_chrome.Transitions is { Count: > 0 })
                _chrome.Transitions = new Transitions();
            return;
        }

        _chrome.ClearValue(Animatable.TransitionsProperty);
    }

    private void OnChromeClasses(object? sender, NotifyCollectionChangedEventArgs e) => ApplyInteraction();

    private void ApplyInteraction()
    {
        if (_chrome is not null)
        {
            _hovered = _chrome.Classes.Contains(":pointerover");
            _pressed = _chrome.Classes.Contains("down");
        }

        ApplyChrome();
        if (_chrome is null) return;
        HoldTransitionsForMotion();
        var dark = _chrome.ActualThemeVariant == ThemeVariant.Dark;
        if (_path == NavGlassPath.Acrylic)
        {
            // Clear the chrome drop before any BoxShadow transition can play it back.
            // Fluid draws the only shadow; the chrome only eases its hover scale.
            _chrome.Transitions = new Transitions();
            _chrome.ClearValue(Border.BoxShadowProperty);
            if (!DashboardMotion.ReduceMotion)
            {
                _chrome.Transitions = new Transitions
                {
                    new TransformOperationsTransition
                    {
                        Property = Visual.RenderTransformProperty,
                        Duration = TimeSpan.FromMilliseconds(160)
                    }
                };
            }

            ApplyAcrylicHover();
        }
        else
            _chrome.BoxShadow = CreateOuterShadow(dark, _hovered, _pressed);
        var scale = _path == NavGlassPath.Acrylic
            ? _pressed ? AcrylicPressScale : _hovered ? AcrylicHoverScale : 1
            : _pressed ? PressScale : _hovered ? HoverScale : 1;
        _chrome.RenderTransform = new ScaleTransform(scale, scale);
    }

    private void ApplyAcrylicHover()
    {
        if (_acrylic is null) return;
        _acrylic.HighlightOpacity = _pressed || _hovered ? AcrylicHighlightHover : AcrylicHighlightRest;
        var alpha = _pressed ? (byte)0x73 : _hovered ? (byte)0x55 : (byte)0x1A;
        _acrylic.ShadowColor = Color.FromArgb(alpha, 0, 0, 0);
    }

    private void DrawBackdrop(DrawingContext context, Rect rect)
    {
        if (_frame is null) return;
        // Uniform pull of the padded snapshot stands in for a 24dp lens. Edges sample
        // nearby backdrop; there is no per-pixel refraction.
        var pull = Math.Min(Pad - 1, _pressed ? 12 : _hovered ? 8 : 6);
        var source = new Rect(Pad - pull, Pad - pull, rect.Width + pull * 2, rect.Height + pull * 2);
        if (source.X >= 0 && source.Y >= 0 && source.Right <= _frameWidth + 0.5 && source.Bottom <= _frameHeight + 0.5)
            context.DrawImage(_frame, source, rect);
        else
            context.DrawImage(_frame, rect);
    }

    private Border? NavChrome()
    {
        for (StyledElement? node = this; node is not null; node = node.Parent)
        {
            if (node is Border border && border.Classes.Contains("nav-float"))
                return border;
        }

        return null;
    }

    private bool AncestorCastsOuterShadow()
    {
        var chrome = NavChrome();
        return chrome is not null && chrome.BoxShadow.Count > 0;
    }

    /// <summary>
    /// Records one frame interval while acrylic is up. Three consecutive intervals over
    /// <see cref="NavGlassChoice.SlowMilliseconds"/> retire acrylic for the session.
    /// Gaps of 500ms or more reset the streak so a stalled frame does not count as glass cost.
    /// </summary>
    public bool ReportFrameMilliseconds(double milliseconds)
    {
        if (_session == NavGlassPath.Approximate || _path != NavGlassPath.Acrylic)
            return false;
        if (double.IsNaN(milliseconds) || milliseconds >= 500)
        {
            _slowStreak = 0;
            return false;
        }

        _watchedFrames++;
        if (milliseconds > NavGlassChoice.SlowMilliseconds)
            _slowStreak++;
        else
            _slowStreak = 0;

        if (NavGlassChoice.SustainedSlow(_slowStreak))
        {
            LockApproximate("sustained frame cost");
            return true;
        }

        if (_watchedFrames >= NavGlassChoice.StableFramesBeforeLock)
            _watching = false;
        return false;
    }

    private void ApplyProbe(NavGlassProbe probe)
    {
        _software = probe.SoftwareRenderer;
        _rendererKnown = true;
        _probeDone = true;
        _probeFailed = probe.Failed;
        _probeMs = probe.SnapshotMilliseconds;
        TryDecide();
    }

    private void FinishProbe(double milliseconds, bool failed)
    {
        if (_probeDone || _session != NavGlassPath.Pending || ProbeOverride is not null)
            return;
        _probeDone = true;
        _probeFailed = failed;
        _probeMs = milliseconds;
        if (!_rendererKnown)
            GraceRenderer();
        else
            TryDecide();
    }

    private void GraceRenderer()
    {
        if (_rendererKnown || _rendererGracePosted) return;
        _rendererGracePosted = true;
        InvalidateVisual();
        var generation = _captureGeneration;
        var weak = new WeakReference<LiquidGlassBar>(this);
        Dispatcher.UIThread.Post(() =>
        {
            if (!weak.TryGetTarget(out var bar) || !bar._attached || bar._captureGeneration != generation)
                return;
            bar._rendererGracePosted = false;
            if (SessionPath != NavGlassPath.Pending) return;
            if (!bar._rendererKnown)
            {
                bar._rendererKnown = true;
                bar._software = _softwareStaticKnown ? _softwareStatic : null;
            }

            bar.TryDecide();
        }, DispatcherPriority.Background);
    }

    private void NoteRenderer(bool? software)
    {
        if (Dispatcher.UIThread.CheckAccess())
            ApplyRenderer(software);
        else
        {
            var weak = new WeakReference<LiquidGlassBar>(this);
            Dispatcher.UIThread.Post(() =>
            {
                if (weak.TryGetTarget(out var bar)) bar.ApplyRenderer(software);
            });
        }
    }

    private void ApplyRenderer(bool? software)
    {
        if (_rendererKnown && _software == software) return;
        _rendererKnown = true;
        _software = software;
        _softwareStaticKnown = true;
        _softwareStatic = software;
        TryDecide();
    }

    private void TryDecide()
    {
        if (!_attached) return;
        if (_session == NavGlassPath.Approximate)
        {
            if (_path != NavGlassPath.Approximate)
                EnterApproximate();
            return;
        }

        if (_session == NavGlassPath.Acrylic)
        {
            if (_path != NavGlassPath.Acrylic)
                PostAdoptAcrylic();
            return;
        }

        if (!_probeDone || !_rendererKnown) return;
        var probe = new NavGlassProbe(_software, _probeMs, _probeFailed);
        if (NavGlassChoice.UseApproximate(probe))
            LockApproximate(probe.Failed ? "probe failed" : "slow capture");
        else
            PostAdoptAcrylic();
    }

    private void LockApproximate(string reason)
    {
        var already = _session == NavGlassPath.Approximate;
        _session = NavGlassPath.Approximate;
        SnapshotNote = reason;
        if (!already)
        {
            LiquidGlassBar[] bars;
            lock (LiveBars)
            {
                bars = LiveBars
                    .Select(weak => weak.TryGetTarget(out var bar) ? bar : null)
                    .OfType<LiquidGlassBar>()
                    .ToArray();
            }

            foreach (var bar in bars)
                bar.EnterApproximate();
            return;
        }

        EnterApproximate();
    }

    private void EnterApproximate()
    {
        _watching = false;
        _watchSerial++;
        var fromAcrylic = _acrylic is not null;
        _path = NavGlassPath.Approximate;
        if (_acrylic is not null)
        {
            var payload = _payload ?? _acrylic.Content as Control;
            _acrylic.Content = null;
            if (ReferenceEquals(Child, _acrylic))
                Child = payload;
            _acrylic = null;
            _radius = BackdropBlurRadius;
            _scaleCap = 1;
        }

        if (fromAcrylic)
            SnapshotNote = "approximate blur";
        ApplyInteraction();
        InvalidateVisual();
        if (_attached)
            Schedule();
    }

    private void PostAdoptAcrylic()
    {
        if (_path == NavGlassPath.Acrylic && _acrylic is not null)
        {
            if (!_watching && ProbeOverride is null && _watchedFrames < NavGlassChoice.StableFramesBeforeLock)
                BeginWatch();
            return;
        }
        if (_session == NavGlassPath.Approximate)
        {
            EnterApproximate();
            return;
        }

        if (_adoptPosted) return;
        _adoptPosted = true;
        var generation = _captureGeneration;
        var weak = new WeakReference<LiquidGlassBar>(this);
        Dispatcher.UIThread.Post(() =>
        {
            if (!weak.TryGetTarget(out var bar)) return;
            bar._adoptPosted = false;
            if (!bar._attached || bar._captureGeneration != generation) return;
            if (_session == NavGlassPath.Approximate)
            {
                bar.EnterApproximate();
                return;
            }

            bar.EnterAcrylic();
        });
    }

    private void EnterAcrylic()
    {
        if (_session == NavGlassPath.Approximate)
        {
            EnterApproximate();
            return;
        }

        try
        {
            RememberPayload();
            if (_acrylic is null)
            {
                var surface = CreateAcrylicSurface();
                if (ReferenceEquals(Child, _payload))
                    Child = null;
                surface.Content = _payload;
                _acrylic = surface;
                Child = surface;
            }

            ExcludeAcrylicFromCapture();
            ApplyAcrylicTint();
            _path = NavGlassPath.Acrylic;
            _session = NavGlassPath.Acrylic;
            _captureGeneration++;
            _queued = false;
            _frame?.Dispose();
            _frame = null;
            BackdropBlurActive = false;
            SnapshotNote = $"acrylic blur {AcrylicBlurRadius:0.#} vibrancy {AcrylicVibrancy:0.#} refraction {AcrylicRefractionHeight:0.#}/{AcrylicRefractionAmount:0.#}";
            ApplyInteraction();
            InvalidateVisual();
            BeginWatch();
        }
        catch (Exception ex)
        {
            if (_acrylic is not null)
            {
                _acrylic.Content = null;
                _acrylic = null;
            }

            if (Child is null)
                Child = _payload;
            LockApproximate(ex.GetBaseException().Message);
        }
    }

    private void RememberPayload()
    {
        if (_payload is not null) return;
        _payload = Child is AcrylicSurface ? null : Child;
    }

    private AcrylicSurface CreateAcrylicSurface()
    {
        var surface = new AcrylicInteractiveSurface
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(Corner),
            BlurRadius = AcrylicBlurRadius,
            Vibrancy = AcrylicVibrancy,
            Brightness = AcrylicBrightness,
            RefractionHeight = AcrylicRefractionHeight,
            RefractionAmount = AcrylicRefractionAmount,
            HighlightEnabled = true,
            ShadowEnabled = true,
            RevealBorderEnabled = true,
            IsInteractive = true,
            InteractiveMaxScaleDip = 24
        };
        AcrylicBackdrop.SetIsExcludedFromCapture(surface, true);
        return surface;
    }

    private void ExcludeAcrylicFromCapture()
    {
        if (_acrylic is not null)
            AcrylicBackdrop.SetIsExcludedFromCapture(_acrylic, true);
        AcrylicBackdrop.SetIsExcludedFromCapture(this, true);
        var chrome = NavChrome();
        if (chrome is not null)
            AcrylicBackdrop.SetIsExcludedFromCapture(chrome, true);
    }

    private void ApplyAcrylicTint()
    {
        if (_acrylic is null) return;
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        _acrylic.TintColor = CreateAcrylicTint(dark);
    }

    private void BeginWatch()
    {
        if (ProbeOverride is not null) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        _watching = true;
        _watchSerial++;
        _slowStreak = 0;
        _watchedFrames = 0;
        _lastFrame = null;
        _surfaceFailuresAtWatch = AcrylicDiagnostics.Snapshot.FilterSurfaceFailures;
        var serial = _watchSerial;
        var weak = new WeakReference<LiquidGlassBar>(this);
        top.RequestAnimationFrame(time =>
        {
            if (weak.TryGetTarget(out var bar))
                bar.OnFrame(time, serial);
        });
    }

    private void OnFrame(TimeSpan time, int serial)
    {
        if (!_watching || !_attached || serial != _watchSerial || _path != NavGlassPath.Acrylic)
            return;
        if (AcrylicDiagnostics.Snapshot.FilterSurfaceFailures > _surfaceFailuresAtWatch)
        {
            LockApproximate("acrylic frame failed");
            return;
        }

        if (_lastFrame is { } previous && ReportFrameMilliseconds((time - previous).TotalMilliseconds))
            return;
        if (!_watching || _path != NavGlassPath.Acrylic) return;
        _lastFrame = time;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var weak = new WeakReference<LiquidGlassBar>(this);
        top.RequestAnimationFrame(next =>
        {
            if (weak.TryGetTarget(out var bar))
                bar.OnFrame(next, serial);
        });
    }

    private sealed class RendererProbe : ICustomDrawOperation
    {
        private readonly WeakReference<LiquidGlassBar> _bar;

        public RendererProbe(LiquidGlassBar bar, Size size)
        {
            _bar = new WeakReference<LiquidGlassBar>(bar);
            Bounds = new Rect(size);
        }

        public Rect Bounds { get; }

        public bool HitTest(Point point) => false;

        public void Dispose()
        {
        }

        public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);

        public void Render(ImmediateDrawingContext context)
        {
            if (!_bar.TryGetTarget(out var bar)) return;
            try
            {
                var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
                if (feature is null) return;
                using var lease = feature.Lease();
                bar.NoteRenderer(lease.GrContext is null);
            }
            catch (Exception)
            {
                // A later render can still lease Skia. Unknown is decided by the grace callback.
            }
        }
    }
}
