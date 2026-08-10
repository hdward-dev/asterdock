namespace AsterDock.Host.Services;

internal static class ApplicationPaths
{
    private const string ProductDirectoryName = "AsterDock";
    private const string LegacyProductDirectoryName = "ApplicationHub";

    private static string LocalApplicationData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string ProductDataDirectory => EnsureDirectory(ProductDirectoryName);

    public static string UserAppsDirectory => EnsureMigratedDirectory("Apps");

    public static string GetApplicationDataDirectory(string applicationId) =>
        EnsureMigratedDirectory("AppData", applicationId);

    private static string EnsureDirectory(params string[] relativeParts)
    {
        var path = Path.Combine([LocalApplicationData, .. relativeParts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string EnsureMigratedDirectory(params string[] relativeParts)
    {
        var currentPath = Path.Combine([LocalApplicationData, ProductDirectoryName, .. relativeParts]);
        if (Directory.Exists(currentPath)) return currentPath;

        var legacyPath = Path.Combine([LocalApplicationData, LegacyProductDirectoryName, .. relativeParts]);
        if (Directory.Exists(legacyPath)) TryCopyDirectory(legacyPath, currentPath);
        Directory.CreateDirectory(currentPath);
        return currentPath;
    }

    private static void TryCopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        try
        {
            foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
                var destinationPath = Path.Combine(destinationDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                File.Copy(sourcePath, destinationPath, overwrite: false);
            }
        }
        catch (IOException)
        {
            // Another process may have completed the migration concurrently.
        }
        catch (UnauthorizedAccessException)
        {
            // Keep startup resilient; the new directory will still be created below.
        }
    }
}
