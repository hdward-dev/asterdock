using NetworkAccelerator.Core.Models;
using System.Text.Json;

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
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<NetworkAcceleratorSettings>(stream, JsonOptions, cancellationToken)
                       .ConfigureAwait(false)
                   ?? new NetworkAcceleratorSettings();
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
