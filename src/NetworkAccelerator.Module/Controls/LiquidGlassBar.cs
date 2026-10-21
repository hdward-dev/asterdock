using System.Collections.Specialized;
using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace NetworkAccelerator.Module.Controls;

/// <summary>
/// Floating capsule drawn over the page. Avalonia 12 has no in-window backdrop
/// blur of sibling content (experimental acrylic samples the window background),
/// so this snapshots only the page region behind the bar and blurs that bitmap.
/// The snapshot is refreshed on scroll, layout, and navigation, not every frame.
/// </summary>
public class LiquidGlassBar : Decorator
{
    public const double Corner = 24;
    public const double BackdropBlurRadius = 8;
    public const double SlowBackdropBlurRadius = 5;
    private const double Pad = 16;
    private const double SlowSnapshotMs = 40;
    private const double HoverScale = 1.015;
    private const double PressScale = 1.02;

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
        Schedule();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        DashboardMotion.Changed -= OnMotionChanged;
        _hovered = false;
        _pressed = false;
        ReleaseChrome();
        _captureGeneration++;
        _queued = false;
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
            if (SnapshotMilliseconds > SlowSnapshotMs && _radius > SlowBackdropBlurRadius)
            {
                _radius = SlowBackdropBlurRadius;
                SnapshotNote += " (next snapshot uses radius 5)";
            }
            else if (SnapshotMilliseconds > SlowSnapshotMs && _scaleCap > 0.6)
            {
                _scaleCap = 0.6;
                SnapshotNote += " (next snapshot scale capped)";
            }

            InvalidateVisual();
        }
        catch (Exception ex)
        {
            BackdropBlurActive = false;
            SnapshotNote = ex.GetBaseException().Message;
            InvalidateVisual();
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
        _chrome.BoxShadow = CreateOuterShadow(dark, _hovered, _pressed);
        var scale = _pressed ? PressScale : _hovered ? HoverScale : 1;
        _chrome.RenderTransform = new ScaleTransform(scale, scale);
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
}
