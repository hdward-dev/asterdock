using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using System.Text.RegularExpressions;
using URemote.Core;
using VMCube.Module.Services;

internal static class SpiceChecks
{
    public static async Task RunAsync(string[] args)
    {
        var helper = args[Array.IndexOf(args, "--spice-helper") + 1];
        var qemu = args[Array.IndexOf(args, "--qemu") + 1];
        foreach (var authentication in new[] { false, true })
        {
            var traceFile = Path.Combine(Path.GetTempPath(), "vmcube-spice-input-" + Guid.NewGuid() + ".log");
            int port;
            using (var listener = new TcpListener(IPAddress.Loopback, 0)) { listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; }
            var info = new ProcessStartInfo(qemu) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "-accel", "tcg", "-m", "128", "-display", "none", "-vga", "qxl", "-nic", "none", "-serial", "none", "-monitor", "none", "-device", "qemu-xhci", "-device", "usb-tablet", "-boot", "menu=on", "-spice", $"addr=127.0.0.1,port={port}," + (authentication ? "password-secret=fixture" : "disable-ticketing=on") }) info.ArgumentList.Add(arg);
            info.ArgumentList.Add("-trace"); info.ArgumentList.Add("enable=input_event_*,file=" + traceFile);
            var modules = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(qemu))!, "modules");
            if (Directory.Exists(modules)) info.Environment["QEMU_MODULE_DIR"] = modules;
            var firmware = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(qemu))!, "share", "qemu");
            if (Directory.Exists(firmware)) { info.ArgumentList.Add("-L"); info.ArgumentList.Add(firmware); }
            if (authentication) { info.ArgumentList.Add("-object"); info.ArgumentList.Add("secret,id=fixture,data=fixture-secret"); }
            using var server = Process.Start(info) ?? throw new Exception("QEMU failed to start");
            var errors = server.StandardError.ReadToEndAsync(); var stdout = server.StandardOutput.ReadToEndAsync();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (true)
                {
                    if (server.HasExited) throw new Exception(await errors);
                    using var probe = new TcpClient();
                    try { await probe.ConnectAsync(IPAddress.Loopback, port, timeout.Token); break; }
                    catch (SocketException) { await Task.Delay(100, timeout.Token); }
                }
                if (authentication)
                {
                    var rejected = new SpiceDesktopSession(helperPath: helper);
                    try { await rejected.RunAsync(new(LocalDesktopProtocol.Vnc, "127.0.0.1", port, Password: "wrong-password"), timeout.Token); throw new Exception("SPICE bad password accepted"); }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("认证失败")) { }
                }
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                var session = new SpiceDesktopSession(helperPath: helper);
                var frames = Channel.CreateUnbounded<LocalDesktopFrame>();
                session.Frame += frame => frames.Writer.TryWrite(frame);
                var running = session.RunAsync(new(LocalDesktopProtocol.Vnc, "127.0.0.1", port, Password: "fixture-secret"), stop.Token);
                var firstFrame = frames.Reader.ReadAsync(timeout.Token).AsTask();
                if (await Task.WhenAny(running, firstFrame) == running) { await running; throw new Exception("SPICE ended before first frame"); }
                var first = await firstFrame;
                if (first.Width < 320 || first.Height < 200 || first.Pixels.Length != first.Width * first.Height * 4) throw new Exception("SPICE invalid QEMU frame");
                for (var i = 3; i < first.Pixels.Length; i += 4) if (first.Pixels[i] != 255) throw new Exception("SPICE frame alpha invalid");
                // No guest agent: pointer motion must arrive as PS/2 relative events.
                // Absolute positions are what a Windows guest without spice tools drops.
                session.Pointer(10, 10, 0); await Task.Delay(150, timeout.Token);
                var moveStart = File.Exists(traceFile) ? (await File.ReadAllTextAsync(traceFile, timeout.Token)).Length : 0;
                session.Pointer(50, 30, 0);
                string moveTrace;
                MatchCollection axes;
                do
                {
                    await Task.Delay(50, timeout.Token);
                    moveTrace = (await File.ReadAllTextAsync(traceFile, timeout.Token))[moveStart..];
                    axes = Regex.Matches(moveTrace, @"input_event_(rel|abs) con -?\d+, axis (\w+), value (-?\d+)");
                    if (axes.Any(x => x.Groups[1].Value == "abs")) throw new Exception("SPICE sent absolute pointer input without a guest agent: " + moveTrace);
                } while (axes.Count(x => x.Groups[1].Value == "rel") < 2);
                int Axis(string name) => axes.Where(x => x.Groups[1].Value == "rel" && x.Groups[2].Value.Equals(name, StringComparison.OrdinalIgnoreCase)).Sum(x => int.Parse(x.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture));
                if (Axis("x") != 40 || Axis("y") != 20) throw new Exception($"SPICE relative motion at QEMU receiver dx={Axis("x")} dy={Axis("y")}: {moveTrace}");
                var traceStart = moveStart + moveTrace.Length;
                foreach (var mask in new[] { 4, 0, 2, 0, 1, 0 })
                { session.Pointer(100, 100, mask); await Task.Delay(30, timeout.Token); }
                MatchCollection received;
                do
                {
                    await Task.Delay(50, timeout.Token);
                    var trace = await File.ReadAllTextAsync(traceFile, timeout.Token);
                    received = Regex.Matches(trace[traceStart..], @"input_event_btn con -?\d+, button (\w+), down ([01])");
                } while (received.Count < 6);
                var actual = received.Take(6).Select(x => x.Groups[1].Value + ":" + x.Groups[2].Value).ToArray();
                var expected = new[] { "right:1", "right:0", "middle:1", "middle:0", "left:1", "left:0" };
                if (!actual.SequenceEqual(expected)) throw new Exception("SPICE button mapping at QEMU receiver: " + string.Join(", ", actual));
                session.Pointer(100, 100, 1); session.Pointer(120, 100, 0, -1);
                session.Key(0x1d, true); session.Key(0x38, true); session.Key(0x153, true);
                session.Key(0x153, false); session.Key(0x38, false); session.Key(0x1d, false);
                var next = frames.Reader.ReadAsync(timeout.Token).AsTask();
                if (await Task.WhenAny(running, next) == running) { await running; throw new Exception("SPICE ended after input"); }
                await next;
                stop.Cancel();
                try { await running.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
                if (!running.IsCompleted) throw new Exception("SPICE worker failed to stop");
                Console.WriteLine($"SPICE/QEMU passed: auth={authentication}, {first.Width}x{first.Height}, frames, relative mouse without guest agent, QEMU-confirmed right/middle/left press/release, key/pointer input and cancellation.");
            }
            finally
            {
                if (!server.HasExited) server.Kill(entireProcessTree: true);
                await server.WaitForExitAsync(); await Task.WhenAll(errors, stdout);
                if (File.Exists(traceFile)) File.Delete(traceFile);
            }
        }
    }
}
