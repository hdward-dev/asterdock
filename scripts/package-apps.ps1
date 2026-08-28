param(
    [ValidateSet("win-x64", "win-arm64", "osx-x64", "osx-arm64")]
    [string]$RuntimeIdentifier,
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts/release/$RuntimeIdentifier"
}

$applications = @(
    @{ Name = "home"; Project = "Home.Module" },
    @{ Name = "invoice-printer"; Project = "InvoicePrinter.Module" },
    @{ Name = "device-information"; Project = "DeviceInformation.Module" },
    @{ Name = "serial-debugger"; Project = "SerialDebugger.Module" },
    @{ Name = "network-accelerator"; Project = "NetworkAccelerator.Module" },
    @{ Name = "android-screen"; Project = "AndroidScreen.Module" }
)

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

foreach ($application in $applications) {
    $projectDirectory = Join-Path $repoRoot "src/$($application.Project)"
    $projectPath = Join-Path $projectDirectory "$($application.Project).csproj"

    dotnet build $projectPath `
        --configuration Release `
        --runtime $RuntimeIdentifier `
        --self-contained false
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $sourceDirectory = Join-Path $projectDirectory "bin/Release/net10.0/$RuntimeIdentifier"
    $bundlePath = Join-Path $OutputDirectory "AsterDock-App-$($application.Name)-$RuntimeIdentifier.appbundle"
    $stagingDirectory = Join-Path $OutputDirectory ".staging-$($application.Name)-$RuntimeIdentifier"
    if (Test-Path -LiteralPath $bundlePath) {
        [System.IO.File]::Delete($bundlePath)
    }
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }

    try {
        New-Item -ItemType Directory -Path $stagingDirectory | Out-Null
        Copy-Item -Path (Join-Path $sourceDirectory "*") -Destination $stagingDirectory -Recurse

        # These assemblies and native assets are supplied by the AsterDock host.
        $sharedAssemblyPatterns = @(
            "AsterDock.Contracts.dll", "AsterDock.UI.dll", "Avalonia*.dll", "HarfBuzzSharp.dll",
            "Irihi.*.dll", "MicroCom.Runtime.dll", "Semi.*.dll", "SkiaSharp.dll",
            "Tmds.DBus.Protocol.dll", "Ursa*.dll"
        )
        foreach ($pattern in $sharedAssemblyPatterns) {
            Get-ChildItem -Path $stagingDirectory -File -Filter $pattern | Remove-Item -Force
        }
        Get-ChildItem -Path $stagingDirectory -File -Filter "libAvaloniaNative.*" | Remove-Item -Force
        Get-ChildItem -Path $stagingDirectory -File -Filter "libHarfBuzzSharp.*" | Remove-Item -Force
        Get-ChildItem -Path $stagingDirectory -File -Filter "libSkiaSharp.*" | Remove-Item -Force
        Get-ChildItem -Path $stagingDirectory -Filter "*.pdb" -File -Recurse | Remove-Item -Force
        Remove-Item -LiteralPath (Join-Path $stagingDirectory "runtimes") -Recurse -Force -ErrorAction SilentlyContinue

        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $stagingDirectory,
            $bundlePath,
            [System.IO.Compression.CompressionLevel]::Optimal,
            $false)
        Write-Output $bundlePath
    }
    finally {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
