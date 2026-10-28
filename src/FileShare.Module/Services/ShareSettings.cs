using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileShareApp.Module.Services;

public sealed class ShareSettings : INotifyPropertyChanged
{
    private string _root = DefaultRoot;

    public static string DefaultRoot
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                var folderId = new Guid("374DE290-123F-4565-9164-39C4925E467B");
                if (SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out var path) == 0)
                {
                    try { return System.Runtime.InteropServices.Marshal.PtrToStringUni(path)!; }
                    finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem(path); }
                }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);
    private bool _passwordEnabled;
    private string _password = "";

    public string Root
    {
        get => _root;
        set { _root = value; OnPropertyChanged(); }
    }

    public int Port { get; set; } = 8080;

    public bool PasswordEnabled
    {
        get => _passwordEnabled;
        set { _passwordEnabled = value; OnPropertyChanged(); }
    }

    public string Password
    {
        get => _password;
        set { _password = value; OnPropertyChanged(); }
    }
    public bool ReadOnly { get; set; } = true;

    private bool _allowUpload;

    public bool AllowUpload
    {
        get => _allowUpload;
        set
        {
            if (_allowUpload == value && ReadOnly == !value) return;
            _allowUpload = value;
            ReadOnly = !value;
            OnPropertyChanged();
        }
    }
    public bool AllowZip { get; set; } = true;
    public string ServerName { get; set; } = "我的下载目录";

    private string _shareAddress = "";

    public string ShareAddress
    {
        get => _shareAddress;
        set { _shareAddress = value ?? ""; OnPropertyChanged(); }
    }

    [JsonIgnore]
    public string ActiveToken => PasswordEnabled ? Password.Trim() : "";

    public static ShareSettings Load(string path)
    {
        var settings = new ShareSettings();
        if (!File.Exists(path)) return settings;
        try
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<ShareSettings>(json, JsonOptions);
            if (loaded is not null) settings = loaded;
        }
        catch { }
        settings.Normalize();
        return settings;
    }

    public void Save(string path)
    {
        Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public void Normalize()
    {
        if (Port is < 1 or > 65535) Port = 8080;
        Password = Password.Trim();
        ShareAddress = ShareAddress.Trim();
        Root = string.IsNullOrWhiteSpace(Root) ? DefaultRoot : Root.Trim();
        ReadOnly = !AllowUpload;
    }

    public static string GeneratePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return string.Create(8, alphabet, static (buffer, source) =>
        {
            Span<byte> bytes = stackalloc byte[buffer.Length];
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            for (var i = 0; i < buffer.Length; i++) buffer[i] = source[bytes[i] % source.Length];
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null
    };
}
