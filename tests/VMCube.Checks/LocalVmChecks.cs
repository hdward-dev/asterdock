using System.Diagnostics;
using URemote.Core;
using VMCube.Module.Services;
using System.Text.Json;
internal static class LocalVmChecks
{
    public static async Task RunAsync(string[] args)
    {
        var qemu = args[Array.IndexOf(args, "--qemu") + 1];
        var helper = args[Array.IndexOf(args, "--spice-helper") + 1];
        foreach (var protocol in new[] { ConsoleProtocol.Vnc, ConsoleProtocol.Spice })
        {
        var root = Path.Combine(Path.GetTempPath(), "vmcube-local-checks-" + Guid.NewGuid());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var local = new LocalVmService(root) { EngineDirectory = Path.GetDirectoryName(qemu) };
        string? id = null;
        try
        {
            var vm = await local.CreateAsync("local-test", 1, 256, 1, null, protocol, timeout.Token); id = vm.Id;
            if ((await local.ReadAsync(timeout.Token)).Single().State != "shut off") throw new Exception("Local VM not registered");
            await local.StartAsync(id, timeout.Token);
            if ((await local.ReadAsync(timeout.Token)).Single().State != "running") throw new Exception("Local QEMU not running");
            if (!OperatingSystem.IsWindows())
            {
                var vmDirectory = Path.Combine(root, "local", id);
                var runtimePath = Path.Combine(vmDirectory, "runtime.json");
                var original = await File.ReadAllTextAsync(runtimePath);
                var runtime = JsonSerializer.Deserialize<LocalVmRuntime>(original)!;
                var secret = await File.ReadAllTextAsync(Path.Combine(vmDirectory, "console.secret"));
                await File.WriteAllTextAsync(runtimePath, JsonSerializer.Serialize(runtime with { StartedUtcTicks = 0, ProcessStartIdentity = "stale" }));
                try
                {
                    try { await local.StartAsync(id, timeout.Token); throw new Exception("Active control socket was replaced"); }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("控制通道仍在使用")) { }
                    if (await File.ReadAllTextAsync(Path.Combine(vmDirectory, "console.secret")) != secret) throw new Exception("Rejected start replaced running guest credentials");
                }
                finally { await File.WriteAllTextAsync(runtimePath, original); }
            }
            await local.ActAsync(id, "suspend", timeout.Token);
            if ((await local.ReadAsync(timeout.Token)).Single().State != "paused") throw new Exception("QMP pause failed");
            await local.ActAsync(id, "resume", timeout.Token);
            var connection = await local.GetConsoleAsync(id, timeout.Token);
            var frame = new TaskCompletionSource<LocalDesktopFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            ILocalDesktopSession desktop = protocol == ConsoleProtocol.Spice ? new SpiceDesktopSession(helperPath: helper) : new VncDesktopSession(); desktop.Frame += value => frame.TrySetResult(value);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var running = desktop.RunAsync(new(LocalDesktopProtocol.Vnc, connection.Endpoint.Host, connection.Endpoint.Port, Password: connection.Password), stop.Token);
            if (await Task.WhenAny(frame.Task, running) == running) { await running; throw new Exception("Local desktop ended before frame"); }
            await frame.Task.WaitAsync(timeout.Token); stop.Cancel();
            try { await running; } catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or System.Net.Sockets.SocketException) { }
            local.Dispose();
            using var reattached = new LocalVmService(root);
            if ((await reattached.ReadAsync(timeout.Token)).Single().State != "running") throw new Exception("Local guest did not survive service reload");
            await reattached.ActAsync(id, "destroy", timeout.Token);
            if ((await reattached.ReadAsync(timeout.Token)).Single().State != "shut off") throw new Exception("QMP shutdown failed");
            await reattached.DetachIsoAsync(id, timeout.Token);
            Console.WriteLine($"Local QEMU passed: {protocol}, qcow2 creation, start, pause/resume, authenticated desktop, reattach and confirmed power-off.");
        }
        finally
        {
            if (id is not null)
            {
                try { await local.ActAsync(id, "destroy", CancellationToken.None); } catch (InvalidOperationException) { }
                var socket = Path.Combine(Path.GetTempPath(), "vmcube-" + id + ".sock"); if (File.Exists(socket)) File.Delete(socket);
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        }
    }
}
