using System.Text.Json;

namespace VMCube.Module.Services;

/// <summary>Explicit hardware settings for an existing qcow2 guest; never executes a launch script.</summary>
public sealed record LocalVmImport(string Name, int Cpus, int MemoryMiB, string DiskPath,
    string? QemuPath = null, string? FirmwareCodePath = null, string? NvramPath = null,
    string? TpmStateDirectory = null, string? SwtpmPath = null, bool SecureBoot = false,
    bool CpuHost = false, bool RequireKvm = false, bool UseLocalTime = false,
    string NetworkModel = "e1000", string VideoModel = "std", string? MacAddress = null, string? GuestAgentSocket = null,
    string[]? UserNicHostForwards = null)
{
    public string Summary => $"{Name} · {Cpus} vCPU · {MemoryMiB / 1024d:0.#} GiB\n" +
        (FirmwareCodePath is null ? "BIOS" : SecureBoot ? "UEFI / Secure Boot" : "UEFI") +
        (TpmStateDirectory is null ? "" : " / TPM 2.0") + "\n磁盘：" + DiskPath;

    public static async Task<LocalVmImport> ReadProfileAsync(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length > 65536) throw new ArgumentException("导入配置文件不能超过 64 KiB。");
        var profile = JsonSerializer.Deserialize<LocalVmImport>(await File.ReadAllTextAsync(path, token),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
            ?? throw new ArgumentException("导入配置文件为空。");
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string? Resolve(string? value) => string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value, directory);
        return profile with { DiskPath = Resolve(profile.DiskPath) ?? "", QemuPath = Resolve(profile.QemuPath),
            FirmwareCodePath = Resolve(profile.FirmwareCodePath), NvramPath = Resolve(profile.NvramPath),
            TpmStateDirectory = Resolve(profile.TpmStateDirectory), SwtpmPath = Resolve(profile.SwtpmPath), GuestAgentSocket = Resolve(profile.GuestAgentSocket) };
    }

    public LocalVmImport Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 80 || Name.Any(char.IsControl) || Cpus is < 1 or > 64 || MemoryMiB is < 256 or > 262144)
            throw new ArgumentException("请输入有效的名称、CPU 数量和内存大小。");
        string Existing(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || !Path.IsPathFullyQualified(value) || !File.Exists(value))
                throw new ArgumentException("文件不存在或路径无效：" + value);
            return Path.GetFullPath(value);
        }
        if ((FirmwareCodePath is null) != (NvramPath is null)) throw new ArgumentException("UEFI 必须同时指定固件和原有 NVRAM 文件。");
        if (SecureBoot && FirmwareCodePath is null) throw new ArgumentException("安全启动需要 UEFI 固件和原有 NVRAM。");
        if ((TpmStateDirectory is null) != (SwtpmPath is null)) throw new ArgumentException("TPM 必须同时指定原有状态目录和 swtpm 程序。");
        if (TpmStateDirectory is not null)
        {
            if (!OperatingSystem.IsLinux()) throw new NotSupportedException("现有 swtpm 虚拟机目前仅支持在 Linux 上导入。");
            if (!Path.IsPathFullyQualified(TpmStateDirectory) || TpmStateDirectory.Any(char.IsControl) || TpmStateDirectory.Contains(',') ||
                !File.Exists(Path.Combine(TpmStateDirectory, "tpm2-00.permall")))
                throw new ArgumentException("请选择包含 tpm2-00.permall 的原有 TPM 2.0 状态目录；不会创建替代 TPM。");
        }
        if (RequireKvm && !OperatingSystem.IsLinux()) throw new NotSupportedException("此虚拟机要求 Linux KVM。");
        if (GuestAgentSocket is not null && (OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(GuestAgentSocket) ||
            GuestAgentSocket.Any(char.IsControl) || !Directory.Exists(Path.GetDirectoryName(GuestAgentSocket))))
            throw new ArgumentException("Guest Agent 需要有效的本机 Unix socket 路径。");
        if (NetworkModel is not ("e1000" or "e1000e" or "virtio-net-pci") || VideoModel is not ("std" or "qxl" or "virtio"))
            throw new ArgumentException("不支持此网卡或显卡型号。");
        if (MacAddress is not null && !System.Text.RegularExpressions.Regex.IsMatch(MacAddress, "\\A[0-9a-fA-F]{2}(:[0-9a-fA-F]{2}){5}\\z"))
            throw new ArgumentException("网卡 MAC 地址格式无效。");
        if (UserNicHostForwards is not null)
        {
            if (UserNicHostForwards.Length > 16) throw new ArgumentException("用户态网卡端口转发条目过多。");
            foreach (var forward in UserNicHostForwards)
            {
                if (string.IsNullOrWhiteSpace(forward) || forward.Length > 120 || forward.Any(char.IsControl) ||
                    forward.Contains(',') || forward.Contains(' ') ||
                    !System.Text.RegularExpressions.Regex.IsMatch(forward, "\\A(?:tcp|udp):(?:[0-9.]*:)?.[0-9]{1,5}-:[0-9]{1,5}\\z"))
                    throw new ArgumentException("用户态网卡端口转发格式无效，请使用如 tcp:127.0.0.1:8000-:8000。");
            }
        }
        return this with { Name = Name.Trim(), DiskPath = Existing(DiskPath), QemuPath = QemuPath is null ? null : Existing(QemuPath),
            FirmwareCodePath = FirmwareCodePath is null ? null : Existing(FirmwareCodePath), NvramPath = NvramPath is null ? null : Existing(NvramPath),
            SwtpmPath = SwtpmPath is null ? null : Existing(SwtpmPath), TpmStateDirectory = TpmStateDirectory is null ? null : Path.GetFullPath(TpmStateDirectory),
            UserNicHostForwards = UserNicHostForwards is null || UserNicHostForwards.Length == 0 ? null : UserNicHostForwards.ToArray() };
    }
}
