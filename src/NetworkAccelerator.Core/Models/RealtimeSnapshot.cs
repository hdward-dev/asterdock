namespace NetworkAccelerator.Core.Models;

public sealed record RealtimeSnapshot(
    long UploadRate,
    long DownloadRate,
    long UploadTotal,
    long DownloadTotal,
    IReadOnlyList<ConnectionRow> Live,
    IReadOnlyList<ConnectionRow> Recent,
    IReadOnlyList<ConnectionRow> Dns,
    int TcpCount,
    int UdpCount,
    IReadOnlyList<ProxyGroupInfo> Groups);
