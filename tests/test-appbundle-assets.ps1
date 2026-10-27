# Locks the appbundle native filter: host Skia/HarfBuzz/Avalonia natives go away,
# application natives (PDFium, System.IO.Ports) stay. Pass -Bundle to also
# inspect a packaged AsterDock-App-invoice-printer.appbundle.
param(
    [string]$Bundle = ""
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "../scripts/appbundle-assets.ps1")

function New-File {
    param([string]$Path, [string]$Content = "native")
    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    Set-Content -LiteralPath $Path -Value $Content -NoNewline
}

function Assert-Path {
    param([string]$Root, [string]$Relative, [bool]$Exists)
    $path = Join-AppRelativePath -Root $Root -RelativePath $Relative
    $actual = Test-Path -LiteralPath $path
    if ($actual -ne $Exists) {
        $state = if ($Exists) { "missing" } else { "still present" }
        throw "$Relative is $state after removing host-supplied assets."
    }
}

$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("asterdock-appbundle-assets-" + [guid]::NewGuid().ToString("n"))
try {
    New-Item -ItemType Directory -Path $staging | Out-Null
    foreach ($relative in @(
        "InvoicePrinter.Module.dll",
        "PDFtoImage.dll",
        "UglyToad.PdfPig.dll",
        "app.json",
        "libpdfium.so",
        "SkiaSharp.dll",
        "HarfBuzzSharp.dll",
        "Avalonia.Controls.dll",
        "AsterDock.Contracts.dll",
        "AsterDock.UI.dll",
        "libSkiaSharp.so",
        "libHarfBuzzSharp.so",
        "libAvaloniaNative.dylib",
        "av_libglesv2.dll",
        "InvoicePrinter.Module.pdb",
        "runtimes/linux-x64/native/libpdfium.so",
        "runtimes/linux-x64/native/libSkiaSharp.so",
        "runtimes/linux-x64/native/libHarfBuzzSharp.so",
        "runtimes/linux-x64/native/libSystem.IO.Ports.Native.so",
        "runtimes/linux-arm64/native/libpdfium.so",
        "runtimes/win-x64/native/pdfium.dll",
        "runtimes/win-x64/native/libSkiaSharp.dll",
        "runtimes/win-x64/native/av_libglesv2.dll",
        "runtimes/win-x64/native/libSkiaSharp.pdb",
        "runtimes/win-arm64/native/pdfium.dll",
        "runtimes/osx-x64/native/libpdfium.dylib",
        "runtimes/osx-arm64/native/libpdfium.dylib",
        "runtimes/osx/native/libAvaloniaNative.dylib",
        "runtimes/osx/native/libHarfBuzzSharp.dylib",
        "runtimes/osx/native/libSkiaSharp.dylib",
        "runtimes/linux-loongarch64/native/libSkiaSharp.so",
        "runtimes/win/lib/net10.0/System.IO.Ports.dll"
    )) {
        New-File -Path (Join-AppRelativePath -Root $staging -RelativePath $relative)
    }

    Remove-HostSuppliedAppAssets -StagingDirectory $staging

    foreach ($relative in @(
        "InvoicePrinter.Module.dll",
        "PDFtoImage.dll",
        "UglyToad.PdfPig.dll",
        "app.json",
        "libpdfium.so",
        "runtimes/linux-x64/native/libpdfium.so",
        "runtimes/linux-x64/native/libSystem.IO.Ports.Native.so",
        "runtimes/linux-arm64/native/libpdfium.so",
        "runtimes/win-x64/native/pdfium.dll",
        "runtimes/win-arm64/native/pdfium.dll",
        "runtimes/osx-x64/native/libpdfium.dylib",
        "runtimes/osx-arm64/native/libpdfium.dylib",
        "runtimes/win/lib/net10.0/System.IO.Ports.dll"
    )) {
        Assert-Path -Root $staging -Relative $relative -Exists $true
    }

    foreach ($relative in @(
        "SkiaSharp.dll",
        "HarfBuzzSharp.dll",
        "Avalonia.Controls.dll",
        "AsterDock.Contracts.dll",
        "AsterDock.UI.dll",
        "libSkiaSharp.so",
        "libHarfBuzzSharp.so",
        "libAvaloniaNative.dylib",
        "av_libglesv2.dll",
        "InvoicePrinter.Module.pdb",
        "runtimes/linux-x64/native/libSkiaSharp.so",
        "runtimes/linux-x64/native/libHarfBuzzSharp.so",
        "runtimes/win-x64/native/libSkiaSharp.dll",
        "runtimes/win-x64/native/av_libglesv2.dll",
        "runtimes/win-x64/native/libSkiaSharp.pdb",
        "runtimes/osx",
        "runtimes/linux-loongarch64"
    )) {
        Assert-Path -Root $staging -Relative $relative -Exists $false
    }

    $missing = Join-Path ([System.IO.Path]::GetTempPath()) ("asterdock-pdfium-missing-" + [guid]::NewGuid().ToString("n"))
    New-Item -ItemType Directory -Path $missing | Out-Null
    try {
        $threw = $false
        try {
            Assert-InvoicePrinterPdfium -StagingDirectory $missing
        }
        catch {
            $threw = $true
            if ($_.Exception.Message -notmatch 'runtimes/linux-x64/native/libpdfium\.so') {
                throw
            }
        }
        if (-not $threw) { throw "A package without linux-x64 libpdfium.so was accepted." }
    }
    finally {
        Remove-Item -LiteralPath $missing -Recurse -Force
    }

    Write-Output "Appbundle native filter passed."
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}

if (-not [string]::IsNullOrWhiteSpace($Bundle)) {
    if (-not (Test-Path -LiteralPath $Bundle -PathType Leaf)) {
        throw "Invoice Printer appbundle was not found: $Bundle"
    }
    Assert-InvoicePrinterPdfiumBundle -BundlePath $Bundle
    Write-Output "Packaged Invoice Printer contains PDFium for linux-x64, linux-arm64, win-x64, win-arm64, osx-x64 and osx-arm64."
}
