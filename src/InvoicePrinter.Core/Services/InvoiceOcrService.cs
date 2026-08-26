using System.Diagnostics;
using System.Text;
using System.Text.Json;
using InvoicePrinter.Core.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace InvoicePrinter.Core.Services;

public sealed class InvoiceOcrService
{
    private const int MinEmbeddedTextLength = 12;

    public async Task<IReadOnlyList<InvoiceRecognition>> RecognizeAsync(IReadOnlyList<InvoicePage> pages)
    {
        var results = new List<InvoiceRecognition>(pages.Count);
        var pending = new List<InvoicePage>();
        foreach (var page in pages)
        {
            var text = TryExtractEmbeddedText(page);
            if (text is null) pending.Add(page);
            else results.Add(CreateResult(page, text, null));
        }

        if (pending.Count > 0)
        {
            foreach (var outcome in await ExtractByOcrAsync(pending))
                results.Add(CreateResult(outcome.Page, outcome.Text, outcome.Error));
        }
        return results;
    }

    private static string? TryExtractEmbeddedText(InvoicePage page)
    {
        if (!page.SourcePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var document = PdfDocument.Open(page.SourcePath);
            if (page.PageIndex < 0 || page.PageIndex >= document.NumberOfPages) return null;
            var content = ContentOrderTextExtractor.GetText(document.GetPage(page.PageIndex + 1));
            if (string.IsNullOrWhiteSpace(content) || content.Trim().Length < MinEmbeddedTextLength) return null;
            return content;
        }
        catch
        {
            return null;
        }
    }

    private static InvoiceRecognition CreateResult(InvoicePage page, string text, string? error)
    {
        var fields = ReceiptFieldParser.Parse(text);
        var message = error ?? (fields.Category is null && fields.Amount is null && fields.SerialNumber is null ? "未识别到类型、金额或编号" : null);
        return new InvoiceRecognition(page.SourcePath, page.DisplayName, fields.Category, fields.Amount, fields.SerialNumber, message);
    }

    private static async Task<IReadOnlyList<OcrOutcome>> ExtractByOcrAsync(IReadOnlyList<InvoicePage> pages)
    {
        if (!OperatingSystem.IsWindows())
            return [.. pages.Select(page => new OcrOutcome(page, string.Empty, "当前平台暂不支持 OCR，仅支持 Windows 系统离线识别"))];

        var tempFiles = new List<string>(pages.Count);
        try
        {
            foreach (var page in pages)
            {
                var path = Path.Combine(Path.GetTempPath(), $"asterdock-ocr-{Guid.NewGuid():N}.png");
                await File.WriteAllBytesAsync(path, page.PreviewPng);
                tempFiles.Add(path);
            }
            var payload = await RunOcrScriptAsync(tempFiles);
            return MapOutcomes(pages, tempFiles, payload);
        }
        catch (Exception exception)
        {
            var message = $"OCR 服务调用失败：{exception.Message}";
            return [.. pages.Select(page => new OcrOutcome(page, string.Empty, message))];
        }
        finally
        {
            foreach (var path in tempFiles)
            {
                try { File.Delete(path); }
                catch { }
            }
        }
    }

    private static async Task<string> RunOcrScriptAsync(IReadOnlyList<string> imagePaths)
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(OcrScript)));
        foreach (var path in imagePaths) info.ArgumentList.Add(path);

        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 PowerShell");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        _ = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            throw new TimeoutException("OCR 处理超时");
        }
        var output = await outputTask;
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException("OCR 服务返回失败");
        return output.Trim();
    }

    private static IReadOnlyList<OcrOutcome> MapOutcomes(IReadOnlyList<InvoicePage> pages, IReadOnlyList<string> tempFiles, string payload)
    {
        Dictionary<string, (bool Ok, string Text)> map;
        try
        {
            using var document = JsonDocument.Parse(payload);
            IEnumerable<JsonElement> items = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray()
                : [document.RootElement];
            map = items.ToDictionary(
                item => item.GetProperty("file").GetString() ?? string.Empty,
                item => (item.GetProperty("ok").GetBoolean(), item.GetProperty("text").GetString() ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception)
        {
            var message = $"OCR 结果解析失败：{exception.Message}";
            return [.. pages.Select(page => new OcrOutcome(page, string.Empty, message))];
        }

        return [.. pages.Select((page, index) =>
        {
            var found = map.TryGetValue(tempFiles[index], out var entry);
            return found && entry.Ok
                ? new OcrOutcome(page, entry.Text, null)
                : new OcrOutcome(page, string.Empty, found ? entry.Text : "未获取到 OCR 结果");
        })];
    }

    private sealed record OcrOutcome(InvoicePage Page, string Text, string? Error);

    private const string OcrScript = """
        $ErrorActionPreference = 'Stop'
        try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch {}
        $null = [Windows.Media.Ocr.OcrEngine,Windows.Foundation,ContentType=WindowsRuntime]
        $null = [Windows.Graphics.Imaging.BitmapDecoder,Windows.Foundation,ContentType=WindowsRuntime]
        $null = [Windows.Graphics.Imaging.SoftwareBitmap,Windows.Foundation,ContentType=WindowsRuntime]
        Add-Type -AssemblyName System.Runtime.WindowsRuntime
        $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
        function Await($operation, $resultType) {
            $task = $asTask.MakeGenericMethod($resultType).Invoke($null, @($operation))
            $task.Wait()
            $task.Result
        }
        $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
        if ($null -eq $engine) {
            try { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage((New-Object Windows.Globalization.Language('zh-Hans-CN'))) } catch {}
        }
        $rows = @()
        foreach ($path in $args) {
            if ($null -eq $engine) { $rows += [pscustomobject]@{ file = $path; ok = $false; text = '未安装可用的 OCR 语言包' }; continue }
            try {
                $fileStream = [System.IO.File]::OpenRead($path)
                $winStream = [System.IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($fileStream)
                $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($winStream)) ([Windows.Graphics.Imaging.BitmapDecoder])
                $bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
                $ocr = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
                $rows += [pscustomobject]@{ file = $path; ok = $true; text = (($ocr.Lines | ForEach-Object { $_.Text }) -join ([char]10)) }
            } catch {
                $rows += [pscustomobject]@{ file = $path; ok = $false; text = $_.Exception.Message }
            }
        }
        ConvertTo-Json -InputObject @($rows) -Compress -Depth 4
        """;
}
