namespace NetworkAccelerator.Core.Services;

public static class SingBoxTun
{
    public const string InterfaceName = "apphub-tun";

    // 避开本机系统 sing-box 常用的 172.19.0.1/30。
    public const string IPv4Address = "172.31.250.1/30";
    public const string IPv6Address = "fdfe:dcba:9876::1/126";

    public static IReadOnlyList<string> Addresses { get; } = [IPv4Address, IPv6Address];
}
