using System.Diagnostics;
using System.Text;

namespace NetworkAccelerator.Core.Services;

public sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null);

public sealed record ProcessResult(
    bool Started,
    int ExitCode,
    string StandardOutput,
    string StandardError);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}

public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
            info.WorkingDirectory = request.WorkingDirectory;
        foreach (var argument in request.Arguments)
            info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) return new ProcessResult(false, -1, string.Empty, string.Empty);
        }
        catch (Exception exception)
        {
            return new ProcessResult(false, -1, string.Empty, exception.Message);
        }

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new ProcessResult(
            true,
            process.ExitCode,
            await output.ConfigureAwait(false),
            await error.ConfigureAwait(false));
    }
}
