# Shared by the appbundle packagers. Dot-source this file; do not run it directly.
#
# The AsterDock host supplies Avalonia, SkiaSharp, HarfBuzzSharp and the theme
# assemblies, plus their native libraries. Universal module builds copy every
# RID of those natives under runtimes/, which is also where application
# libraries such as PDFtoImage's pdfium and System.IO.Ports' native binaries
# live. Delete the host files only. Removing the runtimes directory drops
# pdfium and Invoice Printer cannot open PDFs.

function Get-HostSuppliedNativeBaseNames {
    @(
        "libAvaloniaNative",
        "libHarfBuzzSharp",
        "libSkiaSharp",
        "av_libglesv2"
    )
}

function Join-AppRelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $path = $Root
    foreach ($part in ($RelativePath -split '[\\/]' | Where-Object { $_ })) {
        $path = Join-Path $path $part
    }
    return $path
}

function Remove-HostSuppliedAppAssets {
    param(
        [Parameter(Mandatory = $true)][string]$StagingDirectory
    )

    $sharedAssemblyPatterns = @(
        "AsterDock.Contracts.dll", "AsterDock.UI.dll", "Avalonia*.dll", "HarfBuzzSharp.dll",
        "Irihi.*.dll", "MicroCom.Runtime.dll", "Semi.*.dll", "SkiaSharp.dll",
        "Tmds.DBus.Protocol.dll", "Ursa*.dll"
    )
    foreach ($pattern in $sharedAssemblyPatterns) {
        Get-ChildItem -LiteralPath $StagingDirectory -File -Filter $pattern -ErrorAction SilentlyContinue |
            Remove-Item -Force
    }

    $hostNativeNames = @(Get-HostSuppliedNativeBaseNames)
    $hostNatives = @(Get-ChildItem -LiteralPath $StagingDirectory -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $hostNativeNames -contains $_.BaseName })
    foreach ($file in $hostNatives) {
        Remove-Item -LiteralPath $file.FullName -Force
    }

    Get-ChildItem -LiteralPath $StagingDirectory -Filter "*.pdb" -File -Recurse -ErrorAction SilentlyContinue |
        Remove-Item -Force

    Remove-EmptyAppDirectories -Root (Join-Path $StagingDirectory "runtimes")
}

function Remove-EmptyAppDirectories {
    param([Parameter(Mandatory = $true)][string]$Root)

    if (-not (Test-Path -LiteralPath $Root)) { return }

    $directories = @(Get-ChildItem -LiteralPath $Root -Directory -Recurse -ErrorAction SilentlyContinue |
        Sort-Object { $_.FullName.Length } -Descending)
    foreach ($directory in $directories) {
        if (-not (Test-Path -LiteralPath $directory.FullName)) { continue }
        $children = @(Get-ChildItem -LiteralPath $directory.FullName -Force -ErrorAction SilentlyContinue)
        if ($children.Count -eq 0) {
            Remove-Item -LiteralPath $directory.FullName -Force
        }
    }

    if ((Test-Path -LiteralPath $Root) -and -not @(Get-ChildItem -LiteralPath $Root -Force -ErrorAction SilentlyContinue)) {
        Remove-Item -LiteralPath $Root -Force
    }
}

function Get-InvoicePrinterPdfiumRelativePaths {
    param([string]$RuntimeIdentifier = "")

    if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
        return @(
            "runtimes/linux-x64/native/libpdfium.so",
            "runtimes/linux-arm64/native/libpdfium.so",
            "runtimes/win-x64/native/pdfium.dll",
            "runtimes/win-arm64/native/pdfium.dll",
            "runtimes/osx-x64/native/libpdfium.dylib",
            "runtimes/osx-arm64/native/libpdfium.dylib"
        )
    }

    $fileName = switch -Wildcard ($RuntimeIdentifier) {
        "win-*" { "pdfium.dll" }
        "osx-*" { "libpdfium.dylib" }
        default { "libpdfium.so" }
    }
    # A RID-specific build flattens the selected native beside the assembly.
    # Keep accepting runtimes/<rid>/native when the SDK copied it there too.
    return @($fileName)
}

