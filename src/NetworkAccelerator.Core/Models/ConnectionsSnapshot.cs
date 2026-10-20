namespace NetworkAccelerator.Core.Models;

public sealed record ConnectionsSnapshot(
    long UploadTotal,
    long DownloadTotal,
    IReadOnlyList<ConnectionRow> Connections);
