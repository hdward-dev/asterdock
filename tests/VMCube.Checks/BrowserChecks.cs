using VMCube.Module.Services;

internal static class BrowserChecks
{
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "vmcube-browser-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        var path = Environment.GetEnvironmentVariable("PATH");
        var libraries = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        var plugins = Environment.GetEnvironmentVariable("QT_PLUGIN_PATH");
        var marker = Path.Combine(root, "opened");
        try
        {
            var open = Path.Combine(root, "xdg-open");
            // No shell interpolation of the URL; one exact argv argument, no inherited native library paths.
            await File.WriteAllTextAsync(open, $"#!/bin/sh\n[ -z \"$LD_LIBRARY_PATH\" ] || exit 20\n[ -z \"$QT_PLUGIN_PATH\" ] || exit 21\n[ \"$#\" = 1 ] || exit 22\nprintf '%s' \"$1\" > '{marker}'\n");
            File.SetUnixFileMode(open, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("PATH", root);
            Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", "/tmp/vmcube-bundled-libraries");
            Environment.SetEnvironmentVariable("QT_PLUGIN_PATH", "/tmp/vmcube-bundled-plugins");
            var uri = new Uri("https://example.invalid/download?a=one%20two&b=中文");
            var result = await ExternalBrowser.OpenAsync(uri, _ => throw new Exception("Desktop command should have opened URL first"));
            if (!result.Requested || await File.ReadAllTextAsync(marker) != uri.AbsoluteUri) throw new Exception("Native browser launch lost arguments or inherited native libraries");
            await File.WriteAllTextAsync(open, "#!/bin/sh\nexit 4\n");
            result = await ExternalBrowser.OpenAsync(uri, _ => Task.FromResult(false));
            if (result.Requested || !result.Message.Contains("复制")) throw new Exception("False launch result was silently accepted");
            result = await ExternalBrowser.OpenAsync(uri, _ => throw new InvalidOperationException("Unavailable launcher"));
            if (result.Requested) throw new Exception("Launcher exception reported as success");
            try { await ExternalBrowser.OpenAsync(new Uri("file:///tmp/test"), _ => Task.FromResult(true)); throw new Exception("Non-web URI accepted"); }
            catch (ArgumentException) { }
            Console.WriteLine("Browser checks passed: desktop fallback, isolated library environment, exact URL argument, false/exception feedback and URI validation.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
            Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", libraries);
            Environment.SetEnvironmentVariable("QT_PLUGIN_PATH", plugins);
            Directory.Delete(root, true);
        }
    }
}
