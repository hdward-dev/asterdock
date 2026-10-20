using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using VMCube.Module.Services;
using URemote.Core;

namespace VMCube.Module.Views;

public sealed partial class VMCubeView
{
    private sealed class DesktopPreview : IDisposable
    {
        public WriteableBitmap? Bitmap;
        public DateTimeOffset UpdatedAt;
        public bool Connected;
        public void Dispose() => Bitmap?.Dispose();
    }
    private readonly Dictionary<string, DesktopPreview> _previews = [];
    private readonly Dictionary<string, MachineCard> _previewTargets = [];
    private string PreviewKey(VirtualMachine vm) => (_demo ? "demo" : _connectedUri ?? (IsLocal ? "local" : _uri.Text ?? "remote")) + "/" + vm.Id;

    private void RegisterPreview(MachineCard card, VirtualMachine vm)
    {
        _previewTargets[PreviewKey(vm)] = card;
        ApplyPreview(PreviewKey(vm));
    }
    private void ApplyPreview(string key)
    {
        if (!_previewTargets.TryGetValue(key, out var card)) return;
        var hasFrame = _previews.TryGetValue(key, out var preview) && preview.Bitmap is not null;
        card.ShowFrame(hasFrame ? preview!.Bitmap : null,
            hasFrame ? (preview!.Connected ? "实时预览" : "最近画面") + " · " + preview.UpdatedAt.ToLocalTime().ToString("HH:mm:ss") : "");
    }
    private void UpdatePreview(string key, LocalDesktopFrame frame)
    {
        if (_disposed || frame.Width < 1 || frame.Height < 1) return;
        var now = DateTimeOffset.UtcNow;
        if (_previews.TryGetValue(key, out var cached) && cached.Connected && now - cached.UpdatedAt < TimeSpan.FromMilliseconds(750)) return;
        if (cached is null)
        {
            if (_previews.Count >= 32)
            {
                var oldest = _previews.OrderBy(x => x.Value.Connected).ThenBy(x => x.Value.UpdatedAt).First();
                _previews.Remove(oldest.Key); ApplyPreview(oldest.Key); oldest.Value.Dispose();
            }
            cached = new DesktopPreview(); _previews.Add(key, cached);
        }
        var scale = Math.Min(1, Math.Min(320d / frame.Width, 200d / frame.Height));
        var width = Math.Max(1, (int)(frame.Width * scale)); var height = Math.Max(1, (int)(frame.Height * scale));
        var previous = cached.Bitmap;
        if (previous is null || previous.PixelSize != new PixelSize(width, height))
            cached.Bitmap = new WriteableBitmap(new(width, height), new(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var buffer = cached.Bitmap!.Lock())
        {
            var row = new byte[width * 4];
            for (var y = 0; y < height; y++)
            {
                var sourceY = y * frame.Height / height;
                for (var x = 0; x < width; x++)
                    frame.Pixels.AsSpan((sourceY * frame.Width + x * frame.Width / width) * 4, 4).CopyTo(row.AsSpan(x * 4, 4));
                Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, row.Length);
            }
        }
        cached.Connected = true; cached.UpdatedAt = now; ApplyPreview(key);
        if (!ReferenceEquals(previous, cached.Bitmap)) previous?.Dispose();
    }
    private void PreviewDisconnected(string key)
    {
        if (_previews.TryGetValue(key, out var preview)) { preview.Connected = false; ApplyPreview(key); }
    }
    private void ClearPreviews()
    {
        foreach (var card in _previewTargets.Values) card.ShowFrame(null, "");
        _previewTargets.Clear();
        foreach (var preview in _previews.Values) preview.Dispose();
        _previews.Clear();
    }
}
