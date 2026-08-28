param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0",
    [Parameter(Mandatory = $true)]
    [ValidateSet("win-x64", "win-arm64", "osx-x64", "osx-arm64")]
    [string]$RuntimeIdentifier
)

$repoRoot = Split-Path -Parent $PSScriptRoot
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

    Compress-Archive -Path (Join-Path $stagingDirectory "*") -DestinationPath $bundle -CompressionLevel Optimal
    Write-Output $bundle
}
finally {
    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
