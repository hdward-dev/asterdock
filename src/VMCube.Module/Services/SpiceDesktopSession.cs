using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using URemote.Core;
namespace VMCube.Module.Services;

/// <summary>Thrown when this RID has no bundled or installed <c>vmcube-spice</c> helper.</summary>
public sealed class SpiceComponentMissingException(string runtimeIdentifier) : NotSupportedException(SpiceDesktopSession.MissingComponentMessage(runtimeIdentifier))
{
    public string RuntimeIdentifier { get; } = runtimeIdentifier;
}

/// <summary>Managed transport for the bundled, isolated spice-client-glib worker.</summary>
public sealed partial class SpiceDesktopSession(bool useTls = false, string? caFile = null, string? helperPath = null, string? dataDirectory = null) : ILocalDesktopSession
{
    private readonly Channel<byte[]> _input = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.Wait });
    private volatile bool _ready;
    public event Action<LocalDesktopFrame>? Frame;
    public event Action<string>? Status;
    public static string HelperFileName { get; } = OperatingSystem.IsWindows() ? "vmcube-spice.exe" : "vmcube-spice";
    public static string MissingComponentMessage(string rid) =>
        $"此应用包未包含 {rid} 的 SPICE 组件。请点击「安装 SPICE 组件」下载并校验官方组件，或使用包含对应平台组件的 VMCUBE 安装包。";
    public static IReadOnlyList<string> HelperCandidates(string? dataDirectory = null)
    {
        var file = HelperFileName;
        var rid = RuntimeInformation.RuntimeIdentifier;
        var module = Path.GetDirectoryName(typeof(SpiceDesktopSession).Assembly.Location)!;
        var paths = new List<string> { Path.Combine(module, "native", rid, file), Path.Combine(AppContext.BaseDirectory, "native", rid, file) };
        if (InstalledHelper(dataDirectory, file) is { } installed) paths.Add(installed);
        return paths;
    }
    public static string? TryFindHelper(string? dataDirectory = null) =>
        HelperCandidates(dataDirectory).FirstOrDefault(File.Exists);
    public static string FindHelper(string? dataDirectory = null)
    {
        var executable = TryFindHelper(dataDirectory) ?? throw new SpiceComponentMissingException(RuntimeInformation.RuntimeIdentifier);
        PrepareExecutable(executable); return executable;
    }
    private static string? InstalledHelper(string? dataDirectory, string file)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory)) return null;
        string? directory;
        try { directory = SpiceComponentInstaller.GetInstalledDirectory(dataDirectory); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { return null; }
        if (string.IsNullOrWhiteSpace(directory)) return null;
        var helper = Path.GetFullPath(Path.Combine(directory, file));
        var root = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!helper.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;
        return helper;
    }
    public static void PrepareExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        var mode = File.GetUnixFileMode(path);
        if (!mode.HasFlag(UnixFileMode.UserExecute)) File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute);
    }
    public async Task RunAsync(LocalDesktopOptions options, CancellationToken cancellationToken)
    {
        options.Validate();
        var executable = helperPath ?? FindHelper(dataDirectory);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _transportStop = stop;
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        // GLib debug logging uses stdout, which is reserved for the binary frame protocol.
        info.Environment.Remove("SPICE_DEBUG"); info.Environment.Remove("G_MESSAGES_DEBUG");
        info.ArgumentList.Add(options.Host); info.ArgumentList.Add(options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (useTls) { info.ArgumentList.Add("--tls"); if (!string.IsNullOrWhiteSpace(caFile)) info.ArgumentList.Add(caFile); }
        using var process = new Process { StartInfo = info };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception ex) { throw new InvalidOperationException("SPICE 内置组件无法启动，请检查应用包是否完整。", ex); }
        var errors = process.StandardError.ReadToEndAsync();
        Task? writer = null;
        Exception? failure = null;
        try
        {
            Status?.Invoke("正在连接 SPICE…");
            var password = Encoding.UTF8.GetBytes(options.Password);
            if (password.Length > 4096) throw new ArgumentException("SPICE 密码过长。");
            try
            {
                var count = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(count, (uint)password.Length);
                await process.StandardInput.BaseStream.WriteAsync(count, stop.Token);
                await process.StandardInput.BaseStream.WriteAsync(password, stop.Token);
                await process.StandardInput.BaseStream.FlushAsync(stop.Token);
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(password); }
            writer = WriteInputsAsync(process.StandardInput.BaseStream, stop);
            var stream = process.StandardOutput.BaseStream;
            while (true)
            {
                var header = new byte[16];
                var first = await stream.ReadAsync(header.AsMemory(0, 1), stop.Token);
                if (first == 0) break;
                await stream.ReadExactlyAsync(header.AsMemory(1), stop.Token);
                if (header.AsSpan(0, 4).SequenceEqual("VMFT"u8))
                {
                    var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
                    if (length is < 16 or > 65536) throw new InvalidDataException("SPICE 文件传输消息长度无效。");
                    var payload = new byte[(int)length]; await stream.ReadExactlyAsync(payload, stop.Token);
                    ReceiveTransferEvent(header, payload); continue;
                }
                var (width, height, size) = ParseFrameHeader(header);
                var pixels = new byte[size]; await stream.ReadExactlyAsync(pixels, stop.Token);
                if (!_ready) { _ready = true; Status?.Invoke("已连接 · SPICE"); }
                Frame?.Invoke(new(width, height, pixels));
            }
            await process.WaitForExitAsync(stop.Token);
            if (process.ExitCode != 0) throw new InvalidOperationException((await errors).Trim() is { Length: > 0 } error ? error : "SPICE 连接失败。");
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            _ready = false; stop.Cancel(); _input.Writer.TryComplete();
            EndFileTransfers(); _transportStop = null;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            if (writer is not null) { try { await writer; } catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { } }
        }
        var stderr = (await errors).Trim();
        cancellationToken.ThrowIfCancellationRequested();
        if (failure is not null)
        {
            if (failure is IOException or OperationCanceledException && stderr.Length > 0) throw new InvalidOperationException(stderr, failure);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
    public static (int Width, int Height, int Bytes) ParseFrameHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length != 16 || !header[..4].SequenceEqual("VMSP"u8)) throw new InvalidDataException("SPICE 组件返回无效画面头。");
        var width = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        var height = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        var bytes = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        if (width is < 1 or > 8192 || height is < 1 or > 8192 || (ulong)width * height > 16 * 1024 * 1024 || bytes != (ulong)width * height * 4)
            throw new InvalidDataException("SPICE 画面尺寸或数据长度无效。");
        return ((int)width, (int)height, (int)bytes);
    }
    private async Task WriteInputsAsync(Stream stream, CancellationTokenSource stop)
    {
        try
        {
            await foreach (var packet in _input.Reader.ReadAllAsync(stop.Token))
            { await stream.WriteAsync(packet, stop.Token); await stream.FlushAsync(stop.Token); }
        }
        catch { stop.Cancel(); throw; }
    }
    private void Queue(uint kind, int a, int b, int c = 0, int d = 0)
    {
        if (!_ready) return;
        var packet = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, kind);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), a); BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), b);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12), c); BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(16), d);
        if (!_input.Writer.TryWrite(packet)) throw new IOException("SPICE 输入队列已满，请断开后重连。");
    }
    public void Key(uint symbol, bool down) => Queue(1, checked((int)symbol), down ? 1 : 0);
    public void Pointer(int x, int y, int buttons, int wheel = 0) => Queue(2, Math.Max(0, x), Math.Max(0, y), buttons & 7, Math.Sign(wheel));
}
