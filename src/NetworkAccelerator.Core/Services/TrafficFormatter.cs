using System.Globalization;

namespace NetworkAccelerator.Core.Services;

public static class TrafficFormatter
{
    public static string FormatRate(long bytesPerSecond) => FormatScaled(bytesPerSecond) + "/s";

    public static string FormatTotal(long bytes) => FormatScaled(bytes);

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours} h {duration.Minutes} m";
        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes} m {duration.Seconds} s";
        return $"{(int)duration.TotalSeconds} s";
    }

    public static string FormatRelativeUpdate(DateTimeOffset updated, DateTimeOffset now)
    {
        var delta = now - updated;
        if (delta < TimeSpan.FromMinutes(1)) return "刚刚更新";
        if (delta < TimeSpan.FromHours(1)) return $"更新于 {(int)delta.TotalMinutes} 分钟前";
        if (delta < TimeSpan.FromDays(1)) return $"更新于 {(int)delta.TotalHours} 小时前";
        return $"更新于 {updated.ToLocalTime():MM-dd HH:mm}";
    }

    public static string DescribeSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "—";
        if (Uri.TryCreate(source.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return "远程";
        return "本地";
    }

    private static string FormatScaled(long bytes)
    {
        if (bytes < 0) bytes = 0;
        const double kilo = 1024d;
        if (bytes < kilo) return $"{bytes} B";
        if (bytes < kilo * kilo) return $"{(bytes / kilo).ToString("0.#", CultureInfo.InvariantCulture)} KB";
        if (bytes < kilo * kilo * kilo) return $"{(bytes / kilo / kilo).ToString("0.#", CultureInfo.InvariantCulture)} MB";
        return $"{(bytes / kilo / kilo / kilo).ToString("0.#", CultureInfo.InvariantCulture)} GB";
    }
}
