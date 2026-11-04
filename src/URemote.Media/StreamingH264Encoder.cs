using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace URemote.Media;

/// <summary>One persistent, backpressured encoder per output. No frame files.</summary>
public sealed class StreamingH264Encoder : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task<string> errors;
    private readonly int width, height;
    private readonly string executable;
    public string Codec { get; }
    public bool Hardware => Codec != "libx264";
    // Authorized official codec-name tables: Nvenc=0, VideoToolbox=3;
    // -1 is the generic software implementation. Report only a probed backend.
    public static async Task<int> ImplementationAsync(string executable, CancellationToken ct = default)
        => OperatingSystem.IsMacOS() ? 3 : OperatingSystem.IsLinux()
            && await VideoEncoderSelection.NvencAsync(executable, ct).ConfigureAwait(false) ? 0 : -1;
    public static async Task<StreamingH264Encoder> CreateAsync(string executable, int width, int height, bool inverted, uint format,
        int fps = 30, int outputWidth = 1280, int outputHeight = 720, int bitrate = 4000000, int crf = 18, CancellationToken stopToken = default)
    {
        var useNvenc = await ImplementationAsync(executable, stopToken).ConfigureAwait(false) == 0;
        stopToken.ThrowIfCancellationRequested();
        return new(executable, width, height, inverted, format, fps, outputWidth, outputHeight, bitrate, crf, useNvenc);
    }
    public StreamingH264Encoder(string executable, int width, int height, bool inverted, uint format, int fps = 30, int outputWidth = 1280, int outputHeight = 720, int bitrate = 4000000, int crf = 18, bool useNvenc = false)
    {
        if (crf is < 0 or > 51 || outputWidth is < 2 or > 7680 || outputHeight is < 2 or > 4320 || bitrate is < 1000000 or > 40000000 || width is < 2 or > 16384 || height is < 2 or > 16384 || format > 1 || fps is < 1 or > URemote.Core.HostDisplayInfo.MaxSupportedFps)
            throw new ArgumentException("Unsupported stream format.");
        this.width = width; this.height = height; this.executable = executable;
        Codec = useNvenc ? "h264_nvenc" : OperatingSystem.IsMacOS() ? "h264_videotoolbox" : "libx264";
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden };
        // Frequent IDRs plus a 125 ms VBV caused periodic text-quality collapse.
        // Keep 500 ms of encoder rate-control budget (not a playback queue),
        // without periodic IDRs; each peer starts with an IDR and authenticated PLI/FIR
        // requests a refresh when needed. No inter-frame lookahead or B-frame queue.
        string[] args = ["-hide_banner", "-loglevel", "error", "-f", "rawvideo", "-pixel_format", format == 1 ? "bgr0" : "bgra",
            "-video_size", $"{width}x{height}", "-framerate", fps.ToString(), "-i", "pipe:0", "-an",
            "-vf", (inverted ? "vflip," : "") + $"scale={outputWidth}:{outputHeight}:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=bilinear,pad={outputWidth}:{outputHeight}:(ow-iw)/2:(oh-ih)/2",
            "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-profile:v", "baseline", "-level:v", "5.2",
            "-pix_fmt", "yuv420p", "-crf", crf.ToString(), "-maxrate", bitrate.ToString(), "-bufsize", (bitrate / 2).ToString(),
            "-x264-params", "aud=1:repeat-headers=1:keyint=infinite:scenecut=0", "-threads", Math.Clamp(Environment.ProcessorCount / 2, 2, 8).ToString(),
            "-flush_packets", "1", "-f", "h264", "pipe:1"];
        if (useNvenc)
        {
            var codec = Array.IndexOf(args, "-c:v"); var tail = Array.IndexOf(args, "-flush_packets");
            var input = args[..codec].ToList();
            var directRgb = width == outputWidth && height == outputHeight;
            if (directRgb)
            {
                // NVENC accepts packed RGB and converts it on the GPU. Avoid a full
                // 4K BGRA->YUV420 conversion on the CPU when no resize is needed.
                var filter = input.IndexOf("-vf");
                input.RemoveRange(filter, 2);
                if (inverted) input.AddRange(["-vf", "vflip"]);
            }
            args = [..input, "-c:v", "h264_nvenc", "-preset", "p1", "-tune", "ull", "-bf", "0", "-rc-lookahead", "0",
                "-zerolatency", "1", "-delay", "0", "-profile:v", "baseline", "-level:v", "5.2",
                "-pix_fmt", directRgb ? format == 1 ? "bgr0" : "bgra" : "yuv420p", "-rgb_mode", "yuv420",
                "-rc", "vbr", "-cq", crf.ToString(), "-b:v", bitrate.ToString(), "-maxrate", bitrate.ToString(),
                "-bufsize", (bitrate / 2).ToString(), "-g", "999999", "-aud", "1", ..args[tail..]];
        }
        else if (OperatingSystem.IsMacOS())
        {
            var codec = Array.IndexOf(args, "-c:v"); var tail = Array.IndexOf(args, "-flush_packets");
            args = [..args[..codec], "-c:v", "h264_videotoolbox", "-realtime", "1", "-allow_sw", "1", "-bf", "0",
                "-profile:v", "baseline", "-level:v", "5.2", "-pix_fmt", "yuv420p", "-b:v", bitrate.ToString(),
                "-g", "600", "-bsf:v", "h264_metadata=aud=insert", ..args[tail..]];
        }
        foreach (var arg in args) start.ArgumentList.Add(arg);
        process = Process.Start(start) ?? throw new IOException("Cannot start video encoder.");
        URemote.Core.HiddenConsole.Hide(process);
        errors = process.StandardError.ReadToEndAsync();
    }
    public async Task WriteAsync(byte[] pixels, int stride, CancellationToken ct)
    {
        if (stride < width * 4 || (long)stride * height != pixels.Length) throw new ArgumentException("Frame size changed.");
        try
        {
            if (stride == width * 4) await process.StandardInput.BaseStream.WriteAsync(pixels, ct);
            else for (var row = 0; row < height; row++)
                await process.StandardInput.BaseStream.WriteAsync(pixels.AsMemory(row * stride, width * 4), ct);
        }
        catch (IOException e) { throw Failure(e); }
    }
    public void CompleteInput() => process.StandardInput.Close();
    public async IAsyncEnumerable<byte[]> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var parser = new AnnexBAccessUnits();
        var buffer = new byte[65536];
        try
        {
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, ct)) != 0)
                foreach (var unit in parser.Push(buffer.AsSpan(0, count))) yield return unit;
            if (parser.Finish() is { } tail) yield return tail;
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0) throw Failure();
        }
        finally { Array.Clear(buffer); parser.Clear(); }
    }
    private VideoEncoderException Failure(Exception? inner = null)
    {
        if (Codec == "h264_nvenc") VideoEncoderSelection.DisableNvenc(executable);
        return new(Codec, inner);
    }
    public async ValueTask DisposeAsync()
    {
        try { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); await errors; }
        finally { process.Dispose(); }
    }
}

