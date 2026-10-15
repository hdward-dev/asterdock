using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Controls.Shapes;

namespace VMCube.Module.Views;

internal static class VMCubeIcons
{
    public static Control Draw(string name, double size = 20, string brush = "AppAccentBrush")
    {
        var path = name switch {
            "cube" => "M12,2 L22,7 V17 L12,22 L2,17 V7 Z M4,8 L12,12 L20,8 M12,12 V20 M7,5 L17,10",
            "server" => "M3,3 H21 V10 H3 Z M3,14 H21 V21 H3 Z M6,6 H8 M6,17 H8 M12,6 H18 M12,17 H18",
            "connect" => "M3,4 H21 V17 H3 Z M8,21 H16 M12,17 V21 M8,10 H16 M13,7 L16,10 L13,13",
            "settings" => "M4,6 H20 M4,12 H20 M4,18 H20 M8,3 V9 M16,9 V15 M10,15 V21",
            "history" or "refresh" => "M4,8 A9,9 0 1 1 3,14 M4,3 V8 H9 M12,7 V12 L16,15",
            "star" or "star-filled" => "M12,3 L15,9 L22,10 L17,15 L18,22 L12,18 L6,22 L7,15 L2,10 L9,9 Z",
            "play" => "M7,4 L20,12 L7,20 Z",
            "pause" => "M6,4 H9 V20 H6 Z M15,4 H18 V20 H15 Z",
            "power" => "M12,2 V12 M6,5 A9,9 0 1 0 18,5",
            "stop" => "M5,5 H19 V19 H5 Z",
            "info" => "M12,2 A10,10 0 1 1 11.99,2 M12,10 V17 M12,6 V7",
            "fullscreen" => "M3,9 V3 H9 M15,3 H21 V9 M21,15 V21 H15 M9,21 H3 V15",
            "keyboard" => "M2,5 H22 V19 H2 Z M5,9 H7 M10,9 H12 M15,9 H19 M5,13 H7 M10,13 H12 M15,13 H19 M7,16 H17",
            "disconnect" => "M9,3 H3 V21 H9 M10,12 H22 M17,7 L22,12 L17,17",
            "wake" => "M12,6 A6,6 0 1 1 11.99,6 M12,1 V3 M12,21 V23 M1,12 H3 M21,12 H23 M4,4 L6,6 M18,18 L20,20 M4,20 L6,18 M18,6 L20,4",
            "eject" => "M4,16 L12,4 L20,16 Z M4,20 H20",
            _ => "M2,3 H22 V17 H2 Z M12,17 V21 M6,21 H18 M5,6 H19 V14 H5 Z" };
        var icon = new Avalonia.Controls.Shapes.Path { Width = size, Height = size, Data = Geometry.Parse(path), StrokeThickness = 1.5, Stretch = Stretch.Uniform };
        icon.Bind(Shape.StrokeProperty, new DynamicResourceExtension(brush));
        if (name == "star-filled") icon.Bind(Shape.FillProperty, new DynamicResourceExtension(brush));
        return icon;
    }

    public static Button Button(string tip, string icon, bool primary = false, bool danger = false)
    {
        var button = new Button { Content = Draw(icon, 18, primary ? "AppOnAccentBrush" : danger ? "AppDangerBrush" : "AppStrongTextBrush"),
            Width = 36, Height = 36, Padding = new Thickness(8), CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (primary) button.Classes.Add("ad-primary");
        ToolTip.SetTip(button, tip); AutomationProperties.SetName(button, tip);
        return button;
    }
}
