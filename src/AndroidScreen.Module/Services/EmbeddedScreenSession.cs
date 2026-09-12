using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace AndroidScreen.Module.Services;

public sealed class EmbeddedScreenSession : IAsyncDisposable
{
    // Some Android encoders label SDR screen frames as YCgCo/log316, which FFmpeg 8
    // cannot negotiate for RGB output. Normalize screen-capture metadata before scaling.
    public const string DecoderColorFilter = "setparams=colorspace=bt709:color_primaries=bt709:color_trc=bt709";
    private readonly AndroidBridge _bridge;
    private readonly string _serial;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte[]> _commands = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private TcpClient _video = new() { NoDelay = true };
    private readonly TcpClient _control = new() { NoDelay = true };
    private Process? _server;
    private Process? _decoder;
    private string _decoderPath = "";
    private CancellationTokenSource? _decodeStop;
    private Task? _decodeFrames;
    private Task? _decodeLog;
    private int _port;
    private Task? _completion;
    private Task? _startup;
    private readonly List<Task> _workers = [];
    private readonly Queue<string> _diagnostics = new();
    private readonly string _remotePath = "/data/local/tmp/asterdock-" + Guid.NewGuid().ToString("N") + ".jar";
    private bool _disposed;

    public EmbeddedScreenSession(AndroidBridge bridge, string serial) { _bridge = bridge; _serial = serial; }
    public event Action<byte[]>? FrameReceived;
    public event Action<string>? ClipboardReceived;
    public string DeviceName { get; private set; } = "Android";
    public Task Completion => _completion ?? Task.CompletedTask;

    public Task StartAsync(string serverPath, string decoderPath, int maxSize, CancellationToken cancellationToken)
    {
        if (_startup is not null || _disposed) throw new InvalidOperationException("此投屏会话已经启动或关闭。");
        return _startup = StartCoreAsync(serverPath, decoderPath, maxSize, cancellationToken);
    }

    private async Task StartCoreAsync(string serverPath, string decoderPath, int maxSize, CancellationToken cancellationToken)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        startup.CancelAfter(TimeSpan.FromSeconds(35));
        var token = startup.Token;
        var id = Random.Shared.Next(1, int.MaxValue).ToString("x8");
        await _bridge.RunAsync(token, "-s", _serial, "push", serverPath, _remotePath).ConfigureAwait(false);
        var port = await _bridge.RunAsync(token, "-s", _serial, "forward", "tcp:0", "localabstract:scrcpy_" + id).ConfigureAwait(false);
        if (!int.TryParse(port, out _port) || _port is < 1 or > 65535) throw new IOException("无法建立设备视频通道。");
        _server = _bridge.Start("-s", _serial, "shell", "CLASSPATH=" + _remotePath, "app_process", "/",
            "com.genymobile.scrcpy.Server", ScrcpyInstallerService.ServerVersion, "scid=" + id,
            "tunnel_forward=true", "audio=false", "control=true", "video_codec=h264",
            "send_frame_meta=true", "send_codec_meta=false", "clipboard_autosync=false",
            "max_size=" + maxSize, "max_fps=30", "video_bit_rate=8000000", "log_level=warn");
        _workers.Add(ReadLogAsync(_server.StandardError));
        _workers.Add(ReadLogAsync(_server.StandardOutput));
        // ADB accepts TCP even before the device socket exists. Retry until the server's dummy byte arrives.
        await ConnectVideoAsync(token).ConfigureAwait(false);
        await _control.ConnectAsync("127.0.0.1", _port, token).ConfigureAwait(false);
        var name = new byte[64];
        await _video.GetStream().ReadExactlyAsync(name, token).ConfigureAwait(false);
        DeviceName = Encoding.UTF8.GetString(name).TrimEnd('\0');

