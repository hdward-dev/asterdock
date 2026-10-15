using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using URemote.Core;
using VMCube.Module.Services;

internal static class ImportChecks
{
    public static async Task RunAsync(string[] args)
    {
        var qemu = Path.GetFullPath(args[Array.IndexOf(args, "--qemu") + 1]);
        var helper = Path.GetFullPath(args[Array.IndexOf(args, "--spice-helper") + 1]);
        var root = Path.Combine(Path.GetTempPath(), "vmcube-import-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var source = new LocalVmService(Path.Combine(root, "source")) { EngineDirectory = Path.GetDirectoryName(qemu) };
        using var target = new LocalVmService(Path.Combine(root, "target")) { EngineDirectory = Path.GetDirectoryName(qemu) };
        string? id = null;
        int? tpmPid = null;
        try
        {
            var seed = await source.CreateAsync("source", 1, 256, 1, null, ConsoleProtocol.Vnc, timeout.Token);
            var disk = Path.Combine(root, "source", "local", seed.Id, "disk.qcow2");
            var profile = new LocalVmImport("existing guest", 1, 256, disk, qemu);
            if (args.Contains("--uefi-code"))
            {
                var code = Path.GetFullPath(args[Array.IndexOf(args, "--uefi-code") + 1]);
                var vars = Path.GetFullPath(args[Array.IndexOf(args, "--uefi-vars") + 1]);
                var swtpm = Path.GetFullPath(args[Array.IndexOf(args, "--swtpm") + 1]);
                File.Copy(vars, Path.Combine(root, "VARS.fd"));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(root, "VARS.fd"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var state = Path.Combine(root, "tpm"); Directory.CreateDirectory(state);
                // Create test-only TPM state; import itself must never initialize replacement state.
                using (var initialize = Process.Start(new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(swtpm)!, "swtpm_setup")) { UseShellExecute = false,
                    ArgumentList = { "--tpm", swtpm + " socket", "--tpm2", "--tpmstate", state, "--createek", "--not-overwrite" } })!)
                { await initialize.WaitForExitAsync(timeout.Token); if (initialize.ExitCode != 0) throw new Exception("TPM test initialization failed"); }
                profile = profile with { FirmwareCodePath = code, NvramPath = Path.Combine(root, "VARS.fd"),
                    TpmStateDirectory = state, SwtpmPath = swtpm, SecureBoot = true, GuestAgentSocket = Path.Combine(root, "qga.sock") };
            }
            var profilePath = Path.Combine(root, "guest.vmcube.json");
            await File.WriteAllTextAsync(profilePath, JsonSerializer.Serialize(profile with { DiskPath = Path.GetRelativePath(root, disk) }));
            var parsed = await LocalVmImport.ReadProfileAsync(profilePath, timeout.Token);
            if (parsed.DiskPath != disk) throw new Exception("Relative disk path did not resolve against profile");
            await File.WriteAllTextAsync(Path.Combine(root, "invalid.vmcube.json"), "{\"Name\":\"x\",\"ExtraArguments\":\"-snapshot\"}");
            try { await LocalVmImport.ReadProfileAsync(Path.Combine(root, "invalid.vmcube.json"), timeout.Token); throw new Exception("Unknown launch settings accepted"); }
            catch (JsonException) { }
            try { (profile with { FirmwareCodePath = null, NvramPath = disk }).Validate(); throw new Exception("Partial UEFI state accepted"); }
            catch (ArgumentException) { }
            var before = SHA256.HashData(await File.ReadAllBytesAsync(disk));
            var vm = await target.ImportAsync(parsed, ConsoleProtocol.Spice, timeout.Token); id = vm.Id;
            var after = SHA256.HashData(await File.ReadAllBytesAsync(disk));
            if (!before.SequenceEqual(after)) throw new Exception("Import modified the source disk");
            if (File.Exists(Path.Combine(root, "target", "local", id, "disk.qcow2"))) throw new Exception("Import created a replacement disk");
            if (target.GetDefinition(id).Imported != profile) throw new Exception("Imported hardware settings changed");
            var registryPath = Path.Combine(root, "target", "local", "machines.json");
            using (var data = JsonDocument.Parse(await File.ReadAllTextAsync(registryPath)))
                await File.WriteAllTextAsync(registryPath, data.RootElement.GetProperty("Machines").GetRawText());
            await target.MigrateRegistryAsync(timeout.Token);
            var migrated = await File.ReadAllTextAsync(registryPath);
            if (target.GetDefinition(id).Imported != profile) throw new Exception("Registry migration lost external hardware settings");
            try { JsonSerializer.Deserialize<List<LocalVmDefinition>>(migrated); throw new Exception("Old modules can misinterpret the new registry"); }
            catch (JsonException) { }
            try { await target.ImportAsync(parsed, ConsoleProtocol.Spice, timeout.Token); throw new Exception("Duplicate import accepted"); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("已经导入")) { }
            await target.StartAsync(id, timeout.Token);
            if (profile.GuestAgentSocket is { } agent && !File.Exists(agent)) throw new Exception("Original guest-agent channel missing");
            // Independent workspace must also respect QEMU's disk lock.
            using var competing = new LocalVmService(Path.Combine(root, "competing"));
            try { await competing.ImportAsync(profile, ConsoleProtocol.Spice, timeout.Token); throw new Exception("Running disk accepted"); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("先关闭")) { }
            var runtimePath = Path.Combine(root, "target", "local", id, "runtime.json");
            var runtime = JsonSerializer.Deserialize<LocalVmRuntime>(await File.ReadAllTextAsync(runtimePath))!;
            tpmPid = runtime.TpmPid;
            var childInfo = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) childInfo.ArgumentList.Add(typeof(ImportChecks).Assembly.Location);
            childInfo.ArgumentList.Add("--verify-local"); childInfo.ArgumentList.Add(Path.Combine(root, "target"));
            using (var child = Process.Start(childInfo)!)
            {
                var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
                await child.WaitForExitAsync(timeout.Token);
                if (child.ExitCode != 0) throw new Exception("New process could not reattach: " + await stdout + await stderr);
                Console.Write(await stdout);
            }
            try { await target.StartAsync(id, timeout.Token); throw new Exception("Duplicate start accepted"); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("已经运行")) { }
            var connection = await target.GetConsoleAsync(id, timeout.Token);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var desktop = new SpiceDesktopSession(helperPath: helper);
            var frame = new TaskCompletionSource<LocalDesktopFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            desktop.Frame += f => frame.TrySetResult(f);
            var running = desktop.RunAsync(new(LocalDesktopProtocol.Vnc, connection.Endpoint.Host, connection.Endpoint.Port, Password: connection.Password), stop.Token);
            try
            {
                if (await Task.WhenAny(frame.Task, running) == running) { await running; throw new Exception("Imported desktop ended before a frame"); }
                await frame.Task.WaitAsync(timeout.Token);
            }
            finally { stop.Cancel(); try { await running; } catch (OperationCanceledException) { } }
            target.Dispose();
            using var reattached = new LocalVmService(Path.Combine(root, "target")) { EngineDirectory = Path.GetDirectoryName(qemu) };
            if ((await reattached.ReadAsync(timeout.Token)).Single().State != "running") throw new Exception("Imported guest did not survive reload");
            await reattached.ActAsync(id, "destroy", timeout.Token);
            await CheckTpmExitAsync(tpmPid);
            await reattached.StartAsync(id, timeout.Token);
            var restarted = JsonSerializer.Deserialize<LocalVmRuntime>(await File.ReadAllTextAsync(runtimePath))!;
            await reattached.ActAsync(id, "destroy", timeout.Token);
            await CheckTpmExitAsync(restarted.TpmPid);
            // Failure must report the actual QEMU diagnostic without leaving a TPM running.
            var missingDisk = await reattached.CreateAsync("missing disk", 1, 256, 1, null, ConsoleProtocol.Vnc, timeout.Token);
            File.Delete(Path.Combine(root, "target", "local", missingDisk.Id, "disk.qcow2"));
            try { await reattached.StartAsync(missingDisk.Id, timeout.Token); throw new Exception("Missing disk unexpectedly started"); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("qemu.log") && ex.Message.Contains("disk.qcow2")) { }
            Console.WriteLine("Import checks passed: relative profile, validation, source disk unchanged, duplicate/live disk rejection, SPICE frame, reload and restart" + (profile.TpmStateDirectory is null ? "." : ", original UEFI/TPM and automatic TPM exit."));
        }
        finally
        {
            if (id is not null) { try { await target.ActAsync(id, "destroy", default); } catch (InvalidOperationException) { } }
            await CheckTpmExitAsync(tpmPid);
            Directory.Delete(root, recursive: true);
        }
    }
    private static async Task CheckTpmExitAsync(int? pid)
    {
        if (pid is null) return;
        try
        {
            using var p = Process.GetProcessById(pid.Value);
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (ArgumentException) { }
    }
}
