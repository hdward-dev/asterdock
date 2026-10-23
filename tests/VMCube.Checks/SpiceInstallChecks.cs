using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using VMCube.Module.Services;

internal static class SpiceInstallChecks
{
    public static async Task RunAsync()
    {
        Check(SpiceComponentInstaller.AssetFileName("linux-x64") == "VMCUBE-SPICE-linux-x64.zip", "linux spice asset name");
        Check(SpiceComponentInstaller.AssetFileName("win-arm64") == "VMCUBE-SPICE-win-arm64.zip", "windows spice asset name");
        var digest = "sha256:" + new string('a', 64);
        Check(SpiceComponentInstaller.RequireSha256Digest(digest) == digest, "sha256 digest accepted");
        foreach (var value in new string?[] { null, "", "sha256:abcd", "md5:" + new string('a', 64), "sha256:" + new string('g', 64), "SHA256:" + new string('a', 64) })
        {
            try { SpiceComponentInstaller.RequireSha256Digest(value); throw new Exception("invalid spice digest accepted"); }
            catch (InvalidDataException) { }
        }
        SpiceComponentInstaller.RequireReleaseDownloadUrl("https://github.com/hdward-dev/asterdock/releases/download/v1.0.69/VMCUBE-SPICE-linux-x64.zip");
        foreach (var url in new string?[] { null, "", "http://github.com/hdward-dev/asterdock/releases/download/v1/VMCUBE-SPICE-linux-x64.zip", "https://github.com/other/asterdock/releases/download/v1/VMCUBE-SPICE-linux-x64.zip", "https://example.com/VMCUBE-SPICE-linux-x64.zip" })
        {
            try { SpiceComponentInstaller.RequireReleaseDownloadUrl(url); throw new Exception("untrusted spice url accepted: " + url); }
            catch (InvalidDataException) { }
        }
        var message = SpiceDesktopSession.MissingComponentMessage("linux-x64");
        Check(message.Contains("linux-x64", StringComparison.Ordinal) && message.Contains("安装 SPICE 组件", StringComparison.Ordinal) && message.Contains("VMCUBE 安装包", StringComparison.Ordinal), "missing spice message is actionable");

        var root = Path.Combine(Path.GetTempPath(), "vmcube-spice-install-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var data = Path.Combine(root, "data");
            Directory.CreateDirectory(data);
            Check(SpiceComponentInstaller.GetInstalledDirectory(data) is null, "spice marker absent");
            if (SpiceDesktopSession.TryFindHelper(data) is null)
            {
                try { SpiceDesktopSession.FindHelper(data); throw new Exception("missing spice helper accepted"); }
                catch (SpiceComponentMissingException ex)
                {
                    Check(ex.RuntimeIdentifier == System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier && ex.Message.Contains(ex.RuntimeIdentifier, StringComparison.Ordinal), "missing helper names the current RID");
                }
            }

            var badDigest = "sha256:" + new string('b', 64);
            var missingHelper = Path.Combine(root, "missing.zip");
            WriteZip(missingHelper, ("readme.txt", "no helper"u8.ToArray()));
            try { await SpiceComponentInstaller.InstallVerifiedArchiveAsync(data, missingHelper, Hash(missingHelper), default); throw new Exception("spice archive without helper accepted"); }
            catch (InvalidDataException) { }
            Check(SpiceComponentInstaller.GetInstalledDirectory(data) is null, "failed install does not write a marker");
            try { await SpiceComponentInstaller.InstallVerifiedArchiveAsync(data, missingHelper, badDigest, default); throw new Exception("spice digest mismatch accepted"); }
            catch (InvalidDataException) { }

            var traversal = Path.Combine(root, "traversal.zip");
            WriteZip(traversal, ("../escape", "bad"u8.ToArray()), (SpiceDesktopSession.HelperFileName, "helper"u8.ToArray()));
            try { await SpiceComponentInstaller.InstallVerifiedArchiveAsync(data, traversal, Hash(traversal), default); throw new Exception("spice path traversal accepted"); }
            catch (InvalidDataException) { }
            Check(!Directory.EnumerateFiles(root, "escape", SearchOption.AllDirectories).Any(), "spice traversal did not write an escaped file");

            var nested = Path.Combine(root, "nested.zip");
            WriteZip(nested, ("native/linux-x64/" + SpiceDesktopSession.HelperFileName, "helper"u8.ToArray()));
            try { await SpiceComponentInstaller.InstallVerifiedArchiveAsync(data, nested, Hash(nested), default); throw new Exception("nested spice helper accepted"); }
            catch (InvalidDataException) { }

            var archive = Path.Combine(root, "spice.zip");
            WriteZip(archive, (SpiceDesktopSession.HelperFileName, "spice-worker"u8.ToArray()), ("libspice-client-glib-2.0.so", "library"u8.ToArray()));
            var destination = await SpiceComponentInstaller.InstallVerifiedArchiveAsync(data, archive, Hash(archive), default);
            var helper = Path.Combine(destination, SpiceDesktopSession.HelperFileName);
            Check(File.Exists(helper) && File.ReadAllText(helper) == "spice-worker", "spice helper installed");
            Check(File.ReadAllText(Path.Combine(destination, "libspice-client-glib-2.0.so")) == "library", "relocated spice library installed");
            if (!OperatingSystem.IsWindows()) Check(File.GetUnixFileMode(helper).HasFlag(UnixFileMode.UserExecute), "installed spice helper is executable");
            Check(SpiceComponentInstaller.GetInstalledDirectory(data) == destination, "spice marker records the install directory");
            Check(SpiceDesktopSession.HelperCandidates(data).Any(path => Path.GetFullPath(path) == Path.GetFullPath(helper)), "find helper searches the installed directory");
            var moduleHelper = Path.Combine(Path.GetDirectoryName(typeof(SpiceDesktopSession).Assembly.Location)!, "native", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, SpiceDesktopSession.HelperFileName);
            var baseHelper = Path.Combine(AppContext.BaseDirectory, "native", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, SpiceDesktopSession.HelperFileName);
            if (!File.Exists(moduleHelper) && !File.Exists(baseHelper))
                Check(Path.GetFullPath(SpiceDesktopSession.TryFindHelper(data)!) == Path.GetFullPath(helper), "installed helper is selected when the bundle has none");

            var outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(outside);
            var outsideHelper = Path.Combine(outside, SpiceDesktopSession.HelperFileName);
            await File.WriteAllTextAsync(outsideHelper, "outside");
            await File.WriteAllTextAsync(Path.Combine(data, "spice-directory.json"), JsonSerializer.Serialize(outside));
            Check(!SpiceDesktopSession.HelperCandidates(data).Any(path => Path.GetFullPath(path) == Path.GetFullPath(outsideHelper)), "marker outside the data directory is ignored");
            var found = SpiceDesktopSession.TryFindHelper(data);
            Check(found is null || Path.GetFullPath(found) != Path.GetFullPath(outsideHelper), "outside helper is not executed");
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("SPICE component install checks passed: digest, trusted URL, missing helper, traversal, layout and data-directory marker.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static string Hash(string path) => "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void WriteZip(string path, params (string Name, byte[] Bytes)[] entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var stream = entry.Open();
            stream.Write(bytes);
        }
    }
}
