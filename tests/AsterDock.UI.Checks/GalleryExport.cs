using AsterDock.UI.Controls;
using AsterDock.UI.Motion;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;

namespace AsterDock.UI.Checks;

internal static class GalleryExport
{
    public static void ExportStills(string directory)
    {
        foreach (var dark in new[] { false, true })
        {
            var suffix = dark ? "dark" : "light";
            Save(Indicators(), Path.Combine(directory, $"loading_indicators_{suffix}.png"), 1080, 420, dark, SpinnerMotion.PeakAt);
            Save(CardOverlay(), Path.Combine(directory, $"loading_overlay_card_{suffix}.png"), 880, 480, dark, SpinnerMotion.PeakAt);
            Save(WindowOverlay(), Path.Combine(directory, $"loading_window_overlay_{suffix}.png"), 1080, 640, dark, SpinnerMotion.PeakAt);
            Save(Skeletons(), Path.Combine(directory, $"loading_skeleton_{suffix}.png"), 880, 460, dark, TimeSpan.FromMilliseconds(630));
        }
    }

    public static void ExportVideo(string directory)
    {
        const int width = 960;
        const int height = 540;
        const int fps = 16;
        const double seconds = 7.5;
        var frames = (int)(seconds * fps);
        var frameDirectory = Path.Combine(directory, "frames");
        if (Directory.Exists(frameDirectory)) Directory.Delete(frameDirectory, recursive: true);
        Directory.CreateDirectory(frameDirectory);

        ApplyTheme(false);
        var overlay = new AdLoadingOverlay
        {
            DelayMs = 0,
            MinimumVisibleMs = 0,
            FollowContentCornerRadius = true,
            Text = "正在读取设备…",
            IndicatorSize = AdLoadingSize.Medium,
            Content = SampleCard()
        };
        var root = new Grid
        {
            Margin = new Thickness(36),
            ColumnDefinitions = new ColumnDefinitions("*,16,*"),
            RowDefinitions = new RowDefinitions("Auto,*,Auto")
        };
        root.Children.Add(Title("加载动效"));
        Grid.SetColumnSpan(root.Children[0], 3);
        var indicator = new AdLoadingIndicator { Size = AdLoadingSize.Large, Text = "加载中…", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var indicatorHost = new StackPanel
        {
            Spacing = 18,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                indicator,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 28,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new AdLoadingIndicator { Size = AdLoadingSize.Small },
                        new AdLoadingIndicator { Size = AdLoadingSize.Medium }
                    }
                }
            }
        };
        Grid.SetRow(indicatorHost, 1);
        root.Children.Add(indicatorHost);
        Grid.SetColumn(overlay, 2);
        Grid.SetRow(overlay, 1);
        root.Children.Add(overlay);
        var skeletonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Margin = new Thickness(0, 18, 0, 0),
            Children =
            {
                new AdSkeleton { Kind = AdSkeletonKind.Circle, Width = 44, Height = 44 },
                new AdSkeleton { Kind = AdSkeletonKind.Line, Width = 220, Height = 12, VerticalAlignment = VerticalAlignment.Center },
                new AdSkeleton { Kind = AdSkeletonKind.Rectangle, Width = 180, Height = 44, ShapeCornerRadius = 10 }
            }
        };
        Grid.SetRow(skeletonRow, 2);
        Grid.SetColumnSpan(skeletonRow, 3);
        root.Children.Add(skeletonRow);

        var window = Show(root, width, height);
        for (var frame = 0; frame < frames; frame++)
        {
            var time = TimeSpan.FromSeconds(frame / (double)fps);
            LoadingClock.Freeze(time);
            if (time >= TimeSpan.FromSeconds(4.8) && !overlay.IsLoading) overlay.IsLoading = true;
            LoadingClock.TickAll();
            window.UpdateLayout();
            SaveBitmap(window, Path.Combine(frameDirectory, $"frame_{frame:0000}.png"), width, height);
        }

        window.Close();
        LoadingClock.Resume();
        Encode(frameDirectory, Path.Combine(directory, "loading_spinner_and_overlay.mp4"), fps);
    }

    private static Control Indicators()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 28,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Labeled("小 · 20", new AdLoadingIndicator { Size = AdLoadingSize.Small }),
                Labeled("中 · 36", new AdLoadingIndicator { Size = AdLoadingSize.Medium, Text = "加载中…" }),
                Labeled("大 · 56", new AdLoadingIndicator { Size = AdLoadingSize.Large, Text = "加载中…" }),
                Labeled("确定进度", new AdLoadingIndicator { Size = AdLoadingSize.Large, Progress = 0.62, Text = "62%" })
            }
        };
        return Page("加载指示", row);
    }

    private static Control CardOverlay()
    {
        var overlay = new AdLoadingOverlay
        {
            DelayMs = 0,
            MinimumVisibleMs = 0,
            IsLoading = true,
            Text = "正在读取设备…",
            IndicatorSize = AdLoadingSize.Medium,
            Content = SampleCard()
        };
        return Page("区域加载", overlay);
    }

    private static Control WindowOverlay()
    {
        var page = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = "轻应用", FontSize = 28, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "正在准备可以打开的工具。", Classes = { "ad-muted" } },
                SampleCard(),
                SampleCard()
            }
        };
        return new AdLoadingOverlay
        {
            DelayMs = 0,
            MinimumVisibleMs = 0,
            IsLoading = true,
            IsCancellable = true,
            FollowContentCornerRadius = false,
            UseBlur = false,
            IndicatorSize = AdLoadingSize.Large,
            Text = "正在打开轻应用…",
            Content = new Border { Padding = new Thickness(36), Child = page }
        };
    }

    private static Control Skeletons()
    {
        var list = new StackPanel { Spacing = 16 };
        for (var i = 0; i < 3; i++)
        {
            list.Children.Add(new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("44,*"),
                Children =
                {
                    new AdSkeleton { Kind = AdSkeletonKind.Circle, Width = 44, Height = 44 },
                    new StackPanel
                    {
                        Margin = new Thickness(12, 4, 0, 0),
                        Spacing = 8,
                        Children =
                        {
                            new AdSkeleton { Kind = AdSkeletonKind.Line, Height = 14 },
                            new AdSkeleton { Kind = AdSkeletonKind.Line, Width = 180, Height = 10 }
                        }
                    }
                }
            });
            Grid.SetColumn(list.Children[^1] is Grid grid ? grid.Children[1] : list.Children[^1], 1);
        }

        var block = new AdSkeleton { Kind = AdSkeletonKind.Rectangle, Height = 120, ShapeCornerRadius = 12 };
        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,20,*"),
            Children = { Card(list), block }
        };
        Grid.SetColumn(block, 2);
        return Page("骨架占位", body);
    }

    private static Control SampleCard()
    {
        var body = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "会议室显示屏", FontSize = 18, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "192.168.1.24 · 正在建立画面通道", Classes = { "ad-muted" }, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new Border { Classes = { "ad-card" }, Padding = new Thickness(10, 6), Child = new TextBlock { Text = "桌面" } },
                        new Border { Classes = { "ad-card" }, Padding = new Thickness(10, 6), Child = new TextBlock { Text = "文件" } }
                    }
                }
            }
        };
        return Card(body);
    }

    private static Control Page(string title, Control body)
    {
        var panel = new DockPanel { Margin = new Thickness(28) };
        var heading = Title(title);
        DockPanel.SetDock(heading, Dock.Top);
        panel.Children.Add(heading);
        body.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(body);
        return panel;
    }

    private static TextBlock Title(string text) => new()
    {
        Text = text,
        FontSize = 22,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 0, 0, 16)
    };

    private static Border Card(Control child) => new()
    {
        Classes = { "ad-card" },
        Padding = new Thickness(20),
        Child = child
    };

    private static StackPanel Labeled(string caption, Control control) => new()
    {
        Spacing = 10,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            control,
            new TextBlock
            {
                Text = caption,
                Classes = { "ad-muted" },
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            }
        }
    };

    private static void Save(Control content, string path, int width, int height, bool dark, TimeSpan time)
    {
        LoadingClock.Freeze(time);
        var window = Show(content, width, height, dark);
        window.UpdateLayout();
        LoadingClock.TickAll();
        SaveBitmap(window, path, width, height);
        window.Close();
        LoadingClock.Resume();
    }

    private static Window Show(Control content, int width, int height, bool dark = false)
    {
        ApplyTheme(dark);
        var window = new Window
        {
            Width = width,
            Height = height,
            Content = content,
            Background = new SolidColorBrush(dark ? Color.Parse("#111318") : Color.Parse("#F5F7FA")),
            WindowDecorations = WindowDecorations.None,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };
        window.Show();
        return window;
    }

    private static void ApplyTheme(bool dark)
    {
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    private static void SaveBitmap(Window window, string path, int width, int height)
    {
        window.Width = width;
        window.Height = height;
        window.UpdateLayout();
        var pixelSize = new PixelSize(width, height);
        using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
        var length = new FileInfo(path).Length;
        if (length < 1000)
            throw new InvalidOperationException(
                $"rendered frame is empty ({length} bytes, bounds {window.Bounds}, client {window.ClientSize}): {path}");
    }

    private static void Encode(string frameDirectory, string output, int fps)
    {
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        };
        process.StartInfo.ArgumentList.Add("-y");
        process.StartInfo.ArgumentList.Add("-framerate");
        process.StartInfo.ArgumentList.Add(fps.ToString());
        process.StartInfo.ArgumentList.Add("-i");
        process.StartInfo.ArgumentList.Add(Path.Combine(frameDirectory, "frame_%04d.png"));
        process.StartInfo.ArgumentList.Add("-c:v");
        process.StartInfo.ArgumentList.Add("libx264");
        process.StartInfo.ArgumentList.Add("-pix_fmt");
        process.StartInfo.ArgumentList.Add("yuv420p");
        process.StartInfo.ArgumentList.Add("-crf");
        process.StartInfo.ArgumentList.Add("16");
        process.StartInfo.ArgumentList.Add("-movflags");
        process.StartInfo.ArgumentList.Add("+faststart");
        process.StartInfo.ArgumentList.Add(output);
        if (!process.Start()) throw new InvalidOperationException("无法启动 ffmpeg");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
    }
}
