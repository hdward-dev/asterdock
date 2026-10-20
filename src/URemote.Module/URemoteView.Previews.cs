using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using URemote.Core;
using URemote.Linux;
using Avalonia.Platform;
using System.Runtime.InteropServices;

namespace URemote.Module;

public sealed partial class URemoteView
{
    private readonly List<WriteableBitmap> localBitmaps = [];
    private readonly Dictionary<int, LocalScreenModel> localScreenLabels = [];
    private readonly Dictionary<int, string> localScreenFps = [];
    private bool loadingScreens;
    private async Task RefreshAuthorizedScreensAsync()
    {
        try
        {
            var globals = await DesktopBackend.DiscoverAsync(lifetime.Token);
            if (disposed) return;
            var current = globals.Where(g => g.Interface == "wl_output").Select(g => g.Name).Order().Take(5).ToArray();
            if (!outputs.Select(o => o.Id).SequenceEqual(current))
                ReplaceScreens(current, _ => true, null);
            await RefreshLocalScreensAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch { if (!disposed) detail.Text = "无法读取已授权的显示器，请重新开启被控。"; }
    }
    private readonly Dictionary<string, Bitmap> previewCache = [];
    private DateTimeOffset localWallpaperChecked;
    private readonly HttpClient previewHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };

    private async Task RefreshPreviewsAsync()
    {
        if (loggingOut) return;
        var generation = accountGeneration;
        var activeUrls = devices.Select(d => d.WallpaperUrl).ToHashSet();
        const string localWallpaperKey = "local-wallpaper";
        if (devices.Any(d => d.IsCurrent)) activeUrls.Add(localWallpaperKey);
        foreach (var old in previewCache.Keys.Where(k => !activeUrls.Contains(k)).ToArray())
        { previewCache[old].Dispose(); previewCache.Remove(old); }
        if (devices.Any(d => d.IsCurrent) && DateTimeOffset.UtcNow - localWallpaperChecked > TimeSpan.FromSeconds(30))
        {
            localWallpaperChecked = DateTimeOffset.UtcNow;
            try
            {
                var png = await LinuxWallpaper.RenderAsync(appliedEncoder, lifetime.Token);
                if (png is not null && !disposed && !loggingOut && generation == accountGeneration)
                {
                    using var stream = new MemoryStream(png);
                    var bitmap = Bitmap.DecodeToWidth(stream, 800);
                    if (previewCache.Remove(localWallpaperKey, out var old)) old.Dispose();
                    previewCache[localWallpaperKey] = bitmap;
                    RenderDevices();
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
            catch { /* Wallpaper availability must not affect the device list. */ }
        }
        // No login headers or identity values are sent to image hosts. Keep previews in memory only.
        foreach (var url in devices.Select(d => d.WallpaperUrl).Where(u => u.Length > 0).Distinct().Take(32))
        {
            if (disposed || loggingOut || generation != accountGeneration || previewCache.Count >= 32) break;
            if (previewCache.ContainsKey(url)) continue;
            try
            {
                using var response = await previewHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 5_000_000) continue;
                await using var stream = await response.Content.ReadAsStreamAsync(lifetime.Token);
                using var bytes = new MemoryStream();
                var chunk = new byte[16384]; int read;
                while ((read = await stream.ReadAsync(chunk, lifetime.Token)) > 0)
                {
                    if (bytes.Length + read > 5_000_000) throw new IOException("Preview too large.");
                    bytes.Write(chunk, 0, read);
                }
                bytes.Position = 0;
                var bitmap = Bitmap.DecodeToWidth(bytes, 800);
                if (disposed || loggingOut || generation != accountGeneration) { bitmap.Dispose(); return; }
                previewCache.Add(url, bitmap);
                RenderDevices();
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
            catch { /* A missing thumbnail must never prevent remote control. */ }
        }
    }

    private async Task RefreshLocalScreensAsync()
    {
        if (loadingScreens || disposed) return;
        loadingScreens = true;
        try
        {
            localScreenItems.Clear(); localScreenLabels.Clear();
            foreach (var old in localBitmaps) old.Dispose(); localBitmaps.Clear();
            foreach (var share in outputs.Take(5).ToArray())
            {
                var screenIndex = outputs.Select((item, index) => (item, index)).First(x => x.item.Id == share.Id).index + 1;
                var tile = new LocalScreenModel {
                    Caption = $"显示屏 {screenIndex}" + (localScreenFps.TryGetValue(screenIndex, out var fps) ? $" · FPS {fps}" : ""),
                    OpenSettings = new AsyncCommand(() => { ShowPage(1); return Task.CompletedTask; }) };
                localScreenLabels[screenIndex] = tile;
                localScreenItems.Add(tile);
                if (!hostEnabled || Environment.GetEnvironmentVariable("UREMOTE_NO_AUTO_START") == "1") continue;
                CapturedScreen? frame = null;
                try
                {
                    frame = await DesktopBackend.CaptureAsync(share.Id, false, lifetime.Token);
                    if (disposed) return;
                    if (frame.ShmFormat is not (0 or 1)) continue;
                    var bitmap = new WriteableBitmap(new PixelSize(224, 126), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                    using (var target = bitmap.Lock())
                    {
                        var row = new byte[224 * 4];
                        for (var y = 0; y < 126; y++)
                        {
                            var sy = y * (int)frame.Height / 126;
                            if (frame.YInverted) sy = (int)frame.Height - 1 - sy;
                            for (var x = 0; x < 224; x++)
                            {
                                var at = sy * (int)frame.Stride + x * (int)frame.Width / 224 * 4;
                                Array.Copy(frame.Pixels, at, row, x * 4, 4); row[x * 4 + 3] = 255;
                            }
                            Marshal.Copy(row, 0, target.Address + y * target.RowBytes, row.Length);
                        }
                    }
                    localBitmaps.Add(bitmap); tile.Snapshot = bitmap;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                catch { tile.ShowSnapshot = false; }
                finally { if (frame is not null) Array.Clear(frame.Pixels); }
            }
        }
        finally { loadingScreens = false; }
    }

    private void UpdateScreenFps(string value)
    {
        var fields = value.Split(';').Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1]);
        if (!fields.TryGetValue("screen", out var screen) || !int.TryParse(screen, out var index)
            || !fields.TryGetValue("fps", out var fps)) return;
        var shared = outputs.Select((o, i) => (o, i)).Where(x => x.o.IsChecked == true).Select(x => x.i + 1).ToArray();
        if (index > 0 && index <= shared.Length) index = shared[index - 1];
        localScreenFps[index] = fps;
        if (localScreenLabels.TryGetValue(index, out var label)) label.Caption = $"显示屏 {index} · FPS {fps}";
    }

    private void ClearScreenFps()
    {
        localScreenFps.Clear();
        foreach (var (index, label) in localScreenLabels) label.Caption = $"显示屏 {index}";
    }
}
