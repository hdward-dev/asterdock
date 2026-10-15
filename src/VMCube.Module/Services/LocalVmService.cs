using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace VMCube.Module.Services;

public sealed record LocalVmDefinition(string Id, string Name, int Cpus, int MemoryMiB, int DiskGiB, string? IsoPath, ConsoleProtocol Protocol, LocalVmImport? Imported = null);
public sealed record LocalVmRuntime(int Pid, long StartedUtcTicks, int QmpPort, string? QmpSocket, int ConsolePort, int? TpmPid = null, long? TpmStartedUtcTicks = null, string? ProcessStartIdentity = null);

/// <summary>Local, per-user QEMU workspace. Uses QMP rather than libvirt/virsh.
/// Guests survive closing the UI; runtime identity is verified before reattaching.</summary>
public sealed class LocalVmService(string dataDirectory) : IDisposable
{
    private readonly string _root = Path.Combine(dataDirectory, "local");
    public string? EngineDirectory { get; set; }
    private string DefinitionsPath => Path.Combine(_root, "machines.json");
    public LocalVmDefinition GetDefinition(string id) => Find(id);
    private List<LocalVmDefinition> Load()
    {
        if (!File.Exists(DefinitionsPath)) return [];
        using var data = JsonDocument.Parse(File.ReadAllText(DefinitionsPath));
        if (data.RootElement.ValueKind == JsonValueKind.Array) return data.RootElement.Deserialize<List<LocalVmDefinition>>() ?? [];
        if (data.RootElement.GetProperty("FormatVersion").GetInt32() != 2) throw new InvalidOperationException("虚拟机配置版本不兼容，请更新 VMCUBE。");
        return data.RootElement.GetProperty("Machines").Deserialize<List<LocalVmDefinition>>() ?? [];
    }
    public async Task MigrateRegistryAsync(CancellationToken token)
    {
        using var workspaceLock = await LockAsync(token);
        if (!File.Exists(DefinitionsPath)) return;
        using var data = JsonDocument.Parse(await File.ReadAllTextAsync(DefinitionsPath, token));
        if (data.RootElement.ValueKind == JsonValueKind.Array) await SaveAsync(Load(), token);
    }
    private string VmDirectory(string id)
    {
        if (!Guid.TryParse(id, out _)) throw new ArgumentException("无效的本机虚拟机 ID");
        return Path.Combine(_root, id);
    }
    private async Task SaveAsync(List<LocalVmDefinition> definitions, CancellationToken token)
    {
        Directory.CreateDirectory(_root);
        var temp = DefinitionsPath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new { FormatVersion = 2, Machines = definitions }), token);
        File.Move(temp, DefinitionsPath, overwrite: true);
    }
    private async Task<FileStream> LockAsync(CancellationToken token)
    {
        Directory.CreateDirectory(_root);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(_root, "workspace.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(100, deadline.Token); }
        }
    }
    private string VmExecutable(LocalVmDefinition vm) => vm.Imported?.QemuPath ?? ResolveEngine("qemu-system-x86_64");
    private static string? ProcessIdentity(int pid)
    {
        if (!OperatingSystem.IsLinux()) return null;
        // /proc's boot ID + kernel start ticks are stable across .NET processes.
        // Process.StartTime on Linux can differ by fractions of a millisecond between callers.
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim() + ":" + fields[19];
        }
        catch (IOException) { return null; } // The child may have exited before its first status check.
    }
    private string ControlSocketPath(string id, string kind)
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var directory = !string.IsNullOrWhiteSpace(runtime) && Directory.Exists(runtime)
            ? Path.Combine(runtime, "vmcube") : Path.Combine(_root, ".sockets");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var name = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_root + "/" + id)))[..16];
        var path = Path.Combine(directory, name + "." + kind);
        if (System.Text.Encoding.UTF8.GetByteCount(path) > 103) throw new InvalidOperationException("控制 socket 路径过长，请设置可用的 XDG_RUNTIME_DIR 或使用更短的应用数据目录。");
        return path;
    }
    private static async Task RemoveStaleSocketAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return;
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), deadline.Token); }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionRefused or SocketError.AddressNotAvailable)
        { File.Delete(path); return; }
        throw new InvalidOperationException("虚拟机控制通道仍在使用，请刷新或先关闭原虚拟机，避免重复启动。");
    }
    private static void CaptureUnixLog(ProcessStartInfo info, string path)
    {
        if (OperatingSystem.IsWindows()) return;
        var executable = info.FileName; var arguments = info.ArgumentList.ToArray();
        info.FileName = "/bin/sh"; info.ArgumentList.Clear();
        // Fixed shell program; all paths and QEMU options stay in positional arguments/environment.
        foreach (var arg in new[] { "-c", "exec \"$@\" >> \"$VMCUBE_PROCESS_LOG\" 2>&1", "vmcube", executable }.Concat(arguments)) info.ArgumentList.Add(arg);
        info.Environment["VMCUBE_PROCESS_LOG"] = path;
    }
    private static string LogTail(string path)
    {
        if (!File.Exists(path)) return "";
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(Math.Max(0, stream.Length - 4096), SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        return "\n" + reader.ReadToEnd().Trim() + "\n日志：" + path;
    }
    public string ResolveEngine(string command)
    {
        var file = command + (OperatingSystem.IsWindows() ? ".exe" : "");
        var module = Path.GetDirectoryName(typeof(LocalVmService).Assembly.Location)!;
        var directories = EngineSearchDirectories(EngineDirectory, module);
        var executable = directories.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Path.Combine(x!, file)).FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("未在应用内置目录、系统 PATH 或用户配置目录中找到 QEMU。若已安装，请选择同时包含 qemu-system-x86_64 与 qemu-img 的目录；无需重复安装或安装 virsh。");
        executable = Path.GetFullPath(executable);
        SpiceDesktopSession.PrepareExecutable(executable); return executable;
    }
    public static IReadOnlyList<string> EngineSearchDirectories(string? configured, string moduleDirectory)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[] { configured, Path.Combine(moduleDirectory, "native", RuntimeInformation.RuntimeIdentifier, "qemu"),
            Path.Combine(moduleDirectory, "native", RuntimeInformation.RuntimeIdentifier),
            "/run/current-system/sw/bin", Path.Combine("/etc/profiles/per-user", Environment.UserName, "bin"),
            Path.Combine(home, ".nix-profile", "bin"), Path.Combine(home, ".local", "state", "nix", "profiles", "profile", "bin"),
            "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "qemu") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
    }
    public Task<bool> SupportsSpiceAsync(CancellationToken token) => SupportsSpiceAsync(ResolveEngine("qemu-system-x86_64"), token);
    private static async Task<bool> SupportsSpiceAsync(string executable, CancellationToken token)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        var modules = Path.Combine(Path.GetDirectoryName(info.FileName)!, "modules");
        if (Directory.Exists(modules)) info.Environment["QEMU_MODULE_DIR"] = modules;
        info.ArgumentList.Add("-spice"); info.ArgumentList.Add("help");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法检查 QEMU 引擎。");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); await Task.WhenAll(output, error); throw; }
        var text = await output + await error;
        return text.Contains("password-secret", StringComparison.Ordinal) && text.Contains("port=", StringComparison.Ordinal);
    }
    public async Task<LocalVmDefinition> CreateAsync(string name, int cpus, int memoryMiB, int diskGiB, string? iso, ConsoleProtocol protocol, CancellationToken token)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl)) throw new ArgumentException("请输入 1–80 字符的虚拟机名称。");
        if (cpus is < 1 or > 64 || memoryMiB is < 256 or > 262144 || diskGiB is < 1 or > 2048 || !Enum.IsDefined(protocol)) throw new ArgumentException("CPU、内存、磁盘或显示协议配置无效。");
        if (!string.IsNullOrWhiteSpace(iso) && !File.Exists(iso)) throw new ArgumentException("安装 ISO 不存在。");
        var imageTool = ResolveEngine("qemu-img"); _ = ResolveEngine("qemu-system-x86_64");
        var definition = new LocalVmDefinition(Guid.NewGuid().ToString(), name, cpus, memoryMiB, diskGiB, string.IsNullOrWhiteSpace(iso) ? null : Path.GetFullPath(iso), protocol);
        var directory = VmDirectory(definition.Id); Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var info = new ProcessStartInfo(imageTool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "create", "-f", "qcow2", Path.Combine(directory, "disk.qcow2"), $"{diskGiB}G" }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 qemu-img。");
            var error = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
            try { await process.WaitForExitAsync(token); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); await Task.WhenAll(error, output); throw; }
            await output;
            if (process.ExitCode != 0) throw new InvalidOperationException("创建虚拟磁盘失败：" + await error);
            using var workspaceLock = await LockAsync(token);
            var definitions = Load(); definitions.Add(definition); await SaveAsync(definitions, token); return definition;
        }
        catch { Directory.Delete(directory, recursive: true); throw; }
    }
    public async Task<LocalVmDefinition> ImportAsync(LocalVmImport profile, ConsoleProtocol protocol, CancellationToken token)
    {
        profile = profile.Validate();
        if (!Enum.IsDefined(protocol)) throw new ArgumentException("显示协议无效。");
        using var workspaceLock = await LockAsync(token);
        var definitions = Load();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (definitions.Any(x => string.Equals(CanonicalDisk(x), CanonicalPath(profile.DiskPath), comparison)))
            throw new InvalidOperationException("此磁盘已经导入，请在虚拟机列表中打开。");
        var size = await InspectDiskAsync(profile, token);
        var vm = new LocalVmDefinition(Guid.NewGuid().ToString(), profile.Name, profile.Cpus, profile.MemoryMiB,
            checked((int)Math.Ceiling(size / 1073741824d)), null, protocol, profile);
        var directory = VmDirectory(vm.Id); Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try { definitions.Add(vm); await SaveAsync(definitions, token); return vm; }
        catch { Directory.Delete(directory, recursive: true); throw; }
    }
    private static string CanonicalPath(string path) => (File.Exists(path) ? new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName : null) ?? Path.GetFullPath(path);
    private string CanonicalDisk(LocalVmDefinition vm) => CanonicalPath(vm.Imported?.DiskPath ?? Path.Combine(VmDirectory(vm.Id), "disk.qcow2"));
    private async Task<long> InspectDiskAsync(LocalVmImport profile, CancellationToken token)
    {
        var tool = profile.QemuPath is null ? ResolveEngine("qemu-img") : Path.Combine(Path.GetDirectoryName(profile.QemuPath)!, "qemu-img" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        if (!File.Exists(tool)) throw new InvalidOperationException("原有 QEMU 目录中缺少 qemu-img，请选择完整引擎目录。");
        var info = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "info", "--output=json", "--", profile.DiskPath }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("无法检查现有虚拟磁盘。");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(10), token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); await Task.WhenAll(output, error); throw; }
        var text = await output; var details = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException("无法打开现有磁盘，请先关闭其他工具启动的虚拟机：" + details);
        using var disk = JsonDocument.Parse(text);
        if (disk.RootElement.GetProperty("format").GetString() != "qcow2") throw new NotSupportedException("目前仅支持导入 qcow2 虚拟磁盘。");
        return disk.RootElement.GetProperty("virtual-size").GetInt64();
    }
    private LocalVmDefinition Find(string id) => Load().FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("本机虚拟机不存在。");
    public async Task<List<VirtualMachine>> ReadAsync(CancellationToken token)
    {
        var result = new List<VirtualMachine>();
        foreach (var vm in Load())
        {
            var state = "shut off";
            var runtime = ReadRuntime(vm.Id);
            if (runtime is not null)
            {
                using var response = await QmpAsync(vm.Id, runtime, "query-status", token: token);
                var status = response.RootElement.GetProperty("return").GetProperty("status").GetString();
                state = status switch { "running" => "running", "paused" or "prelaunch" => "paused", "shutdown" => "in shutdown", _ => status ?? "unknown" };
            }
            result.Add(new(vm.Id, vm.Name, state, vm.Cpus, vm.MemoryMiB / 1024d));
        }
        return result;
    }
    private LocalVmRuntime? ReadRuntime(string id)
    {
        var path = Path.Combine(VmDirectory(id), "runtime.json");
        if (!File.Exists(path)) return null;
        var runtime = JsonSerializer.Deserialize<LocalVmRuntime>(File.ReadAllText(path));
        if (runtime is null) return null;
        try
        {
            using var process = Process.GetProcessById(runtime.Pid);
            if (process.HasExited) return null;
            var matches = runtime.ProcessStartIdentity is { } identity ? ProcessIdentity(runtime.Pid) == identity :
                Math.Abs(process.StartTime.ToUniversalTime().Ticks - runtime.StartedUtcTicks) <= (OperatingSystem.IsLinux() ? TimeSpan.TicksPerSecond : 0);
            return matches ? runtime : null;
        }
        catch (ArgumentException) { return null; }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    public async Task StartAsync(string id, CancellationToken token)
    {
        using var workspaceLock = await LockAsync(token);
        var vm = Find(id);
        if (ReadRuntime(id) is not null) throw new InvalidOperationException("此虚拟机已经运行，请刷新状态。");
        if (vm.Imported is { } imported) { imported.Validate(); await InspectDiskAsync(imported, token); }
        var executable = VmExecutable(vm);
        if (vm.Protocol == ConsoleProtocol.Spice)
        {
            if (!await SupportsSpiceAsync(executable, token)) throw new NotSupportedException("此 QEMU 引擎未包含 SPICE 服务端，请使用支持 SPICE 的引擎或创建 VNC 虚拟机。远程 SPICE 客户端不受影响。");
        }
        var directory = VmDirectory(id);
        var passwordFile = Path.Combine(directory, "console.secret");
        int displayPort, qmpPort;
        using (var display = new TcpListener(IPAddress.Loopback, 0))
        using (var qmp = new TcpListener(IPAddress.Loopback, 0))
        { display.Start(); qmp.Start(); displayPort = ((IPEndPoint)display.LocalEndpoint).Port; qmpPort = ((IPEndPoint)qmp.LocalEndpoint).Port; }
        var qmpSocket = OperatingSystem.IsWindows() ? null : ControlSocketPath(id, "qmp");
        if (qmpSocket is not null) await RemoveStaleSocketAsync(qmpSocket, token);
        await File.WriteAllTextAsync(passwordFile, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), token);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(passwordFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        // QEMU owns no redirected .NET streams/tasks, so a running guest cannot pin an unloaded module.
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
        var modules = Path.Combine(Path.GetDirectoryName(executable)!, "modules");
        if (Directory.Exists(modules)) info.Environment["QEMU_MODULE_DIR"] = modules;
        var acceleration = vm.Imported?.RequireKvm == true ? "kvm" : RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? OperatingSystem.IsLinux() ? "kvm:tcg" : OperatingSystem.IsMacOS() ? "hvf:tcg" : "whpx:tcg" : "tcg";
        var profile = vm.Imported;
        foreach (var arg in new[] { "-name", "vmcube-" + id, "-machine", "q35,accel=" + acceleration + (profile?.SecureBoot == true ? ",smm=on" : ""), "-m", vm.MemoryMiB.ToString(), "-smp", vm.Cpus.ToString(),
            "-display", "none", "-vga", profile?.VideoModel ?? (vm.Protocol == ConsoleProtocol.Spice ? "qxl" : "std"),
            "-drive", "if=none,id=os,format=qcow2,file=" + EscapeComma(profile?.DiskPath ?? Path.Combine(directory, "disk.qcow2")),
            "-device", "ide-hd,drive=os,bus=ide.0" + (profile is null ? "" : ",bootindex=1"),
            "-nic", "user,model=" + (profile?.NetworkModel ?? "e1000") + (profile?.MacAddress is { } mac ? ",mac=" + mac : "") +
                (profile?.UserNicHostForwards is { Length: > 0 } forwards ? string.Concat(forwards.Select(forward => ",hostfwd=" + forward)) : ""),
            "-device", "qemu-xhci", "-device", "usb-tablet", "-monitor", "none", "-serial", "none", "-qmp",
            qmpSocket is null ? $"tcp:127.0.0.1:{qmpPort},server=on,wait=off" : "unix:" + EscapeComma(qmpSocket) + ",server=on,wait=off",
            "-object", "secret,id=console,file=" + EscapeComma(passwordFile) }) info.ArgumentList.Add(arg);
        if (profile?.CpuHost == true) { info.ArgumentList.Add("-cpu"); info.ArgumentList.Add("host"); }
        if (profile?.UseLocalTime == true) { info.ArgumentList.Add("-rtc"); info.ArgumentList.Add("base=localtime,clock=host"); }
        if (profile?.FirmwareCodePath is { } code)
        {
            info.ArgumentList.Add("-drive"); info.ArgumentList.Add("if=pflash,format=raw,readonly=on,file=" + EscapeComma(code));
            info.ArgumentList.Add("-drive"); info.ArgumentList.Add("if=pflash,format=raw,file=" + EscapeComma(profile.NvramPath!));
            if (profile.SecureBoot) { info.ArgumentList.Add("-global"); info.ArgumentList.Add("driver=cfi.pflash01,property=secure,value=on"); }
        }
        if (vm.Protocol == ConsoleProtocol.Spice) { info.ArgumentList.Add("-spice"); info.ArgumentList.Add($"addr=127.0.0.1,port={displayPort},password-secret=console,gl=off"); }
        else { info.ArgumentList.Add("-vnc"); info.ArgumentList.Add($"127.0.0.1:{displayPort - 5900},password-secret=console"); }
        if (vm.IsoPath is not null)
        {
            if (!File.Exists(vm.IsoPath)) throw new InvalidOperationException("安装 ISO 已移动，请重新挂载安装镜像。");
            info.ArgumentList.Add("-cdrom"); info.ArgumentList.Add(vm.IsoPath); info.ArgumentList.Add("-boot"); info.ArgumentList.Add("once=d,menu=on");
        }
        var firmware = Path.Combine(Path.GetDirectoryName(executable)!, "share", "qemu");
        if (Directory.Exists(firmware)) { info.ArgumentList.Add("-L"); info.ArgumentList.Add(firmware); }
        var qemuLog = Path.Combine(directory, "qemu.log");
        await File.WriteAllTextAsync(qemuLog, "", token);
        Process? tpm = null; Process? process = null; var started = false;
        try
        {
            if (profile?.TpmStateDirectory is not null)
            {
                var tpmSocket = ControlSocketPath(id, "tpm");
                await RemoveStaleSocketAsync(tpmSocket, token);
                var tpmInfo = new ProcessStartInfo(profile.SwtpmPath!) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
                foreach (var arg in new[] { "socket", "--tpm2", "--tpmstate", "dir=" + profile.TpmStateDirectory + ",lock", "--ctrl",
                    "type=unixio,path=" + tpmSocket + ",mode=0600,terminate", "--flags", "not-need-init" }) tpmInfo.ArgumentList.Add(arg);
                var tpmLog = Path.Combine(directory, "swtpm.log"); await File.WriteAllTextAsync(tpmLog, "", token);
                CaptureUnixLog(tpmInfo, tpmLog);
                tpm = Process.Start(tpmInfo) ?? throw new IOException("无法启动 swtpm。");
                using var tpmDeadline = CancellationTokenSource.CreateLinkedTokenSource(token); tpmDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                while (!File.Exists(tpmSocket))
                {
                    if (tpm.HasExited) throw new InvalidOperationException("TPM 启动失败，请确认原虚拟机已关闭、原有状态文件可用且未被占用。" + LogTail(tpmLog));
                    await Task.Delay(50, tpmDeadline.Token);
                }
                foreach (var arg in new[] { "-chardev", "socket,id=chrtpm,path=" + EscapeComma(tpmSocket), "-tpmdev", "emulator,id=tpm0,chardev=chrtpm", "-device", "tpm-tis,tpmdev=tpm0" }) info.ArgumentList.Add(arg);
            }
            if (profile?.GuestAgentSocket is { } guestAgent)
                foreach (var arg in new[] { "-device", "virtio-serial-pci", "-chardev", "socket,path=" + EscapeComma(guestAgent) + ",server=on,wait=off,id=qga0",
                    "-device", "virtserialport,chardev=qga0,name=org.qemu.guest_agent.0" }) info.ArgumentList.Add(arg);
            CaptureUnixLog(info, qemuLog);
            process = Process.Start(info) ?? throw new InvalidOperationException("QEMU 启动失败。");
            var runtime = new LocalVmRuntime(process.Id, process.StartTime.ToUniversalTime().Ticks, qmpPort, qmpSocket, displayPort, tpm?.Id, tpm?.StartTime.ToUniversalTime().Ticks, ProcessIdentity(process.Id));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
            while (true)
            {
                if (process.HasExited) throw new InvalidOperationException($"QEMU 启动失败（退出代码 {process.ExitCode}）。" + LogTail(qemuLog));
                deadline.Token.ThrowIfCancellationRequested();
                try { using var response = await QmpAsync(id, runtime, "query-status", token: deadline.Token); break; }
                catch (SocketException) { await Task.Delay(100, deadline.Token); }
            }
            if (!OperatingSystem.IsWindows() && qmpSocket is not null && File.Exists(qmpSocket)) File.SetUnixFileMode(qmpSocket, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var runtimePath = Path.Combine(directory, "runtime.json");
            await File.WriteAllTextAsync(runtimePath + ".tmp", JsonSerializer.Serialize(runtime), token);
            File.Move(runtimePath + ".tmp", runtimePath, overwrite: true);
            started = true;
        }
        finally
        {
            if (!started)
            {
                if (process is not null && !process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
                if (tpm is not null && !tpm.HasExited) { tpm.Kill(); await tpm.WaitForExitAsync(CancellationToken.None); }
            }
            process?.Dispose(); tpm?.Dispose();
        }
    }
    public async Task ActAsync(string id, string action, CancellationToken token)
    {
        if (action == "start") { await StartAsync(id, token); return; }
        var command = action switch { "shutdown" => "system_powerdown", "suspend" => "stop", "resume" => "cont", "destroy" => "quit", _ => throw new ArgumentException("不支持的本机虚拟机操作") };
        var runtime = ReadRuntime(id) ?? throw new InvalidOperationException("本机虚拟机尚未运行。");
        using var response = await QmpAsync(id, runtime, command, token: token);
        if (action == "destroy")
        {
            try { using var process = Process.GetProcessById(runtime.Pid); await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token); }
            catch (ArgumentException) { }
        }
    }
    public async Task<(ConsoleEndpoint Endpoint, string Password)> GetConsoleAsync(string id, CancellationToken token)
    {
        var vm = Find(id); var runtime = ReadRuntime(id) ?? throw new InvalidOperationException("请先启动虚拟机。");
        using var response = await QmpAsync(id, runtime, "query-status", token: token);
        return (new("127.0.0.1", runtime.ConsolePort, vm.Protocol), await File.ReadAllTextAsync(Path.Combine(VmDirectory(id), "console.secret"), token));
    }
    public async Task DetachIsoAsync(string id, CancellationToken token)
    {
        using var workspaceLock = await LockAsync(token);
        if (ReadRuntime(id) is not null) throw new InvalidOperationException("请先正常关闭虚拟机，再移除安装镜像。");
        var definitions = Load(); var index = definitions.FindIndex(x => x.Id == id);
        if (index < 0) throw new InvalidOperationException("虚拟机不存在。");
        definitions[index] = definitions[index] with { IsoPath = null }; await SaveAsync(definitions, token);
    }
    private static string EscapeComma(string value) => value.Replace(",", ",,", StringComparison.Ordinal);
    private static async Task<JsonDocument> QmpAsync(string id, LocalVmRuntime runtime, string command, object? arguments = null, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var socket = new Socket(runtime.QmpSocket is null ? AddressFamily.InterNetwork : AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        EndPoint endpoint = runtime.QmpSocket is null ? new IPEndPoint(IPAddress.Loopback, runtime.QmpPort) : new UnixDomainSocketEndPoint(runtime.QmpSocket);
        await socket.ConnectAsync(endpoint, deadline.Token);
        using var stream = new NetworkStream(socket, ownsSocket: false);
        using var reader = new StreamReader(stream); using var writer = new StreamWriter(stream) { AutoFlush = true };
        using var greeting = JsonDocument.Parse(await reader.ReadLineAsync(deadline.Token) ?? throw new IOException("QMP 连接已关闭。"));
        if (!greeting.RootElement.TryGetProperty("QMP", out _)) throw new InvalidDataException("无效的 QMP 服务。");
        async Task<JsonDocument> ExecuteAsync(string method, object? values = null)
        {
            var request = JsonSerializer.Serialize(new { execute = method, arguments = values ?? new { }, id = "vmcube" });
            await writer.WriteLineAsync(request.AsMemory(), deadline.Token);
            while (true)
            {
                var line = await reader.ReadLineAsync(deadline.Token) ?? throw new IOException("QMP 连接已关闭。");
                var message = JsonDocument.Parse(line);
                if (message.RootElement.TryGetProperty("error", out var error)) { var description = error.ToString(); message.Dispose(); throw new InvalidOperationException("QMP 操作失败：" + description); }
                if (message.RootElement.TryGetProperty("return", out _)) return message;
                message.Dispose();
            }
        }
        using var capabilities = await ExecuteAsync("qmp_capabilities");
        using var name = await ExecuteAsync("query-name");
        if (name.RootElement.GetProperty("return").GetProperty("name").GetString() != "vmcube-" + id)
            throw new InvalidDataException("QMP 虚拟机身份不匹配，已停止操作。");
        return await ExecuteAsync(command, arguments);
    }
    public void Dispose() { /* No permanent sockets, background readers or process handles. Guests keep running. */ }
}
