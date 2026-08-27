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
    if (Test-Path -LiteralPath $bundlePath) {
        [System.IO.File]::Delete($bundlePath)
    }
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $sourceDirectory,
        $bundlePath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)
    Write-Output $bundlePath
}
