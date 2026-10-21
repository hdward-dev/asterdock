using System.Security.Cryptography;

namespace NetworkAccelerator.Core.Services;

public sealed record LinuxTunLaunch(string Executable, bool ViaPkexec, string? PkexecPath)
{
    public static LinuxTunLaunch AsUser(string executable) => new(executable, false, null);

    public static LinuxTunLaunch WithPkexec(string executable, string pkexecPath) => new(executable, true, pkexecPath);
}

public sealed class LinuxTunPrivilege
{
    private readonly string _dataDirectory;
    private readonly IProcessRunner _runner;
    private readonly ILinuxCommandResolver _commands;
    private readonly ICapabilityXattrReader _xattr;
    private readonly IFileCapabilityPolicy _filesystem;

    public LinuxTunPrivilege(
        string dataDirectory,
        IProcessRunner processRunner,
        ILinuxCommandResolver commands,
        ICapabilityXattrReader xattr,
        IFileCapabilityPolicy? filesystem = null)
    {
        _dataDirectory = dataDirectory;
        _runner = processRunner;
        _commands = commands;
        _xattr = xattr;
        _filesystem = filesystem ?? new LinuxMountCapabilityPolicy();
    }

    public Func<IDisposable?>? BeginAuthorization { get; set; }

    public string CapableCopyPath => Path.Combine(_dataDirectory, "bin", "sing-box");

    public string HashSidecarPath => CapableCopyPath + ".sha256";

    public async Task<LinuxTunLaunch> PrepareAsync(string sourceExecutable, CancellationToken cancellationToken = default)
    {
        var sourceHash = await HashFileAsync(sourceExecutable, cancellationToken).ConfigureAwait(false);
        var copyPath = CapableCopyPath;
        var copyExists = File.Exists(copyPath);
        var recorded = copyExists ? ReadRecordedHash() : null;
        var hashMatches = recorded is not null && string.Equals(recorded, sourceHash, StringComparison.OrdinalIgnoreCase);
        var copyHasCap = copyExists && await HasNetAdminAsync(copyPath, cancellationToken).ConfigureAwait(false);
        var sourceHasCap = await HasNetAdminAsync(sourceExecutable, cancellationToken).ConfigureAwait(false);
        var pkexec = _commands.FindPkexec();
        var setcap = _commands.FindSetcap();
        var decision = LinuxTunAuthorization.Decide(new LinuxTunFacts(
            copyExists,
            copyHasCap,
            hashMatches,
            sourceHasCap,
            setcap is not null,
            pkexec is not null,
            _filesystem.HonorsFileCapabilities(copyPath)));

        switch (decision)
        {
            case LinuxTunDecision.UseCapableCopy:
                return LinuxTunLaunch.AsUser(copyPath);
            case LinuxTunDecision.UseSource:
                return LinuxTunLaunch.AsUser(sourceExecutable);
            case LinuxTunDecision.PkexecMissing:
                throw new TunAuthorizationException(TunAuthorizationException.PkexecMissingMessage, cancelled: false);
            case LinuxTunDecision.LaunchWithPkexec:
                return LinuxTunLaunch.WithPkexec(sourceExecutable, pkexec!);
            case LinuxTunDecision.CopyAndSetcap:
                CopyExecutable(sourceExecutable, copyPath);
                using (BeginScope())
                {
                    var result = await _runner.RunAsync(
                        new ProcessRequest(pkexec!, [setcap!, LinuxTunAuthorization.CapabilityArguments, copyPath]),
                        cancellationToken).ConfigureAwait(false);
                    EnsureSetcapSucceeded(result);
                }

                if (!await HasNetAdminAsync(copyPath, cancellationToken).ConfigureAwait(false))
                    throw new TunAuthorizationException(TunAuthorizationException.Failed("setcap 未生效"), cancelled: false);
                await File.WriteAllTextAsync(HashSidecarPath, sourceHash, cancellationToken).ConfigureAwait(false);
                return LinuxTunLaunch.AsUser(copyPath);
            default:
                throw new InvalidOperationException("未知的 TUN 授权决定。");
        }
    }

    public async Task<bool> HasNetAdminAsync(string path, CancellationToken cancellationToken = default)
    {
        var getcap = _commands.FindGetcap();
        if (getcap is not null)
        {
            var result = await _runner.RunAsync(new ProcessRequest(getcap, [path]), cancellationToken).ConfigureAwait(false);
            if (result.Started)
                return LinuxTunAuthorization.OutputHasNetAdmin(result.StandardOutput)
                       || LinuxTunAuthorization.OutputHasNetAdmin(result.StandardError);
        }

        var xattr = _xattr.ReadSecurityCapability(path);
        return xattr is not null && LinuxTunAuthorization.XattrHasNetAdmin(xattr);
    }

    private void EnsureSetcapSucceeded(ProcessResult result)
    {
        var outcome = LinuxTunAuthorization.ClassifyPkexec(result.ExitCode, result.StandardError, result.Started);
        if (outcome == LinuxPkexecOutcome.Cancelled)
            throw new TunAuthorizationException(TunAuthorizationException.CancelledMessage, cancelled: true);
        if (outcome != LinuxPkexecOutcome.Success)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new TunAuthorizationException(TunAuthorizationException.Failed(detail), cancelled: false);
        }
    }

    private IDisposable BeginScope() => BeginAuthorization?.Invoke() ?? EmptyDisposable.Instance;

    private void CopyExecutable(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp";
        File.Copy(source, temporary, overwrite: true);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(
                temporary,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        File.Move(temporary, destination, overwrite: true);
    }

    private string? ReadRecordedHash()
    {
        try
        {
            return File.Exists(HashSidecarPath) ? File.ReadAllText(HashSidecarPath).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
