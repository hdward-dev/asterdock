using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using URemote.Core;
using URemote.Module;

AppBuilder.Configure<LayoutApp>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false, ShouldRenderOnUIThread = true })
    .UseSkia()
    .SetupWithoutStarting();
var failed = false;
void Check(bool condition, string message)
{
    if (!condition) { failed = true; Console.WriteLine("FAIL: " + message); }
    else Console.WriteLine("PASS: " + message);
}

Check(DeviceCardWrap.Resolve(900, 380, 640, 16, true) is (2, var wide) && Math.Abs(wide - 442) < 0.01, "900px viewport uses two cards that fill the row");
Check(DeviceCardWrap.Resolve(1400, 380, 640, 16, true) is (3, var mid) && Math.Abs(mid - 456) < 0.01, "1400px viewport uses three wider cards");
Check(DeviceCardWrap.Resolve(700, 380, 640, 16, true) is (1, var capped) && Math.Abs(capped - 640) < 0.01, "a pane too narrow for two cards caps one card at the max width");
Check(DeviceCardWrap.Resolve(560, 380, 640, 16, true) is (1, var narrow) && Math.Abs(narrow - 560) < 0.01, "a pane under the max uses the available width");
Check(DeviceCardWrap.Resolve(240, 380, 640, 16, true) is (1, var tight) && Math.Abs(tight - 240) < 0.01, "viewport narrower than the minimum still gives the card that width");
Check(DeviceCardWrap.Resolve(double.PositiveInfinity, 380, 640, 16, true).ItemWidth >= 380, "infinite measure uses a finite card width");
Check(DeviceCardWrap.Resolve(980, 380, 640, 16, false).ItemWidth == 980, "list mode uses the full row");

var flags = BindingFlags.Instance | BindingFlags.NonPublic;
var view = new URemoteView();
var type = typeof(URemoteView);
var devicePage = (Control)type.GetField("devicePage", flags)!.GetValue(view)!;
type.GetField("pages", flags)!.SetValue(view, new Control[] { devicePage });
type.GetMethod("ShowPage", flags)!.Invoke(view, new object[] { 0 });
var devices = Enumerable.Range(1, 9).Select(i => new UuDevice(
    "fixture-" + i, "设备 " + i, i % 5 == 0 ? "mobile" : "desktop", 1,
    i % 3 == 0 ? "DISCONNECTED" : "CONNECTED", true, true, "4.41.0", false)).ToArray();
type.GetField("devices", flags)!.SetValue(view, devices);
type.GetMethod("RenderDevices", flags)!.Invoke(view, null);

var window = new Window { Width = 1440, Height = 880, Content = view, Background = Brushes.White };
window.RequestedThemeVariant = ThemeVariant.Light;
window.Show();
Layout(window, 1440, 880);

var rows = (DeviceCardWrap)type.GetField("deviceRows", flags)!.GetValue(view)!;
var catalog = (TextBlock)type.GetField("catalogCount", flags)!.GetValue(view)!;
Check(catalog.Text?.Contains("我的设备", StringComparison.Ordinal) == true && catalog.Text.Contains('9'), "catalog heading shows 我的设备 and the count");
var cards = rows.Children.OfType<DeviceCard>().ToArray();
Check(cards.Length == 9, "nine device cards are created");
Check(cards.All(card => card.Width > 100 && card.Bounds.Width > 100 && card.Bounds.Height > 80), "nine cards have a real arranged size");
Check(cards.All(HasChrome), "every card has a surface background and a 1px border");
Check(cards.All(card => card.GetVisualDescendants().OfType<Grid>().Any(grid => grid.Name == "actions" && grid.Bounds.Width > 80)),
    "action row inside each card is measured");
var names = cards.Select(card => card.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).FirstOrDefault(text => text?.StartsWith("设备 ", StringComparison.Ordinal) == true)).ToArray();
Check(names.Distinct().Count() == 9, "each device name is visible on its card");
var wideColumns = RowCount(cards);
var wideWidth = cards[0].Bounds.Width;
var widePanel = rows.Bounds.Width;
Check(wideColumns >= 2 && Math.Abs(RightOf(cards) - widePanel) < 3, "wide window wraps at least two cards and fills the row, panel=" + widePanel.ToString("0") + " card=" + wideWidth.ToString("0"));

