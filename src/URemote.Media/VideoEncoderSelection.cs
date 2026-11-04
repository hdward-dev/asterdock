using System.Collections.Concurrent;
using System.Diagnostics;

namespace URemote.Media;

internal static class VideoEncoderSelection
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<bool>>> nvenc = new(StringComparer.Ordinal);
    internal static async Task<bool> NvencAsync(string executable, CancellationToken ct)
        => await nvenc.GetOrAdd(executable, path => new(() => ProbeAsync(path))).Value.WaitAsync(ct).ConfigureAwait(false);
    internal static void DisableNvenc(string executable)
        => nvenc[executable] = new(() => Task.FromResult(false));

    private static async Task<bool> ProbeAsync(string executable)
    {
        // A listed encoder is not proof of a usable driver. Encode one synthetic
        // packed-RGB frame with the same GPU conversion used by the real stream.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Process? process = null;
        try
        {
            var start = new ProcessStartInfo(executable)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=size=256x256:rate=1,format=bgr0",
                "-frames:v", "1", "-c:v", "h264_nvenc", "-preset", "p1", "-tune", "ull", "-bf", "0", "-pix_fmt", "bgr0",
                "-rgb_mode", "yuv420", "-profile:v", "baseline", "-f", "null", "-" }) start.ArgumentList.Add(arg);
            process = Process.Start(start);
            if (process is null) return false;
            URemote.Core.HiddenConsole.Hide(process);
            var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(output, errors).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
        { return false; }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync().ConfigureAwait(false); } }
                finally { process.Dispose(); }
            }
        }
    }
}

public sealed class VideoEncoderException(string codec, Exception? inner = null)
    : IOException("Video encoder failed: " + codec, inner)
{
    public string Codec { get; } = codec;
}
