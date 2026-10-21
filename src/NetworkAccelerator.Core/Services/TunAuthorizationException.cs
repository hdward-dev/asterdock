namespace NetworkAccelerator.Core.Services;

public sealed class TunAuthorizationException : Exception
{
    public const string CancelledMessage = "已取消授权，虚拟网卡未开启。";
    public const string PkexecMissingMessage = "未找到 pkexec，无法授予 TUN 权限。";

    public TunAuthorizationException(string message, bool cancelled)
        : base(message) =>
        Cancelled = cancelled;

    public bool Cancelled { get; }

    public static string Failed(string? detail)
    {
        var text = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim();
        if (text is null) return "授予 TUN 权限失败。";
        var line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(line)) return "授予 TUN 权限失败。";
        if (line.Length > 180) line = line[..180];
        return $"授予 TUN 权限失败：{line}";
    }
}
