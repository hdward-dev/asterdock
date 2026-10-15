using VMCube.Module.Services;
using URemote.Core;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Buffers.Binary;

if (args.Length == 2 && args[0] == "--verify-local")
{
    using var child = new LocalVmService(args[1]);
    if ((await child.ReadAsync(default)).Single().State != "running") throw new Exception("Cross-process guest reattach failed");
    Console.WriteLine("Cross-process guest identity and QMP reattach passed.");
    return;
}

var engineSearch = LocalVmService.EngineSearchDirectories(null, "/tmp/vmcube-module");
if (!engineSearch.Contains("/run/current-system/sw/bin") || !engineSearch.Any(x => x.EndsWith(Path.Combine(".nix-profile", "bin"), StringComparison.Ordinal))) throw new Exception("NixOS user/system profile search missing");
var parsed = LibvirtService.ParseInfo("Name: ubuntu:dev\nState: running\nCPU(s): 4\nMax memory: 8388608 KiB\n");
if (parsed["Name"] != "ubuntu:dev" || parsed["CPU(s)"] != "4") throw new Exception("dominfo 解析失败");
var vm = new VirtualMachine(Guid.NewGuid().ToString(), "ubuntu", "paused", 4, 8);
if (vm.StateLabel != "已暂停" || !vm.Summary.Contains("8 GiB")) throw new Exception("状态显示失败");
var service = new LibvirtService();
foreach (var action in new[] { "destroy", "start; touch /tmp/bad", "undefine" })
{
    try { await service.ActAsync("qemu:///system", action, vm.Id, default); throw new Exception("危险操作未拒绝"); }
    catch (ArgumentException) { }
}
try { await service.ActAsync("qemu:///system", "start", "--domain hacked", default); throw new Exception("无效 UUID 未拒绝"); }
catch (ArgumentException) { }
foreach (var uri in new[] { "qemu://remote/system", "https://host/system", "qemu+ssh://host/system?command=bad", "" })
{
    try { await service.ReadAsync(uri, default); throw new Exception("无效 URI 未拒绝"); }
    catch (ArgumentException) { }
}
Console.WriteLine("VMCUBE checks passed: parsing, labels, action allowlist, UUID and URI validation.");

