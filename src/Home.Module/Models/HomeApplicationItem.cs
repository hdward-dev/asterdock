using AsterDock.Contracts;
using Avalonia.Media;

namespace Home.Module.Models;

public sealed class HomeApplicationItem
{
    private HomeApplicationItem()
    {
        Id = "load-new-application";
        Name = "加载新应用";
        Description = "添加应用目录或应用包";
        IconGeometry = Geometry.Parse("M12,5 V19 M5,12 H19");
        IconBackground = new SolidColorBrush(Color.Parse("#F4F6F8"));
        IconForeground = new SolidColorBrush(Color.Parse("#667085"));
        IsAddTile = true;
    }

    public HomeApplicationItem(ApplicationSummary application)
    {
        Id = application.Id;
        Name = application.Name;
        Description = application.Description;
        IconGeometry = Geometry.Parse(application.Icon switch
        {
            "printer" => "M6,3 H18 V8 H20 A2,2 0 0 1 22,10 V17 H18 V22 H6 V17 H2 V10 A2,2 0 0 1 4,8 H6 Z M8,15 V20 H16 V15 Z M8,5 V8 H16 V5 Z",
            "monitor" => "M3,4 H21 V17 H3 Z M5,6 V15 H19 V6 Z M9,20 H15 M12,17 V20",
            "home" => "M3,11 L12,3 L21,11 V21 H14 V15 H10 V21 H3 Z",
            "network" => "M4.9,9.4 A11.8,11.8 0 0 1 19.1,9.4 M7.8,13 A7.1,7.1 0 0 1 16.2,13 M10.5,16.3 A2.6,2.6 0 0 1 13.5,16.3 M12,20 L12,20.1",
            _ => "M4,4 H10 V10 H4 Z M14,4 H20 V10 H14 Z M4,14 H10 V20 H4 Z M14,14 H20 V20 H14 Z"
        });
        IconBackground = new SolidColorBrush(Color.Parse(application.Icon switch
        {
            "monitor" => "#E7F8FB",
            "network" => "#E9F7EF",
            _ => "#E8F0FE"
        }));
        IconForeground = new SolidColorBrush(Color.Parse(application.Icon switch
        {
            "monitor" => "#00A3BF",
            "network" => "#16A05D",
            _ => "#1267E8"
        }));
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public Geometry IconGeometry { get; }
    public IBrush IconBackground { get; }
    public IBrush IconForeground { get; }
    public bool IsAddTile { get; }
    public bool IsApplication => !IsAddTile;

    public static HomeApplicationItem CreateAddTile() => new();
}
