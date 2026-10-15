using System.Xml;
using System.Xml.Linq;
using System.Net;
namespace VMCube.Module.Services;

public enum ConsoleProtocol { Vnc, Spice }
public sealed record ConsoleEndpoint(string Host, int Port, ConsoleProtocol Protocol = ConsoleProtocol.Vnc, bool UseTls = false)
{
    public static ConsoleEndpoint FromDomainXml(string xml, ConsoleProtocol? preferred = null)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        var graphics = XDocument.Load(reader).Root?.Element("devices")?.Elements("graphics")
            .Where(x => (string?)x.Attribute("type") is "vnc" or "spice")
            .Where(x => preferred is null || (string?)x.Attribute("type") == (preferred == ConsoleProtocol.Vnc ? "vnc" : "spice"))
            .OrderBy(x => (string?)x.Attribute("type") == (preferred == ConsoleProtocol.Vnc ? "vnc" : "spice") ? 0 : 1).FirstOrDefault();
        if (graphics is null) throw new NotSupportedException("此虚拟机未配置 VNC/SPICE 控制台，请在 KVM 主机为虚拟机配置图形设备。");
        var protocol = (string?)graphics.Attribute("type") == "spice" ? ConsoleProtocol.Spice : ConsoleProtocol.Vnc;
        if (!string.IsNullOrEmpty((string?)graphics.Attribute("socket")) || graphics.Elements("listen").Any(x => (string?)x.Attribute("type") == "socket"))
            throw new NotSupportedException("此控制台使用 Unix socket，请将图形设备配置为 TCP 监听后连接。");
        int.TryParse((string?)graphics.Attribute("port"), out var port);
        var tls = false;
        if (port is < 1 or > 65535)
        {
            if (protocol == ConsoleProtocol.Spice && int.TryParse((string?)graphics.Attribute("tlsPort"), out port) && port is > 0 and <= 65535) tls = true;
            else throw new InvalidOperationException("图形控制台尚未监听，请先启动或恢复虚拟机后重试。");
        }
        var host = (string?)graphics.Element("listen")?.Attribute("address") ?? (string?)graphics.Attribute("listen") ?? "127.0.0.1";
        if (host is "0.0.0.0" or "::" or "") host = "127.0.0.1";
        if (!IPAddress.TryParse(host, out _) && Uri.CheckHostName(host) != UriHostNameType.Dns)
            throw new InvalidDataException("服务器返回了无效控制台监听地址。");
        return new(host, port, protocol, tls);
    }
}