Layout(window, 1800, 880);
cards = rows.Children.OfType<DeviceCard>().ToArray();
var broaderColumns = RowCount(cards);
var broaderWidth = cards[0].Bounds.Width;
Check(broaderColumns > wideColumns && Math.Abs(broaderWidth - wideWidth) > 20, "a wider window adds columns instead of keeping one fixed card width");
Check(Math.Abs(RightOf(cards) - rows.Bounds.Width) < 3, "the wider row is filled");

Layout(window, 860, 880);
cards = rows.Children.OfType<DeviceCard>().ToArray();
var narrowColumns = RowCount(cards);
var narrowCard = cards[0].Bounds.Width;
Check(narrowColumns == 1 && Math.Abs(narrowCard - Math.Min(rows.Bounds.Width, rows.MaxItemWidth)) < 3,
    "a narrower window drops to one column sized to the pane, panel=" + rows.Bounds.Width.ToString("0") + " card=" + narrowCard.ToString("0"));
Check(Math.Abs(narrowCard - wideWidth) > 20, "card width changes with the viewport");
Check(cards.All(card => card.Bounds.Width > 40 && HasChrome(card)), "narrow cards stay visible and keep their border");

type.GetField("listView", flags)!.SetValue(view, true);
type.GetMethod("RenderDevices", flags)!.Invoke(view, null);
Layout(window, 1440, 880);
cards = rows.Children.OfType<DeviceCard>().ToArray();
Check(rows.SingleColumn && cards.Length == 9 && RowCount(cards) == 1 && Math.Abs(cards[0].Bounds.Width - rows.Bounds.Width) < 3,
    "list mode stretches each card to the row");
Check(cards.Select(card => card.Bounds.Y).Distinct().Count() == 9, "list mode stacks one card per row");

type.GetField("listView", flags)!.SetValue(view, false);
var search = (TextBox)type.GetField("search", flags)!.GetValue(view)!;
search.Text = "设备 4";
type.GetMethod("RenderDevices", flags)!.Invoke(view, null);
Layout(window, 1440, 880);
Check(rows.Children.OfType<DeviceCard>().Count() == 1, "name search still filters the wrapped cards");
search.Text = "";
type.GetMethod("RenderDevices", flags)!.Invoke(view, null);
type.GetField("devices", flags)!.SetValue(view, Array.Empty<UuDevice>());
type.GetMethod("RenderDevices", flags)!.Invoke(view, null);
Layout(window, 1440, 880);
var message = rows.Children.OfType<CatalogMessageCard>().Single();
Check(message.Bounds.Width > 200 && Math.Abs(message.Bounds.Width - rows.Bounds.Width) < 3, "empty catalog message uses the row width");

var loose = new DeviceCardWrap();
for (var i = 0; i < 9; i++) loose.Children.Add(Card(i));
loose.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
Check(loose.DesiredSize.Width >= 380 && loose.Children.OfType<DeviceCard>().All(card => card.Width >= 380 && card.DesiredSize.Width >= 280),
    "a wrapping panel measured with infinite width does not collapse");

var shot = Path.Combine(Path.GetTempPath(), "uremote-device-cards.png");
var narrowShot = Path.Combine(Path.GetTempPath(), "uremote-device-cards-narrow.png");
type.GetField("devices", flags)!.SetValue(view, devices);
type.GetMethod("RenderDevices", flags)!.Invoke(view, null);
Layout(window, 1440, 2400);
cards = rows.Children.OfType<DeviceCard>().ToArray();
Check(cards.Length == 9 && cards.All(card => card.Bounds.Width > 100 && card.Bounds.Bottom > card.Bounds.Top), "all nine cards stay arranged in the tall device page");
Console.WriteLine($"wide-panel={rows.Bounds.Width:0}x{rows.Bounds.Height:0} card={cards[0].Bounds.Width:0}x{cards[0].Bounds.Height:0} bottom={cards.Max(card => card.Bounds.Bottom):0}");
Save(window, shot);
Layout(window, 860, 2400);
cards = rows.Children.OfType<DeviceCard>().ToArray();
Console.WriteLine($"narrow-panel={rows.Bounds.Width:0} columns={RowCount(cards)} card={cards[0].Bounds.Width:0}");
Save(window, narrowShot);
Check(new FileInfo(shot).Length > 1000 && new FileInfo(narrowShot).Length > 1000, "rendered wide and narrow device pages");
Console.WriteLine("screenshot=" + shot);
Console.WriteLine("narrow=" + narrowShot);

