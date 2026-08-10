using AsterDock.Contracts;
using Avalonia.Media;

namespace Home.Module.Models;

public sealed class RecentApplicationItem
{
    private readonly DateTimeOffset _lastOpenedAt;

    public RecentApplicationItem(RecentApplication recent)
    {
        Id = recent.Application.Id;
        Name = recent.Application.Name;
        _lastOpenedAt = recent.LastOpenedAt;
        IconGeometry = new HomeApplicationItem(recent.Application).IconGeometry;
    }

    public string Id { get; }
    public string Name { get; }
    public Geometry IconGeometry { get; }
    public string RelativeTime => FormatRelativeTime(_lastOpenedAt);

    private static string FormatRelativeTime(DateTimeOffset timestamp)
    {
        var elapsed = DateTimeOffset.Now - timestamp;
        if (elapsed < TimeSpan.FromMinutes(1)) return "刚刚";
        if (elapsed < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)elapsed.TotalMinutes)} 分钟前";
        if (timestamp.Date == DateTimeOffset.Now.Date) return $"今天 {timestamp:HH:mm}";
        if (timestamp.Date == DateTimeOffset.Now.Date.AddDays(-1)) return $"昨天 {timestamp:HH:mm}";
        return timestamp.ToString("MM-dd HH:mm");
    }
}
