param(
    [string]$OutputDirectory = "",
    # Package one installable app id (for example invoice-printer). Empty packages every app.
    [string]$Name = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "appbundle-assets.ps1")
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts/release/apps"
}

$applications = @(Get-ChildItem -Path (Join-Path $repoRoot "src/*.Module/app.json") | Sort-Object FullName | ForEach-Object {
    $manifest = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    # Home is supplied by the host and is not an installable application.
    if ($manifest.id -ne "home") {
        @{ Name = $manifest.id; Project = $_.Directory.Name }
    }
})
if (-not [string]::IsNullOrWhiteSpace($Name)) {
    $applications = @($applications | Where-Object { $_.Name -eq $Name })
    if ($applications.Count -eq 0) {
        throw "No installable application with id '$Name'."
    }
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

foreach ($application in $applications) {
    $projectDirectory = Join-Path $repoRoot "src/$($application.Project)"
    $projectPath = Join-Path $projectDirectory "$($application.Project).csproj"

    dotnet build $projectPath `
        --configuration Release
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $sourceDirectory = Join-Path $projectDirectory "bin/Release/net10.0"
    $bundlePath = Join-Path $OutputDirectory "AsterDock-App-$($application.Name).appbundle"
    $stagingDirectory = Join-Path $OutputDirectory ".staging-$($application.Name)"
    if (Test-Path -LiteralPath $bundlePath) {
        [System.IO.File]::Delete($bundlePath)
    }
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }

    try {
        New-Item -ItemType Directory -Path $stagingDirectory | Out-Null
        # A developer machine may retain sibling RID build directories below
        # bin/Release/net10.0. They are not part of the portable module output.
        Get-ChildItem -Path $sourceDirectory | Where-Object {
            $_.Name -notmatch '^(win|osx|linux)-'
        } | Copy-Item -Destination $stagingDirectory -Recurse

        # QEMU engines are installed per platform on demand; keep the universal app below 512 MB.
        # SPICE stays in the universal bundle. After QEMU is removed, also publish
        # VMCUBE-SPICE-<RID>.zip (helper + relocated libs only) so a missing helper can be repaired.
        if ($application.Name -eq "vmcube") {
            $nativeDirectory = Join-Path $stagingDirectory "native"
            if (Test-Path $nativeDirectory) {
                foreach ($platform in Get-ChildItem -LiteralPath $nativeDirectory -Directory) {
                    $engine = Join-Path $platform.FullName "qemu"
                    if (Test-Path $engine) {
                        $engineBundle = Join-Path $OutputDirectory "VMCUBE-QEMU-$($platform.Name).zip"
                        if (Test-Path $engineBundle) { [System.IO.File]::Delete($engineBundle) }
                        [System.IO.Compression.ZipFile]::CreateFromDirectory($engine, $engineBundle, [System.IO.Compression.CompressionLevel]::Optimal, $false)
                        Remove-Item -LiteralPath $engine -Recurse -Force
                    }
                    $helperName = "vmcube-spice"
                    if ($platform.Name -like "win-*") { $helperName = "vmcube-spice.exe" }
                    $helper = Join-Path $platform.FullName $helperName
                    if (Test-Path -LiteralPath $helper) {
                        $spiceBundle = Join-Path $OutputDirectory "VMCUBE-SPICE-$($platform.Name).zip"
                        if (Test-Path $spiceBundle) { [System.IO.File]::Delete($spiceBundle) }
                        [System.IO.Compression.ZipFile]::CreateFromDirectory($platform.FullName, $spiceBundle, [System.IO.Compression.CompressionLevel]::Optimal, $false)
                        Write-Output $spiceBundle
                    }
                }
            }
        }

        # Host assemblies and Avalonia/Skia/HarfBuzz natives are supplied by AsterDock.
        # Application natives under runtimes/<rid>/native, including PDFium, stay in the bundle.
        Remove-HostSuppliedAppAssets -StagingDirectory $stagingDirectory

        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $stagingDirectory,
            $bundlePath,
            [System.IO.Compression.CompressionLevel]::Optimal,
            $false)
        if ($application.Name -eq "invoice-printer") {
            Assert-InvoicePrinterPdfium -StagingDirectory $stagingDirectory -BundlePath $bundlePath
        }
        Write-Output $bundlePath
    }
    finally {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
