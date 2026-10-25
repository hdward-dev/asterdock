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

    public static IReadOnlyList<string> GetLanAddresses()
    {
        var result = new List<string>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var address in adapter.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var text = address.Address.ToString();
                if (!text.StartsWith("169.254.") && !result.Contains(text)) result.Add(text);
            }
        }
        return result;
    }
}