// AUD marks access-unit boundaries; accepts start codes split across arbitrary pipe reads.
public sealed class AnnexBAccessUnits
{
    private readonly List<byte> pending = [];
    private int scanned;
    private bool sawAud;
    public List<byte[]> Push(ReadOnlySpan<byte> bytes)
    {
        if (pending.Count + bytes.Length > 8_000_000) throw new InvalidDataException("H264 access unit exceeds limit.");
        foreach (var b in bytes) pending.Add(b);
        var result = new List<byte[]>();
        while (scanned + 4 < pending.Count)
        {
            int prefix = pending[scanned] == 0 && pending[scanned + 1] == 0
                ? pending[scanned + 2] == 1 ? 3 : pending[scanned + 2] == 0 && pending[scanned + 3] == 1 ? 4 : 0 : 0;
            if (prefix > 0 && (pending[scanned + prefix] & 31) == 9)
            {
                if (sawAud && scanned > 0)
                { result.Add(pending.GetRange(0, scanned).ToArray()); pending.RemoveRange(0, scanned); scanned = 0; }
                sawAud = true; scanned += prefix;
            }
            else scanned++;
        }
        return result;
    }
    public byte[]? Finish() { var result = sawAud && pending.Count > 0 ? pending.ToArray() : null; Clear(); return result; }
    public void Clear() { for (int i = 0; i < pending.Count; i++) pending[i] = 0; pending.Clear(); scanned = 0; sawAud = false; }
}
