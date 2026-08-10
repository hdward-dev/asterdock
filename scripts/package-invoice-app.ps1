param([string]$Configuration = "Release")

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\InvoicePrinter.Module\InvoicePrinter.Module.csproj"
$source = Join-Path $repoRoot "src\InvoicePrinter.Module\bin\$Configuration\net10.0"
$artifactDirectory = Join-Path $repoRoot "artifacts\apps"
$bundle = Join-Path $artifactDirectory "InvoicePrinter.appbundle"

dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
if (Test-Path -LiteralPath $bundle) { Remove-Item -LiteralPath $bundle -Force }
Compress-Archive -Path (Join-Path $source "*") -DestinationPath $bundle -CompressionLevel Optimal
Write-Output $bundle
