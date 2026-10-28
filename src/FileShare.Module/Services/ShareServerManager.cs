using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using FileShare;

namespace FileShareApp.Module.Services;

public sealed class ShareServerManager
{
    private readonly string _settingsPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IAsyncDisposable? _application;
    private ShareSettings? _settings;

    public event Action? StateChanged;

    public ShareServerManager(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _settingsPath = Path.Combine(dataDirectory, "settings.json");
    }

    public ShareSettings Load() => _settings ??= ShareSettings.Load(_settingsPath);

    public void Save(ShareSettings settings) => settings.Save(_settingsPath);

    public bool IsRunning => _application is not null;

    public async Task StartAsync(ShareSettings settings, CancellationToken cancellationToken)
    {
        settings.Normalize();
        if (!Directory.Exists(settings.Root)) throw new InvalidOperationException("共享目录不存在。");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_application is not null) await StopCoreAsync().ConfigureAwait(false);

            try
            {
                _application = await ShareHost.StartAsync(new ShareHostOptions
                {
                    Root = settings.Root,
                    Port = settings.Port,
                    Token = settings.ActiveToken,
                    ReadOnly = settings.ReadOnly,
                    AllowUpload = settings.AllowUpload,
                    AllowZip = settings.AllowZip,
                    ServerName = settings.ServerName
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"共享服务未能启动：{ex.Message}", ex);
            }
        }
        finally
        {
            _gate.Release();
            StateChanged?.Invoke();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally
        {
            _gate.Release();
            StateChanged?.Invoke();
        }
    }

    private async Task StopCoreAsync()
    {
        var application = _application;
        _application = null;
        if (application is null) return;
        await application.DisposeAsync().ConfigureAwait(false);
    }

    public static IReadOnlyList<LanAddressChoice> GetLanAddressChoices()
    {
        var result = new List<LanAddressChoice>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            IPInterfaceProperties properties;
            try { properties = adapter.GetIPProperties(); }
            catch { continue; }

            var hasGateway = false;
            try
            {
                hasGateway = properties.GatewayAddresses.Any(gateway =>
                    gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any));
            }
            catch { }

            foreach (var address in properties.UnicastAddresses)
            {
                if (!IsSelectableLanAddress(address.Address)) continue;
                var text = address.Address.ToString();
                if (result.Any(item => string.Equals(item.Address, text, StringComparison.OrdinalIgnoreCase))) continue;
                result.Add(new LanAddressChoice
                {
                    Address = text,
                    Adapter = string.IsNullOrWhiteSpace(adapter.Name) ? adapter.Description : adapter.Name,
                    HasGateway = hasGateway
                });
            }
        }

        return result
            .OrderByDescending(item => IsPrivateLan(item.Address))
            .ThenBy(item => IsBenchmarkRange(item.Address))
            .ThenByDescending(item => item.HasGateway)
            .ThenBy(item => item.Adapter, StringComparer.CurrentCulture)
            .ToList();
    }

    public static LanAddressChoice? ChooseDefault(IReadOnlyList<LanAddressChoice> choices, string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var saved = choices.FirstOrDefault(choice => string.Equals(choice.Address, preferred, StringComparison.OrdinalIgnoreCase));
            if (saved is not null) return saved;
        }

        return choices
            .OrderByDescending(choice => IsPrivateLan(choice.Address))
            .ThenByDescending(choice => choice.HasGateway)
            .ThenBy(choice => IsBenchmarkRange(choice.Address))
            .ThenBy(choice => choice.Address, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public static bool IsPrivateLan(string address)
    {
        if (!TryGetIPv4(address, out var bytes)) return false;
        if (bytes[0] == 10) return true;
        if (bytes[0] == 192 && bytes[1] == 168) return true;
        return bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
    }

    public static bool IsBenchmarkRange(string address)
    {
        if (!TryGetIPv4(address, out var bytes)) return false;
        return bytes[0] == 198 && bytes[1] is 18 or 19;
    }

    private static bool IsSelectableLanAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (bytes[0] == 0 || bytes[0] >= 224) return false;
        return bytes[0] != 169 || bytes[1] != 254;
    }

    private static bool TryGetIPv4(string address, out byte[] bytes)
    {
        bytes = [];
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        bytes = ip.GetAddressBytes();
        return true;
    }
}

public sealed class LanAddressChoice : IEquatable<LanAddressChoice>
{
    public required string Address { get; init; }
    public required string Adapter { get; init; }
    public bool HasGateway { get; init; }
    public override string ToString() => $"{Address} · {Adapter}";
    public bool Equals(LanAddressChoice? other) => other is not null && string.Equals(Address, other.Address, StringComparison.OrdinalIgnoreCase);
    public override bool Equals(object? obj) => obj is LanAddressChoice other && Equals(other);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Address);
}
