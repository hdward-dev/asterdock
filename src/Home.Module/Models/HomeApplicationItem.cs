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
        var icon = application.Id switch
        {
            "android-screen" => "phone",
            "u-remote" => "remote",
            _ => application.Icon
        };
        IconGeometry = Geometry.Parse(icon switch
        {
            "phone" => "M7,2 H17 V22 H7 Z M9,4 V18 H15 V4 Z M11,19 H13 V21 H11 Z",
            "serial" => "M2,4 H22 V20 H2 Z M4,6 V18 H20 V6 Z M7,8 L11,12 L7,16 L6,15 L9,12 L6,9 Z M13,14 H18 V16 H13 Z",
            "remote" => "M2,3 H17 V6 H15 V5 H4 V15 H11 V17 H9 V19 H12 V21 H5 V19 H7 V17 H2 Z M13,8 H22 V22 H13 Z M15,10 V19 H20 V10 Z",
            "printer" => "M6,3 H18 V8 H20 A2,2 0 0 1 22,10 V17 H18 V22 H6 V17 H2 V10 A2,2 0 0 1 4,8 H6 Z M8,15 V20 H16 V15 Z M8,5 V8 H16 V5 Z",
            "monitor" => "M3,4 H21 V17 H13 V20 H17 V22 H7 V20 H11 V17 H3 Z M5,6 V15 H19 V6 Z",
            "home" => "M3,11 L12,3 L21,11 V21 H14 V15 H10 V21 H3 Z",
            "network" => "M14,1 L3,14 H10 L8,23 L21,9 H13 Z",
            _ => "M4,4 H10 V10 H4 Z M14,4 H20 V10 H14 Z M4,14 H10 V20 H4 Z M14,14 H20 V20 H14 Z"
        });
        IconBackground = new SolidColorBrush(Color.Parse(icon switch
        {
            "phone" => "#F0E7FC",
            "serial" => "#FFF1D1",
            "monitor" => "#E7F8FB",
            "network" => "#E9F7EF",
            _ => "#E8F0FE"
        }));
        IconForeground = new SolidColorBrush(Color.Parse(icon switch
        {
            "phone" => "#9556E9",
            "serial" => "#E89B00",
            "monitor" => "#00A3BF",
            "network" => "#16A05D",
            _ => "#1267E8"
        }));
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public string ShortDescription => Id switch
    {
        "invoice-printer" => "导入、排版与打印",
        "device-information" => "查看硬件与运行状态",
        "network-accelerator" => "连接更快一步",
        "android-screen" => "连接手机，轻松操控",
        "serial-debugger" => "收发数据，高效联调",
        "u-remote" => "随时连接远程设备",
        _ => Description
    };
    public Geometry IconGeometry { get; }
    public IBrush IconBackground { get; }
    public IBrush IconForeground { get; }
    public bool IsAddTile { get; }
    public bool IsApplication => !IsAddTile;

    public static HomeApplicationItem CreateAddTile() => new();
}
