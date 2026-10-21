using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
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
    private const double Pad = 16;
    private const double SlowSnapshotMs = 40;

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
    private bool _queued;
    private bool _capturing;
    private double _radius = 5;
    private double _scaleCap = 1;

    public LiquidGlassBar()
    {
        SizeChanged += (_, _) => Schedule();
        ActualThemeVariantChanged += (_, _) =>
        {
            ApplyChrome();
            Schedule();
        };
        ApplyChrome();
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
            if (_frame is not null)
                context.DrawImage(_frame, rect);
            context.FillRectangle(_fill, rect);
            context.FillRectangle(_sheen, rect);
        }

        if (_rim is not null)
        {
            var stroke = rect.Inflate(-0.5);
            context.DrawRectangle(null, _rim, new RoundedRect(stroke, Math.Max(0, Corner - 0.5)));
        }

        context.DrawRectangle(null, null, shape, _inset);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
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
        if (_queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            CaptureNow();
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
        RenderTargetBitmap? cropped = null;
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

            cropped = CreateTarget(Bounds.Width, Bounds.Height, scale);
            using (var context = cropped.CreateDrawingContext())
            {
                context.DrawImage(
                    blurred,
                    new Rect(Pad, Pad, Bounds.Width, Bounds.Height),
                    new Rect(0, 0, Bounds.Width, Bounds.Height));
            }

            var previous = _frame;
            _frame = cropped;
            cropped = null;
            previous?.Dispose();
            BackdropBlurActive = true;
            SnapshotMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            SnapshotNote = $"blur radius {_radius:0.#} scale {scale:0.00} in {SnapshotMilliseconds:0.0} ms";
            if (SnapshotMilliseconds > SlowSnapshotMs && (_radius > 3 || _scaleCap > 0.6))
            {
                _radius = 3;
                _scaleCap = Math.Min(_scaleCap, 0.6);
                SnapshotNote += " (next snapshot capped)";
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
            cropped?.Dispose();
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

        _fill = new SolidColorBrush(dark
            ? Color.FromArgb(148, 0x1E, 0x24, 0x30)
            : Color.FromArgb(153, 255, 255, 255));
        _sheen = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)(dark ? 42 : 90), 255, 255, 255), 0),
                new GradientStop(Colors.Transparent, 0.36),
                new GradientStop(Colors.Transparent, 1)
            }
        };
        var rim = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(dark ? Color.Parse("#99FFFFFF") : Color.Parse("#F5FFFFFF"), 0),
                new GradientStop(dark ? Color.Parse("#18FFFFFF") : Color.Parse("#38FFFFFF"), 1)
            }
        };
        _rim = new Pen(rim, 1);
        _inset = new BoxShadows(new BoxShadow
        {
            IsInset = true,
            OffsetY = 1,
            Blur = 8,
            Color = dark ? Color.FromArgb(0x66, 0, 0, 0) : Color.FromArgb(0x1C, 0, 0, 0)
        });
        InvalidateVisual();
    }
}
