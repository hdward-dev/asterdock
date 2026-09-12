using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using AndroidScreen.Module.Services;
using AndroidScreen.Module.Views;
using AndroidScreen.Module.ViewModels;
using Avalonia;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void EqualHex(byte[] bytes, string expected) => Check(Convert.ToHexString(bytes).Equals(expected, StringComparison.OrdinalIgnoreCase), "Unexpected wire bytes: " + Convert.ToHexString(bytes));
EqualHex(ScrcpyProtocol.Key(3, 0), "0000000000030000000000000000");
EqualHex(ScrcpyProtocol.Touch(0, 10, 20, 1080, 1920), "0200fffffffffffffffe0000000a0000001404380780ffff0000000000000000");
EqualHex(ScrcpyProtocol.Scroll(10, 20, 1080, 1920, 1, -1), "030000000a00000014043807800800f80000000000");
EqualHex(ScrcpyProtocol.Clipboard("中", true), "0900000000000000000100000003e4b8ad");
var devices = AndroidBridge.ParseDevices("List of devices attached\nusb device product:p model:Pixel_9 device:p\nunauth unauthorized\n192.168.1.2:5555 offline\n* daemon started successfully\n");
Check(devices.Count == 3 && devices[0].Model == "Pixel 9" && !devices[1].IsAvailable && !devices[2].IsAvailable, "Device states");
Check(AndroidScreenView.MapPosition(new Point(500, 300), new Size(1000, 600), new PixelSize(1080, 1920), false) == (540, 960, 1080, 1920), "Portrait center");
Check(AndroidScreenView.MapPosition(new Point(5, 300), new Size(1000, 600), new PixelSize(1080, 1920), false) is null, "Letterbox must not inject");
Check(AndroidScreenView.MapPosition(new Point(-10, -10), new Size(1000, 600), new PixelSize(1080, 1920), true) == (0, 0, 1080, 1920), "Captured drag clamping");
Check(AndroidScreenView.MapPosition(new Point(500, 300), new Size(1000, 600), new PixelSize(1920, 1080), false) == (960, 540, 1920, 1080), "Landscape center");
try { AndroidScreenViewModel.ValidateAddress("127.0.0.1;id"); throw new Exception("Address accepted shell syntax"); } catch (ArgumentException) { }
var invalid = new byte[14]; invalid[0] = (byte)'B'; invalid[1] = (byte)'M'; BinaryPrimitives.WriteInt32LittleEndian(invalid.AsSpan(2), int.MaxValue);
try { await EmbeddedScreenSession.ReadBitmapAsync(new MemoryStream(invalid), default); throw new Exception("Oversize frame accepted"); } catch (InvalidDataException) { }
try { await EmbeddedScreenSession.ReadBitmapAsync(new MemoryStream([1, 2]), default); throw new Exception("Truncated frame accepted"); } catch (EndOfStreamException) { }
Console.WriteLine("PASS protocol vectors, device states, letterbox/rotation coordinates, malformed frames and address validation");