if (!OperatingSystem.IsWindows())
{
    var root = Path.Combine(Path.GetTempPath(), "vmcube-checks-" + Guid.NewGuid());
    Directory.CreateDirectory(root);
    var previousPath = Environment.GetEnvironmentVariable("PATH");
    try
    {
        var executable = Path.Combine(root, "virsh");
        await File.WriteAllTextAsync(executable, """
#!/bin/sh
[ "$1" = "--connect" ] || exit 2
[ "$2" = "qemu:///system" ] || exit 3
case "$3" in
list) echo '12345678-1234-1234-1234-123456789abc';;
dumpxml) echo '<domain><devices><graphics type="vnc" port="5901" listen="127.0.0.1"/></devices></domain>';;
dominfo) printf 'Name: dev:ubuntu\nState: running\nCPU(s): 4\nMax memory: 8388608 KiB\n';;
start) echo 'started';;
shutdown) echo 'guest refused' >&2; exit 1;;
esac
""");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable("PATH", root + Path.PathSeparator + previousPath);
        var machines = await service.ReadAsync("qemu:///system", default);
        if (machines.Count != 1 || machines[0].Name != "dev:ubuntu" || machines[0].MemoryGiB != 8 || machines[0].Cpus != 4)
            throw new Exception("读取虚拟机进程集成失败");
        var console = await service.GetConsoleAsync("qemu:///system", machines[0].Id, default);
        if (console.Host != "127.0.0.1" || console.Port != 5901) throw new Exception("控制台发现失败");
        await service.ActAsync("qemu:///system", "start", machines[0].Id, default);
        try { await service.ActAsync("qemu:///system", "shutdown", machines[0].Id, default); throw new Exception("命令失败未传播"); }
        catch (InvalidOperationException ex) when (ex.Message == "guest refused") { }
        Console.WriteLine("Process integration passed: URI arguments, inventory, action, stderr failure.");
        var ssh = Path.Combine(root, "ssh");
        await File.WriteAllTextAsync(ssh, "#!/bin/sh\necho 'fixture authentication failed' >&2\nexit 1\n");
        File.SetUnixFileMode(ssh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try { await ConsoleTunnel.OpenAsync("qemu+ssh://admin@kvm.example/system", console, default); throw new Exception("SSH 失败未传播"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("fixture authentication failed")) { }
        var pidFile = Path.Combine(root, "ssh.pid");
        Environment.SetEnvironmentVariable("VMCUBE_CHECK_PID", pidFile);
        try
        {
            await File.WriteAllTextAsync(ssh, "#!/bin/sh\necho $$ > \"$VMCUBE_CHECK_PID\"\nexec sleep 60\n");
            using var stopTunnel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try { await ConsoleTunnel.OpenAsync("qemu+ssh://admin@kvm.example/system", console, stopTunnel.Token); throw new Exception("SSH 取消未生效"); }
            catch (OperationCanceledException) { }
            var pid = int.Parse(await File.ReadAllTextAsync(pidFile));
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                if (!process.HasExited) throw new Exception("取消后 SSH 进程仍在运行");
            }
            catch (ArgumentException) { }
        }
        finally { Environment.SetEnvironmentVariable("VMCUBE_CHECK_PID", null); }
        Console.WriteLine("SSH process checks passed: authentication failure, cancellation and child process cleanup.");
    }
    finally { Environment.SetEnvironmentVariable("PATH", previousPath); Directory.Delete(root, true); }
}

var endpoint = ConsoleEndpoint.FromDomainXml("<domain><devices><graphics type='vnc' port='5902'><listen type='address' address='0.0.0.0'/></graphics></devices></domain>");
if (endpoint != new ConsoleEndpoint("127.0.0.1", 5902)) throw new Exception("通配监听地址解析失败");
foreach (var xml in new[] { "<domain><devices><graphics type='vnc' port='-1'/></devices></domain>", "<domain><devices><graphics type='vnc' port='5900' socket='/tmp/vnc'/></devices></domain>" })
{
    try { ConsoleEndpoint.FromDomainXml(xml); throw new Exception("无效控制台未拒绝"); }
    catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { }
}
try { ConsoleEndpoint.FromDomainXml("<!DOCTYPE domain [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><domain>&x;</domain>"); throw new Exception("DTD 未拒绝"); }
catch (System.Xml.XmlException) { }
var sshArgs = ConsoleTunnel.BuildArguments("qemu+ssh://admin@kvm.example:2222/system", new("::1", 5901), 35001);
if (!sshArgs.Contains("BatchMode=yes") || !sshArgs.Contains("StrictHostKeyChecking=yes") || !sshArgs.Contains("127.0.0.1:35001:[::1]:5901") || !sshArgs.Contains("2222") || sshArgs.Last() != "kvm.example")
    throw new Exception("SSH 隧道参数错误");
try { ConsoleTunnel.BuildArguments("qemu+ssh://user:secret@host/system", endpoint, 35001); throw new Exception("URI 密码未拒绝"); }
catch (ArgumentException) { }
await using (var local = await ConsoleTunnel.OpenAsync("qemu:///system", endpoint, default))
    if (local.Host != endpoint.Host || local.Port != endpoint.Port) throw new Exception("本地控制台路由失败");
var spiceEndpoint = ConsoleEndpoint.FromDomainXml("<domain><devices><graphics type='vnc' port='5901'/><graphics type='spice' port='5902'/></devices></domain>");
Check(spiceEndpoint.Protocol == ConsoleProtocol.Spice && spiceEndpoint.Port == 5902, "SPICE is preferred during automatic discovery");
Check(ConsoleEndpoint.FromDomainXml("<domain><devices><graphics type='spice' port='-1' tlsPort='5903'/></devices></domain>").UseTls, "TLS-only SPICE endpoint discovered");
var remoteProcess = LibvirtService.BuildStartInfo("qemu+ssh://admin@kvm.example:2222/session", ["start", vm.Id]);
Check(remoteProcess.FileName == "ssh" && remoteProcess.ArgumentList.Last() == "LC_ALL=C virsh --connect qemu:///session start " + vm.Id, "remote management uses server virsh over SSH");
try { LibvirtService.BuildStartInfo("qemu+ssh://admin@kvm.example/system", ["start", vm.Id + "; id"]); throw new Exception("remote shell injection accepted"); }
catch (ArgumentException) { }
var frameHeader = new byte[16]; "VMSP"u8.CopyTo(frameHeader); BinaryPrimitives.WriteUInt32LittleEndian(frameHeader.AsSpan(4), 2); BinaryPrimitives.WriteUInt32LittleEndian(frameHeader.AsSpan(8), 3); BinaryPrimitives.WriteUInt32LittleEndian(frameHeader.AsSpan(12), 24);
Check(SpiceDesktopSession.ParseFrameHeader(frameHeader) == (2, 3, 24), "SPICE frame header parsed");
frameHeader[12] = 25;
try { SpiceDesktopSession.ParseFrameHeader(frameHeader); throw new Exception("invalid frame accepted"); } catch (InvalidDataException) { }
var zipRoot = Path.Combine(Path.GetTempPath(), "vmcube-archive-" + Guid.NewGuid()); Directory.CreateDirectory(zipRoot);
try
{
    var zip = Path.Combine(zipRoot, "bad.zip");
    using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
    using (var writer = new StreamWriter(archive.CreateEntry("../escape").Open())) writer.Write("bad");
    try { QemuEngineInstaller.ExtractVerifiedArchive(zip, Path.Combine(zipRoot, "extracted")); throw new Exception("path traversal accepted"); }
    catch (InvalidDataException) { }
}
finally { Directory.Delete(zipRoot, recursive: true); }
Console.WriteLine("Console discovery, SPICE framing, remote SSH management, archive traversal and injection checks passed.");

byte[] challenge = Enumerable.Range(0, 16).Select(x => (byte)x).ToArray();
foreach (var version in new[] { 3, 7, 8 })
    foreach (var authenticated in new[] { false, true }) await VncLoopback(version, authenticated);
Console.WriteLine("VNC loopback passed: RFB 3.3/3.7/3.8, authentication, pixels, resize, pointer, keyboard and disconnect.");
if (args.Contains("--spice-helper")) { await SpiceChecks.RunAsync(args); await LocalVmChecks.RunAsync(args); await ImportChecks.RunAsync(args); }
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task<byte[]> Read(NetworkStream stream, int size, CancellationToken token)
{
    var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, token); return bytes;
}
async Task Write(NetworkStream stream, byte[] bytes, CancellationToken token)
{
    // Exercise fragmented TCP reads, including headers and authentication.
    foreach (var value in bytes) await stream.WriteAsync(new[] { value }, token);
}
async Task Handshake(NetworkStream stream, int version, bool authentication, CancellationToken token)
{
    var banner = Encoding.ASCII.GetBytes($"RFB 003.00{version}\n");
    await Write(stream, banner, token); Check((await Read(stream, 12, token)).SequenceEqual(banner), "RFB version negotiation");
    var security = authentication ? (byte)2 : (byte)1;
    if (version == 3) await Write(stream, [0, 0, 0, security], token);
    else { await Write(stream, [1, security], token); Check((await Read(stream, 1, token))[0] == security, "RFB security selection"); }
    if (authentication)
    {
        await Write(stream, challenge, token);
        Check((await Read(stream, 16, token)).SequenceEqual(VncAuthentication.Respond("secret", challenge)), "VNC password challenge response");
    }
    if (authentication || version == 8) await Write(stream, [0, 0, 0, 0], token);
    Check((await Read(stream, 1, token))[0] == 1, "shared VNC connection");
    var init = new byte[24]; init[1] = 2; init[3] = 2;
    await Write(stream, init, token);
    var pixel = await Read(stream, 20, token); Check(pixel[4] == 32 && pixel[6] == 0 && pixel[7] == 1 && pixel[14] == 16, "32-bit BGRX pixel format requested");
    var enc = await Read(stream, 16, token); Check(enc[0] == 2 && enc[3] == 3 && BinaryPrimitives.ReadInt32BigEndian(enc.AsSpan(12)) == -223, "raw, CopyRect and resize negotiated");
}
byte[] Update(int x, int y, int width, int height, int encoding, byte[] data)
{
    var bytes = new byte[16 + data.Length]; bytes[3] = 1;
    BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), (ushort)x); BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), (ushort)y);
    BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8), (ushort)width); BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10), (ushort)height);
    BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12), encoding); data.CopyTo(bytes, 16); return bytes;
}
async Task VncLoopback(int version, bool authenticated)
{
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
    var client = new VncDesktopSession();
    var frames = System.Threading.Channels.Channel.CreateUnbounded<LocalDesktopFrame>();
    client.Frame += frame => frames.Writer.TryWrite(frame);
    var running = client.RunAsync(new(LocalDesktopProtocol.Vnc, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, Password: "secret"), stop.Token);
    using var server = await listener.AcceptTcpClientAsync(timeout.Token); var stream = server.GetStream();
    await Handshake(stream, version, authenticated, timeout.Token);
    Check((await Read(stream, 10, timeout.Token))[1] == 0, "initial full framebuffer request");
    await Write(stream, Update(0, 0, 2, 2, 0, [1,2,3,0,4,5,6,0,7,8,9,0,10,11,12,0]), timeout.Token);
    var first = await frames.Reader.ReadAsync(timeout.Token);
    Check(first.Width == 2 && first.Height == 2 && first.Pixels.SequenceEqual(new byte[] { 1,2,3,255,4,5,6,255,7,8,9,255,10,11,12,255 }), "fragmented raw framebuffer decoded with opaque alpha");
    Check((await Read(stream, 10, timeout.Token))[1] == 1, "incremental framebuffer request");
    await Write(stream, Update(0, 1, 2, 1, 1, [0,0,0,0]), timeout.Token);
    var copy = await frames.Reader.ReadAsync(timeout.Token);
    Check(copy.Pixels.AsSpan(0, 8).SequenceEqual(copy.Pixels.AsSpan(8, 8)) && first.Pixels[8] == 7, "CopyRect handles overlap without mutating delivered frames");
    await Read(stream, 10, timeout.Token);
    await Write(stream, Update(0, 0, 3, 1, -223, []), timeout.Token);
    var resized = await frames.Reader.ReadAsync(timeout.Token);
    Check(resized.Width == 3 && resized.Height == 1 && resized.Pixels.Length == 12, "desktop resize changes framebuffer");
    var resizeRequest = await Read(stream, 10, timeout.Token);
    Check(resizeRequest[1] == 0 && BinaryPrimitives.ReadUInt16BigEndian(resizeRequest.AsSpan(6)) == 3, "resize requests full new framebuffer");
    client.Pointer(999, -1, 1); client.Pointer(2, 0, 0, -1); client.Key(0xffe3, true); client.Key(0xffe3, false);
    var pointer = await Read(stream, 6, timeout.Token);
    Check(pointer.SequenceEqual(new byte[] { 5,1,0,2,0,0 }), "pointer mapped and clamped to remote dimensions");
    Check((await Read(stream, 6, timeout.Token))[1] == 16 && (await Read(stream, 6, timeout.Token))[1] == 0, "wheel press followed by release");
    Check((await Read(stream, 8, timeout.Token))[1] == 1 && (await Read(stream, 8, timeout.Token))[1] == 0, "keyboard press and release transmitted");
    stop.Cancel();
    try { await running.WaitAsync(timeout.Token); } catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or IOException or SocketException) { }
    Check(running.IsCompleted, "cancel disconnects VNC and joins writer");
}
