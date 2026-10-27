param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0",
    [Parameter(Mandatory = $true)]
    [ValidateSet("win-x64", "win-arm64", "osx-x64", "osx-arm64")]
    [string]$RuntimeIdentifier
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "appbundle-assets.ps1")
$project = Join-Path $repoRoot "src\InvoicePrinter.Module\InvoicePrinter.Module.csproj"
$source = Join-Path $repoRoot "src\InvoicePrinter.Module\bin\$Configuration\net10.0\$RuntimeIdentifier"
$artifactDirectory = Join-Path $repoRoot "artifacts\apps"
$bundle = Join-Path $artifactDirectory "AsterDockApp-invoice-printer-$Version-$RuntimeIdentifier.appbundle"
$stagingDirectory = Join-Path $artifactDirectory ".invoice-printer-$Version-$RuntimeIdentifier"

dotnet build $project -c $Configuration --runtime $RuntimeIdentifier --self-contained false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$manifest = Get-Content (Join-Path $source "app.json") -Raw | ConvertFrom-Json
if ($manifest.version -ne $Version) {
    throw "Version $Version does not match app.json version $($manifest.version)"
}

New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
if (Test-Path -LiteralPath $bundle) { Remove-Item -LiteralPath $bundle -Force }
if (Test-Path -LiteralPath $stagingDirectory) { Remove-Item -LiteralPath $stagingDirectory -Recurse -Force }

try {
    New-Item -ItemType Directory -Path $stagingDirectory | Out-Null
    Copy-Item -Path (Join-Path $source "*") -Destination $stagingDirectory -Recurse

    # Host assemblies and Avalonia/Skia/HarfBuzz natives are supplied by AsterDock.
    # Keep PDFium, which a RID build places beside the assembly or under runtimes/<rid>/native.
    Remove-HostSuppliedAppAssets -StagingDirectory $stagingDirectory
    Assert-InvoicePrinterPdfium -StagingDirectory $stagingDirectory -RuntimeIdentifier $RuntimeIdentifier

    Compress-Archive -Path (Join-Path $stagingDirectory "*") -DestinationPath $bundle -CompressionLevel Optimal
    Assert-InvoicePrinterPdfiumBundle -BundlePath $bundle -RuntimeIdentifier $RuntimeIdentifier
    Write-Output $bundle
}
finally {
    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
