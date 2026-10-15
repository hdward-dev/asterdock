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
    private readonly Dictionary<string, (Image Image, Control Placeholder, Border Badge, TextBlock Caption)> _previewTargets = [];
    private string PreviewKey(VirtualMachine vm) => (_demo ? "demo" : _connectedUri ?? (IsLocal ? "local" : _uri.Text ?? "remote")) + "/" + vm.Id;

    private void AddDesktopPreview(Grid container, Control placeholder, VirtualMachine vm)
    {
        var key = PreviewKey(vm);
        var image = new Image { Stretch = Stretch.Uniform, IsVisible = false };
        var caption = new TextBlock { FontSize = 10, Foreground = Brushes.White };
        var badge = new Border { Child = caption, IsVisible = false, Background = new SolidColorBrush(Color.Parse("#CC151D23")),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 3), Margin = new Thickness(6),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        container.Children.Add(image); container.Children.Add(badge);
        _previewTargets[key] = (image, placeholder, badge, caption);
        ApplyPreview(key);
    }
    private void ApplyPreview(string key)
    {
        if (!_previewTargets.TryGetValue(key, out var target)) return;
        var hasFrame = _previews.TryGetValue(key, out var preview) && preview.Bitmap is not null;
        target.Image.Source = hasFrame ? preview!.Bitmap : null;
        target.Image.IsVisible = hasFrame; target.Placeholder.IsVisible = !hasFrame; target.Badge.IsVisible = hasFrame;
        if (hasFrame) target.Caption.Text = (preview!.Connected ? "实时预览" : "最近画面") + " · " + preview.UpdatedAt.ToLocalTime().ToString("HH:mm:ss");
        target.Image.InvalidateVisual();
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
        foreach (var target in _previewTargets.Values) target.Image.Source = null;
        _previewTargets.Clear();
        foreach (var preview in _previews.Values) preview.Dispose();
        _previews.Clear();
    }
}
