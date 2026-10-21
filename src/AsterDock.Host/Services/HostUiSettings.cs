using System.Text.Json;

namespace AsterDock.Host.Services;

internal sealed class HostUiSettings
{
    public bool ReduceMotion { get; set; }

    public bool AllowSystemNotifications { get; set; } = true;

    public static HostUiSettings Load()
    {
        try
        {
            var path = SettingsPath();
            if (path is null || !File.Exists(path)) return new HostUiSettings();
            return JsonSerializer.Deserialize<HostUiSettings>(File.ReadAllText(path)) ?? new HostUiSettings();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new HostUiSettings();
        }
    }

    public void Save()
    {
        var path = SettingsPath();
        if (path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string? SettingsPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "AsterDock", "ui-settings.json");
    }
}