function Assert-InvoicePrinterPdfium {
    param(
        [Parameter(Mandatory = $true)][string]$StagingDirectory,
        [string]$RuntimeIdentifier = "",
        [string]$BundlePath = ""
    )

    $minimumBytes = 1MB
    if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
        $required = @(Get-InvoicePrinterPdfiumRelativePaths)
        foreach ($relative in $required) {
            $path = Join-AppRelativePath -Root $StagingDirectory -RelativePath $relative
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "Invoice Printer package is missing $relative. PDFtoImage loads pdfium from the application directory; the AsterDock host does not supply it."
            }
            $length = (Get-Item -LiteralPath $path).Length
            if ($length -lt $minimumBytes) {
                throw "Invoice Printer native $relative is only $length bytes; expected the PDFium library."
            }
        }
    }
    else {
        $fileName = @(Get-InvoicePrinterPdfiumRelativePaths -RuntimeIdentifier $RuntimeIdentifier)[0]
        $candidates = @(
            $fileName,
            "runtimes/$RuntimeIdentifier/native/$fileName"
        )
        $present = @($candidates | Where-Object {
            Test-Path -LiteralPath (Join-AppRelativePath -Root $StagingDirectory -RelativePath $_) -PathType Leaf
        })
        if ($present.Count -eq 0) {
            throw "Invoice Printer package is missing $fileName for $RuntimeIdentifier. PDFtoImage loads pdfium from the application directory; the AsterDock host does not supply it."
        }
        foreach ($relative in $present) {
            $path = Join-AppRelativePath -Root $StagingDirectory -RelativePath $relative
            $length = (Get-Item -LiteralPath $path).Length
            if ($length -lt $minimumBytes) {
                throw "Invoice Printer native $relative is only $length bytes; expected the PDFium library."
            }
        }
    }

    Assert-NoHostSuppliedAppAssets -StagingDirectory $StagingDirectory

    if (-not [string]::IsNullOrWhiteSpace($BundlePath)) {
        Assert-InvoicePrinterPdfiumBundle -BundlePath $BundlePath -RuntimeIdentifier $RuntimeIdentifier
    }

    Write-Output "Invoice Printer PDFium natives verified."
}

function Assert-NoHostSuppliedAppAssets {
    param([Parameter(Mandatory = $true)][string]$StagingDirectory)

    $hostNativeNames = @(Get-HostSuppliedNativeBaseNames)
    $leaked = @(Get-ChildItem -LiteralPath $StagingDirectory -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $hostNativeNames -contains $_.BaseName -or $_.Extension -eq ".pdb" })
    if ($leaked.Count -gt 0) {
        $prefix = $StagingDirectory.TrimEnd('\', '/')
        $relative = @($leaked | ForEach-Object { $_.FullName.Substring($prefix.Length).TrimStart('\', '/') })
        throw "Host-supplied native assets or debug symbols are still staged: $($relative -join ', ')"
    }

    $sharedAssemblies = @(Get-ChildItem -LiteralPath $StagingDirectory -File -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -eq "AsterDock.Contracts.dll" -or
            $_.Name -eq "AsterDock.UI.dll" -or
            $_.Name -eq "HarfBuzzSharp.dll" -or
            $_.Name -eq "MicroCom.Runtime.dll" -or
            $_.Name -eq "SkiaSharp.dll" -or
            $_.Name -eq "Tmds.DBus.Protocol.dll" -or
            $_.Name -like "Avalonia*.dll" -or
            $_.Name -like "Irihi.*.dll" -or
            $_.Name -like "Semi.*.dll" -or
            $_.Name -like "Ursa*.dll"
        })
    if ($sharedAssemblies.Count -gt 0) {
        throw "Host-supplied assemblies are still staged: $(($sharedAssemblies.Name) -join ', ')"
    }
}

function Assert-InvoicePrinterPdfiumBundle {
    param(
        [Parameter(Mandatory = $true)][string]$BundlePath,
        [string]$RuntimeIdentifier = ""
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($BundlePath)
    try {
        $entries = @{}
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.EndsWith("/")) { continue }
            $name = ($entry.FullName -replace '\\', '/').TrimStart('/')
            $entries[$name] = [int64]$entry.Length
        }
    }
    finally {
        $zip.Dispose()
    }

    $minimumBytes = 1MB
    if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
        foreach ($relative in @(Get-InvoicePrinterPdfiumRelativePaths)) {
            if (-not $entries.ContainsKey($relative)) {
                throw "Invoice Printer appbundle is missing $relative."
            }
            if ($entries[$relative] -lt $minimumBytes) {
                throw "Invoice Printer appbundle entry $relative is only $($entries[$relative]) bytes."
            }
        }
    }
    else {
        $fileName = @(Get-InvoicePrinterPdfiumRelativePaths -RuntimeIdentifier $RuntimeIdentifier)[0]
        $candidates = @($fileName, "runtimes/$RuntimeIdentifier/native/$fileName")
        $present = @($candidates | Where-Object { $entries.ContainsKey($_) -and $entries[$_] -ge $minimumBytes })
        if ($present.Count -eq 0) {
            throw "Invoice Printer appbundle is missing $fileName for $RuntimeIdentifier."
        }
    }

    $hostNativeNames = @(Get-HostSuppliedNativeBaseNames)
    foreach ($name in $entries.Keys) {
        $fileName = [System.IO.Path]::GetFileName($name)
        $baseName = [System.IO.Path]::GetFileNameWithoutExtension($fileName)
        if ($hostNativeNames -contains $baseName -or $fileName.EndsWith(".pdb")) {
            throw "Invoice Printer appbundle still contains host-supplied asset $name."
        }
        if ($name.Contains("/")) { continue }
        if ($fileName -eq "SkiaSharp.dll" -or $fileName -eq "HarfBuzzSharp.dll" -or $fileName -like "Avalonia*.dll" -or
            $fileName -eq "AsterDock.Contracts.dll" -or $fileName -eq "AsterDock.UI.dll") {
            throw "Invoice Printer appbundle still contains host-supplied assembly $name."
        }
    }
}