var selected = Card(99, selected: true);
var selectedWindow = new Window { Width = 480, Height = 360, Content = selected, Background = Brushes.White };
selectedWindow.Show();
Layout(selectedWindow, 480, 360);
var accent = selected.BorderBrush as ISolidColorBrush;
Check(selected.BorderThickness == new Thickness(1) && accent is not null && accent.Color.R == 0x12 && accent.Color.G == 0x67 && accent.Color.B == 0xE8,
    "selected card keeps a 1px accent border");
selectedWindow.Close();

view.Dispose();
window.Close();
if (failed) throw new Exception("Device card layout checks failed.");
Console.WriteLine("PASS: 9 devices render as adaptive cards under 我的设备");

static void Layout(Window window, double width, double height)
{
    window.Width = width;
    window.Height = height;
    window.Measure(new Size(width, height));
    window.Arrange(new Rect(0, 0, width, height));
    Dispatcher.UIThread.RunJobs();
    window.UpdateLayout();
}

static int RowCount(IReadOnlyList<DeviceCard> cards)
{
    var top = cards.Min(card => card.Bounds.Y);
    return cards.Count(card => Math.Abs(card.Bounds.Y - top) < 1);
}

static double RightOf(IReadOnlyList<DeviceCard> cards)
{
    var top = cards.Min(card => card.Bounds.Y);
    return cards.Where(card => Math.Abs(card.Bounds.Y - top) < 1).Max(card => card.Bounds.Right);
}

static bool HasChrome(DeviceCard card)
{
    var ok = card.BorderThickness == new Thickness(1) && card.CornerRadius == new CornerRadius(12) && card.Background is not null && card.BorderBrush is not null;
    if (!ok) Console.WriteLine($"chrome thickness={card.BorderThickness} radius={card.CornerRadius} bg={card.Background} brush={card.BorderBrush}");
    return ok;
}

static DeviceCard Card(int index, bool selected = false) => new()
{
    DataContext = new DeviceCardModel
    {
        Name = "设备 " + index,
        IsSelected = selected,
        StatusText = "●  在线",
        PlatformName = "Windows",
        BadgeText = "在线",
        FavoriteLabel = "收藏设备",
        TransferTip = "文件",
        TerminalTip = "终端",
        TransferName = "文件传输",
        TerminalName = "终端",
        PortName = "端口映射",
        WakeName = "远程开机",
        Online = true,
        PreviewHeight = 120
    }
};

static void Save(Window window, string path)
{
    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
    using var frame = window.CaptureRenderedFrame() ?? throw new Exception(
        "headless frame was not rendered; client=" + window.ClientSize + " bounds=" + window.Bounds);
    frame.Save(path, PngBitmapEncoderOptions.Default);
    var width = frame.PixelSize.Width;
    var height = frame.PixelSize.Height;
    var buffer = new byte[width * height * 4];
    var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
    try { frame.CopyPixels(new PixelRect(0, 0, width, height), pinned.AddrOfPinnedObject(), buffer.Length, width * 4); }
    finally { pinned.Free(); }
    var white = 0;
    var border = 0;
    for (var i = 0; i + 3 < buffer.Length; i += 4)
    {
        var b = buffer[i];
        var g = buffer[i + 1];
        var r = buffer[i + 2];
        if (r > 248 && g > 248 && b > 248) white++;
        if (Math.Abs(r - 226) < 8 && Math.Abs(g - 231) < 8 && Math.Abs(b - 239) < 8) border++;
    }
    if (white < 1000 || border < 100)
        throw new Exception($"rendered frame has white={white} border={border}; card chrome is not visible");
}

public sealed class LayoutApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new Semi.Avalonia.SemiTheme());
        Styles.Add(new global::Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://AsterDock.UI/"))
        {
            Source = new Uri("avares://AsterDock.UI/Themes/AsterDockTheme.axaml")
        });
        RequestedThemeVariant = ThemeVariant.Light;
    }
}