        _decoderPath = decoderPath;
        _completion = RunAsync();
    }

    private async Task StartDecoderAsync(CancellationToken token)
    {
        await StopDecoderAsync().ConfigureAwait(false);
        var info = new ProcessStartInfo(_decoderPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_decoderPath)!
        };
        // Resolve the packaged dylibs only in this child process.
        if (OperatingSystem.IsMacOS()) info.Environment["DYLD_LIBRARY_PATH"] = Path.GetDirectoryName(_decoderPath)!;
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-probesize", "32", "-analyzeduration", "0",
            "-flags", "low_delay", "-f", "h264", "-i", "pipe:0", "-an", "-fps_mode", "passthrough",
            "-vf", DecoderColorFilter, "-c:v", "bmp", "-pix_fmt", "bgr24", "-f", "image2pipe", "-flush_packets", "1", "pipe:1" })
            info.ArgumentList.Add(argument);
        _decoder = Process.Start(info) ?? throw new IOException("无法启动视频解码器。");
        _decodeStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _decodeFrames = ReadFramesAsync(_decoder.StandardOutput.BaseStream, _decodeStop.Token);
        _decodeLog = ReadLogAsync(_decoder.StandardError);
    }

    private async Task StopDecoderAsync()
    {
        _decodeStop?.Cancel();
        AndroidBridge.Kill(_decoder);
        if (_decodeFrames is not null) { try { await _decodeFrames.ConfigureAwait(false); } catch { } }
        if (_decodeLog is not null) { try { await _decodeLog.ConfigureAwait(false); } catch { } }
        _decoder?.Dispose(); _decodeStop?.Dispose();
        _decoder = null; _decodeFrames = null; _decodeLog = null; _decodeStop = null;
    }

    // scrcpy marks encoder configuration packets with bit 63. A rotation restarts the
    // device encoder. Restart our decoder on the next media packet, feeding all SPS/PPS
    // packets together so the BMP encoder and input coordinates use the new dimensions.
    private async Task ReadVideoAsync(CancellationToken token)
    {
        var stream = _video.GetStream();
        using var config = new MemoryStream();
        var header = new byte[12];
        while (true)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var read = stream.ReadExactlyAsync(header, timeout.Token).AsTask();
            if (_decodeFrames is not null)
            {
                var done = await Task.WhenAny(read, _decodeFrames).ConfigureAwait(false);
                if (done == _decodeFrames)
                {
                    timeout.Cancel();
                    try { await read.ConfigureAwait(false); } catch { }
                    await _decodeFrames.ConfigureAwait(false);
                    throw new IOException("视频解码器已停止。");
                }
            }
            await read.ConfigureAwait(false);
            var metadata = BinaryPrimitives.ReadUInt64BigEndian(header);
            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8));
            if (length is <= 0 or > 4 * 1024 * 1024) throw new InvalidDataException("视频数据包大小无效。");
            var packet = new byte[length];
            await stream.ReadExactlyAsync(packet, timeout.Token).ConfigureAwait(false);
            if ((metadata & (1UL << 63)) != 0)
            {
                if (config.Length + length > 1024 * 1024) throw new InvalidDataException("视频编码参数过大。");
                config.Write(packet); continue;
            }
            if (config.Length > 0)
            {
                await StartDecoderAsync(token).ConfigureAwait(false);
                await _decoder!.StandardInput.BaseStream.WriteAsync(config.GetBuffer().AsMemory(0, (int)config.Length), token).ConfigureAwait(false);
                config.SetLength(0);
            }
            if (_decoder is null) throw new InvalidDataException("未收到视频编码参数。");
            await _decoder.StandardInput.BaseStream.WriteAsync(packet, token).ConfigureAwait(false);
        }
    }

    private async Task ConnectVideoAsync(CancellationToken token)
    {
        // Each failed forward closes its socket; create a fresh socket for the next attempt.
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var candidate = new TcpClient { NoDelay = true };
            try
            {
                await candidate.ConnectAsync("127.0.0.1", _port, token).ConfigureAwait(false);
                var dummy = new byte[1];
                await candidate.GetStream().ReadExactlyAsync(dummy, token).ConfigureAwait(false);
                if (dummy[0] != 0) throw new IOException("投屏握手无效。");
                _video.Dispose();
                _video = candidate;
                return;
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                candidate.Dispose();
                if (_server is { HasExited: true }) throw new IOException("手机端投屏服务启动失败。" + Diagnostics(), ex);
                await Task.Delay(150, token).ConfigureAwait(false);
            }
            catch { candidate.Dispose(); throw; }
        }
    }

    public bool Send(byte[] command) => !_stop.IsCancellationRequested && _commands.Writer.TryWrite(command);

    private async Task RunAsync()
    {
        var token = _stop.Token;
        var video = ReadVideoAsync(token);
        var send = SendCommandsAsync(token);
        var receive = ReadClipboardAsync(token);
        var server = _server!.WaitForExitAsync(token);
        var tasks = new[] { video, send, receive, server };
        try
        {
            var ended = await Task.WhenAny(tasks).ConfigureAwait(false);
            await ended.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            throw new IOException("设备连接已断开。" + Diagnostics());
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException("等待视频数据超时，请检查设备连接后重试。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !token.IsCancellationRequested)
        {
            throw new IOException(ex.Message + Diagnostics(), ex);
        }
        finally
        {
            _stop.Cancel();
            _video.Dispose(); _control.Dispose();
            AndroidBridge.Kill(_decoder); AndroidBridge.Kill(_server);
            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { /* observed by the owning session */ }
            await StopDecoderAsync().ConfigureAwait(false);
        }
    }

    public static async Task<byte[]> ReadBitmapAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[14];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(2));
        if (header[0] != 'B' || header[1] != 'M' || length is < 54 or > 24 * 1024 * 1024)
            throw new InvalidDataException("视频帧格式或大小无效。");
        var frame = new byte[length]; header.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(14), token).ConfigureAwait(false);
        return frame;
    }

    private async Task ReadFramesAsync(Stream stream, CancellationToken token)
    {
        using var firstFrame = CancellationTokenSource.CreateLinkedTokenSource(token);
        firstFrame.CancelAfter(TimeSpan.FromSeconds(20));
        try { FrameReceived?.Invoke(await ReadBitmapAsync(stream, firstFrame.Token).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException("等待画面超时，请解锁手机并重新连接。"); }
        while (true) FrameReceived?.Invoke(await ReadBitmapAsync(stream, token).ConfigureAwait(false));
    }

    private async Task SendCommandsAsync(CancellationToken token)
    {
        await foreach (var command in _commands.Reader.ReadAllAsync(token).ConfigureAwait(false))
            await _control.GetStream().WriteAsync(command, token).ConfigureAwait(false);
    }

    private async Task ReadClipboardAsync(CancellationToken token)
    {
        var stream = _control.GetStream(); var type = new byte[1];
        while (true)
        {
            await stream.ReadExactlyAsync(type, token).ConfigureAwait(false);
            if (type[0] == 1) { await stream.ReadExactlyAsync(new byte[8], token).ConfigureAwait(false); continue; }
            if (type[0] != 0) throw new InvalidDataException("未知的设备控制消息。");
            var header = new byte[4]; await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
            var size = BinaryPrimitives.ReadInt32BigEndian(header);
            if (size is < 0 or > 256 * 1024) throw new InvalidDataException("设备剪贴板内容过大。");
            var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            ClipboardReceived?.Invoke(Encoding.UTF8.GetString(bytes));
        }
    }

    private async Task ReadLogAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
                lock (_diagnostics) { _diagnostics.Enqueue(line.Length > 1024 ? line[..1024] : line); while (_diagnostics.Count > 48) _diagnostics.Dequeue(); }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }
    private string Diagnostics() { lock (_diagnostics) return string.Join(" ", _diagnostics).Trim(); }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        _stop.Cancel(); _commands.Writer.TryComplete();
        // Startup owns resources until it has observed cancellation. Joining it avoids
        // launching a server/decoder after disposal has already killed existing children.
        if (_startup is not null) { try { await _startup.ConfigureAwait(false); } catch { } }
        _video.Dispose(); _control.Dispose();
        AndroidBridge.Kill(_decoder); AndroidBridge.Kill(_server);
        if (_completion is not null) { try { await _completion.ConfigureAwait(false); } catch { } }
        try { await Task.WhenAll(_workers).ConfigureAwait(false); } catch { }
        _decoder?.Dispose(); _server?.Dispose();
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            if (_port > 0) await _bridge.RunAsync(cleanup.Token, "-s", _serial, "forward", "--remove", "tcp:" + _port).ConfigureAwait(false);
            await _bridge.RunAsync(cleanup.Token, "-s", _serial, "shell", "rm", "-f", _remotePath).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or System.ComponentModel.Win32Exception) { }
        _stop.Dispose();
    }
}