if (args.Contains("--install-smoke"))
{
    var installRoot = Path.Combine(Path.GetTempPath(), "asterdock-core-check-" + Guid.NewGuid().ToString("N"));
    try
    {
        using var installer = new ScrcpyInstallerService(installRoot);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var executable = await installer.InstallAsync(deadline.Token);
        var core = ScreenRuntime.ResolveCore(executable);
        Check(File.Exists(core.Server), "Server missing from release archive");
        var version = await new AndroidBridge(core.Adb).RunAsync(deadline.Token, "version");
        Check(version.Contains("Android Debug Bridge"), "ADB executable cannot run");
        Console.WriteLine("PASS official release download, SHA-256, extraction, scrcpy/ADB executability and server discovery");
    }
    finally { if (Directory.Exists(installRoot)) Directory.Delete(installRoot, recursive: true); }
}
if (!args.Contains("--integration")) return;
if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The simulated ADB integration fixture currently requires Python 3 on macOS/Linux.");
var directory = Path.Combine(Path.GetTempPath(), "asterdock-screen-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"))) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
    var fakeAdb = Path.Combine(directory, "fake-adb.py");
    File.SetUnixFileMode(fakeAdb, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    File.WriteAllText(Path.Combine(directory, "port"), ((IPEndPoint)listener.LocalEndpoint).Port.ToString()); listener.Stop();
    // Discovery must work before the scrcpy core is installed.
    File.Copy(fakeAdb, Path.Combine(directory, "adb"));
    File.SetUnixFileMode(Path.Combine(directory, "adb"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    using (var model = new AndroidScreenViewModel(Path.Combine(directory, "empty-core"), directory))
    {
        await model.RefreshAsync();
        Check(!model.IsCoreAvailable && model.Devices.Count == 1 && model.SelectedDevice?.Model == "Test Phone", "Discovery incorrectly requires installed core");
        var selected = model.SelectedDevice;
        await model.RefreshAsync();
        Check(ReferenceEquals(selected, model.SelectedDevice), "Unchanged scan replaced device selection");
    }
    Console.WriteLine("PASS device discovery before core installation and stable selection on refresh");
    var decoder = ScreenRuntime.ResolveDecoder(AppContext.BaseDirectory);
    await using var session = new EmbeddedScreenSession(new AndroidBridge(fakeAdb), "fixture");
    var landscape = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var portrait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var clipboard = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    session.FrameReceived += bytes =>
    {
        var width = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18));
        var height = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22));
        if (width == 320 && height == 180) landscape.TrySetResult();
        if (width == 180 && height == 320) portrait.TrySetResult();
    };
    session.ClipboardReceived += text => clipboard.TrySetResult(text);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    await session.StartAsync(Path.Combine(directory, "landscape.h264"), decoder, 1600, timeout.Token);
    Check(session.DeviceName == "Test Phone", "Fragmented name handshake");
    await landscape.Task.WaitAsync(timeout.Token);
    Check(session.Send(ScrcpyProtocol.Key(3, 0)) && session.Send(ScrcpyProtocol.Key(3, 1)) && session.Send([8, 0]), "Control queue");
    Check(await clipboard.Task.WaitAsync(timeout.Token) == "来自手机", "Clipboard socket");
    await portrait.Task.WaitAsync(timeout.Token);
    File.WriteAllText(Path.Combine(directory, "disconnect"), "yes");
    try { await session.Completion.WaitAsync(timeout.Token); throw new Exception("Disconnect was not reported"); } catch (IOException) { }
    await session.DisposeAsync();
    Check(File.Exists(Path.Combine(directory, "removed")) && File.Exists(Path.Combine(directory, "cleaned")), "ADB forward/server cleanup");
    var commands = File.ReadAllLines(Path.Combine(directory, "commands"));
    Check(commands.Contains("0000000000030000000000000000") && commands.Contains("0001000000030000000000000000"), "Ordered key down/up");
    Console.WriteLine("PASS real H.264 decode, fragmented handshake, encoder restart on rotation, control/clipboard, disconnect and cleanup");
    File.WriteAllText(Path.Combine(directory, "delay-handshake"), "yes");
    await using var cancelled = new EmbeddedScreenSession(new AndroidBridge(fakeAdb), "fixture");
    var starting = cancelled.StartAsync(Path.Combine(directory, "landscape.h264"), decoder, 1600, timeout.Token);
    await Task.Delay(300);
    await cancelled.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    try { await starting; throw new Exception("Disposal did not cancel startup"); } catch (OperationCanceledException) { }
    var reclaimed = new TcpListener(IPAddress.Loopback, int.Parse(File.ReadAllText(Path.Combine(directory, "port"))));
    reclaimed.Start(); reclaimed.Stop();
    Console.WriteLine("PASS disposal during handshake cancels startup and releases the socket");
}
finally { Directory.Delete(directory, recursive: true); }
