using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using URemote.Core;
namespace VMCube.Module.Services;

/// <summary>Managed transport for the bundled, isolated spice-client-glib worker.</summary>
public sealed class SpiceDesktopSession(bool useTls = false, string? caFile = null, string? helperPath = null) : ILocalDesktopSession
{
    private readonly Channel<byte[]> _input = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.Wait });
    private volatile bool _ready;
    public event Action<LocalDesktopFrame>? Frame;
    public event Action<string>? Status;
    public static string FindHelper()
    {
        var file = OperatingSystem.IsWindows() ? "vmcube-spice.exe" : "vmcube-spice";
        var rid = RuntimeInformation.RuntimeIdentifier;
        var module = Path.GetDirectoryName(typeof(SpiceDesktopSession).Assembly.Location)!;
        var paths = new[] { Path.Combine(module, "native", rid, file), Path.Combine(AppContext.BaseDirectory, "native", rid, file) };
        var executable = paths.FirstOrDefault(File.Exists) ?? throw new NotSupportedException($"此应用包未包含 {rid} 的 SPICE 组件，请使用包含对应平台组件的 VMCUBE 安装包。");
        PrepareExecutable(executable); return executable;
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
        var executable = helperPath ?? FindHelper();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
