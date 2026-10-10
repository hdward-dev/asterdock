using System.Xml;
using System.Xml.Linq;
using System.Net;
namespace VMCube.Module.Services;

public sealed record ConsoleEndpoint(string Host, int Port)
{
    public static ConsoleEndpoint FromDomainXml(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        var graphics = XDocument.Load(reader).Root?.Element("devices")?.Elements("graphics").FirstOrDefault(x => (string?)x.Attribute("type") == "vnc");
        if (graphics is null) throw new NotSupportedException("此虚拟机未配置 VNC 控制台。请在 KVM 主机为虚拟机配置 VNC 图形设备；当前暂不支持 SPICE。");
        if (!string.IsNullOrEmpty((string?)graphics.Attribute("socket")) || graphics.Elements("listen").Any(x => (string?)x.Attribute("type") == "socket"))
            throw new NotSupportedException("此控制台使用 Unix socket，请将 VNC 配置为 TCP 监听后连接。");
        if (!int.TryParse((string?)graphics.Attribute("port"), out var port) || port is < 1 or > 65535)
            throw new InvalidOperationException("VNC 控制台尚未监听，请先启动或恢复虚拟机后重试。");
        var host = (string?)graphics.Attribute("listen") ?? (string?)graphics.Element("listen")?.Attribute("address") ?? "127.0.0.1";
        if (host is "0.0.0.0" or "::" or "") host = "127.0.0.1";
        if (!IPAddress.TryParse(host, out _) && Uri.CheckHostName(host) != UriHostNameType.Dns)
            throw new InvalidDataException("服务器返回了无效 VNC 监听地址。");
        return new(host, port);
    }
}
