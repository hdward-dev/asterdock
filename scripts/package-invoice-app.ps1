param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0"
)

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\InvoicePrinter.Module\InvoicePrinter.Module.csproj"
$source = Join-Path $repoRoot "src\InvoicePrinter.Module\bin\$Configuration\net10.0"
$artifactDirectory = Join-Path $repoRoot "artifacts\apps"
$bundle = Join-Path $artifactDirectory "AsterDockApp-invoice-printer-$Version.appbundle"

dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$manifest = Get-Content (Join-Path $source "app.json") -Raw | ConvertFrom-Json
if ($manifest.version -ne $Version) {
    throw "Version $Version does not match app.json version $($manifest.version)"
}

New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
if (Test-Path -LiteralPath $bundle) { Remove-Item -LiteralPath $bundle -Force }
Compress-Archive -Path (Join-Path $source "*") -DestinationPath $bundle -CompressionLevel Optimal
Write-Output $bundle
