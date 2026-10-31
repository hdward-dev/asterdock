using System.Formats.Tar;
using System.IO.Compression;

namespace NetworkAccelerator.Core.Services;

public static class SingBoxCoreLayout
{
    public static readonly string[] CompanionFileNames = ["libcronet.so", "libcronet.dylib", "libcronet.dll"];

    public static async Task ExtractAsync(
        string archivePath,
        string destinationDirectory,
        string executableName,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            await ExtractZipAsync(archivePath, destinationDirectory, executableName, cancellationToken).ConfigureAwait(false);
        else
            await ExtractTarGzipAsync(archivePath, destinationDirectory, executableName, cancellationToken).ConfigureAwait(false);

        if (!File.Exists(Path.Combine(destinationDirectory, executableName)))
            throw new InvalidDataException("sing-box 下载包中未找到核心文件");
    }

    private static async Task ExtractZipAsync(
        string archivePath,
        string destinationDirectory,
        string executableName,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var wroteExecutable = false;
        var wroteCompanions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = Path.GetFileName(entry.FullName);
            if (!ShouldExtract(name, executableName, wroteExecutable, wroteCompanions)) continue;
            var destinationName = CanonicalName(name, executableName);
            await using var input = entry.Open();
            await CopyEntryAsync(input, Path.Combine(destinationDirectory, destinationName), cancellationToken).ConfigureAwait(false);
            if (destinationName.Equals(executableName, StringComparison.OrdinalIgnoreCase)) wroteExecutable = true;
            else wroteCompanions.Add(destinationName);
        }
    }

    private static async Task ExtractTarGzipAsync(
        string archivePath,
        string destinationDirectory,
        string executableName,
        CancellationToken cancellationToken)
    {
        await using var archiveStream = File.OpenRead(archivePath);
        await using var gzip = new GZipStream(archiveStream, CompressionMode.Decompress);
        using var reader = new TarReader(gzip, leaveOpen: false);
        var wroteExecutable = false;
        var wroteCompanions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            if (entry.EntryType is not TarEntryType.RegularFile || entry.DataStream is null) continue;
            var name = Path.GetFileName(entry.Name.Replace('\\', '/'));
            if (!ShouldExtract(name, executableName, wroteExecutable, wroteCompanions))
            {
                await entry.DataStream.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var destinationName = CanonicalName(name, executableName);
            await CopyEntryAsync(entry.DataStream, Path.Combine(destinationDirectory, destinationName), cancellationToken).ConfigureAwait(false);
            if (destinationName.Equals(executableName, StringComparison.OrdinalIgnoreCase)) wroteExecutable = true;
            else wroteCompanions.Add(destinationName);
        }
    }

    private static string CanonicalName(string name, string executableName)
    {
        if (name.Equals(executableName, StringComparison.OrdinalIgnoreCase)) return executableName;
        foreach (var companion in CompanionFileNames)
        {
            if (companion.Equals(name, StringComparison.OrdinalIgnoreCase)) return companion;
        }

        return name;
    }

    private static bool ShouldExtract(string name, string executableName, bool wroteExecutable, HashSet<string> wroteCompanions)
    {
        if (name.Length == 0) return false;
        if (name.Equals(executableName, StringComparison.OrdinalIgnoreCase)) return !wroteExecutable;
        return CompanionFileNames.Contains(name, StringComparer.OrdinalIgnoreCase) && !wroteCompanions.Contains(name);
    }

    private static async Task CopyEntryAsync(Stream input, string destination, CancellationToken cancellationToken)
    {
        var temporary = destination + ".partial";
        await using (var output = File.Create(temporary))
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, destination, overwrite: true);
    }
}
