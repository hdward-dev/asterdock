using System.Globalization;

namespace NetworkAccelerator.Core.Services;

public static class TrafficFormatter
{
    public static string FormatRate(long bytesPerSecond)
    {
        SplitRate(bytesPerSecond, out var value, out var unit);
        return value + " " + unit;
    }

    public static void SplitRate(long bytesPerSecond, out string value, out string unit)
    {
        SplitScaled(bytesPerSecond, out value, out var scaled);
        unit = scaled + "/s";
    }

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
        SplitScaled(bytes, out var value, out var unit);
        return value + " " + unit;
    }

    private static void SplitScaled(long bytes, out string value, out string unit)
    {
        if (bytes < 0) bytes = 0;
        const double kilo = 1024d;
        if (bytes < kilo)
        {
            value = bytes.ToString(CultureInfo.InvariantCulture);
            unit = "B";
            return;
        }

        if (bytes < kilo * kilo)
        {
            value = (bytes / kilo).ToString("0.#", CultureInfo.InvariantCulture);
            unit = "KB";
            return;
        }

        if (bytes < kilo * kilo * kilo)
        {
            value = (bytes / kilo / kilo).ToString("0.#", CultureInfo.InvariantCulture);
            unit = "MB";
            return;
        }

        value = (bytes / kilo / kilo / kilo).ToString("0.#", CultureInfo.InvariantCulture);
        unit = "GB";
    }
}
