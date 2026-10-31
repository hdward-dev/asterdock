using System.Diagnostics;
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
    private readonly Func<string, CancellationToken, Task<bool>>? _canRun;

    public LinuxTunPrivilege(
        string dataDirectory,
        IProcessRunner processRunner,
        ILinuxCommandResolver commands,
        ICapabilityXattrReader xattr,
        IFileCapabilityPolicy? filesystem = null,
        Func<string, CancellationToken, Task<bool>>? canRun = null)
    {
        _dataDirectory = dataDirectory;
        _runner = processRunner;
        _commands = commands;
        _xattr = xattr;
        _filesystem = filesystem ?? new LinuxMountCapabilityPolicy();
        _canRun = canRun;
    }

    public Func<IDisposable?>? BeginAuthorization { get; set; }

    public string CapableCopyPath => Path.Combine(_dataDirectory, "bin", "sing-box");

    public string HashSidecarPath => CapableCopyPath + ".sha256";

    public async Task<LinuxTunLaunch> PrepareAsync(string sourceExecutable, CancellationToken cancellationToken = default)
    {
        var canHold = LinuxExecutableFormat.CanHoldFileCapabilities(sourceExecutable);
        if (!canHold)
            RemoveCapableCopy();

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
            _filesystem.HonorsFileCapabilities(copyPath),
            pkexec is null && _commands.HasPlainPkexec(),
            canHold));

        switch (decision)
        {
            case LinuxTunDecision.UseCapableCopy:
                if (!await CanRunAsync(copyPath, cancellationToken).ConfigureAwait(false))
                {
                    RemoveCapableCopy();
                    return PkexecOrThrow(sourceExecutable, pkexec);
                }

                SyncCompanionLibraries(sourceExecutable, copyPath);
                return LinuxTunLaunch.AsUser(copyPath);
            case LinuxTunDecision.UseSource:
                return LinuxTunLaunch.AsUser(sourceExecutable);
            case LinuxTunDecision.PkexecMissing:
                throw new TunAuthorizationException(TunAuthorizationException.PkexecMissingMessage, cancelled: false);
            case LinuxTunDecision.SetuidWrapperMissing:
                throw new TunAuthorizationException(TunAuthorizationException.SetuidWrapperMessage, cancelled: false, setuidWrapperMissing: true);
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
                SyncCompanionLibraries(sourceExecutable, copyPath);
                return LinuxTunLaunch.AsUser(copyPath);
            default:
                throw new InvalidOperationException("未知的 TUN 授权决定。");
        }
    }

    public async Task<bool> HasNetAdminAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!LinuxExecutableFormat.CanHoldFileCapabilities(path))
            return false;

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
        if (outcome == LinuxPkexecOutcome.SetuidWrapperMissing)
            throw new TunAuthorizationException(TunAuthorizationException.SetuidWrapperMessage, cancelled: false, setuidWrapperMissing: true);
        if (outcome != LinuxPkexecOutcome.Success)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new TunAuthorizationException(TunAuthorizationException.Failed(detail), cancelled: false);
        }
    }

    private LinuxTunLaunch PkexecOrThrow(string sourceExecutable, string? pkexec)
    {
        if (pkexec is not null)
            return LinuxTunLaunch.WithPkexec(sourceExecutable, pkexec);
        if (_commands.HasPlainPkexec())
            throw new TunAuthorizationException(TunAuthorizationException.SetuidWrapperMessage, cancelled: false, setuidWrapperMissing: true);
        throw new TunAuthorizationException(TunAuthorizationException.PkexecMissingMessage, cancelled: false);
    }

    private Task<bool> CanRunAsync(string path, CancellationToken cancellationToken) =>
        _canRun is not null
            ? _canRun(path, cancellationToken)
            : SingBoxLaunchProbe.CanRunAsync(path, cancellationToken);

    private void RemoveCapableCopy()
    {
        TryDelete(CapableCopyPath);
        TryDelete(HashSidecarPath);
        TryDelete(CapableCopyPath + ".tmp");
        var directory = Path.GetDirectoryName(CapableCopyPath);
        if (directory is null) return;
        foreach (var name in SingBoxCoreLayout.CompanionFileNames)
            TryDelete(Path.Combine(directory, name));
    }

    private static void SyncCompanionLibraries(string sourceExecutable, string destinationExecutable)
    {
        var sourceDirectory = Path.GetDirectoryName(sourceExecutable);
        var destinationDirectory = Path.GetDirectoryName(destinationExecutable);
        if (sourceDirectory is null || destinationDirectory is null) return;
        foreach (var name in SingBoxCoreLayout.CompanionFileNames)
        {
            var from = Path.Combine(sourceDirectory, name);
            var to = Path.Combine(destinationDirectory, name);
            if (!File.Exists(from))
            {
                TryDelete(to);
                continue;
            }

            var sourceInfo = new FileInfo(from);
            var destinationInfo = new FileInfo(to);
            if (destinationInfo.Exists && destinationInfo.Length == sourceInfo.Length) continue;
            Directory.CreateDirectory(destinationDirectory);
            var temporary = to + ".tmp";
            File.Copy(from, temporary, overwrite: true);
            File.Move(temporary, to, overwrite: true);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
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

public static class SingBoxLaunchProbe
{
    public static async Task<bool> CanRunAsync(string executable, CancellationToken cancellationToken)
    {
        Process? process = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            info.ArgumentList.Add("version");
            process = new Process { StartInfo = info };
            if (!process.Start()) return false;
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        catch
        {
            TryKill(process);
            return false;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }
}
