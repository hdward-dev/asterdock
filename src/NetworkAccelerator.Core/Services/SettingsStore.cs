using NetworkAccelerator.Core.Models;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetworkAccelerator.Core.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public SettingsStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "settings.json");
    }

    public async Task<NetworkAcceleratorSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return new NetworkAcceleratorSettings();
        try
        {
            var json = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
            var settings = JsonSerializer.Deserialize<NetworkAcceleratorSettings>(json, JsonOptions)
                           ?? new NetworkAcceleratorSettings();
            if (JsonNode.Parse(json) is JsonObject document
                && !document.ContainsKey(nameof(NetworkAcceleratorSettings.SystemProxyEnabled)))
                settings.SystemProxyEnabled = !settings.TunEnabled;
            return settings;
        }
        catch (JsonException)
        {
            return new NetworkAcceleratorSettings();
        }
    }

    public async Task SaveAsync(NetworkAcceleratorSettings settings, CancellationToken cancellationToken = default)
    {
        var temporaryPath = _path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, _path, overwrite: true);
    }
}
