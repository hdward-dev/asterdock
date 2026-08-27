param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Version = "1.0.0",
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts/release/$RuntimeIdentifier"
}

$architecture = if ($RuntimeIdentifier -eq "win-arm64") { "arm64" } else { "x64" }
$temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "asterdock-$([System.Guid]::NewGuid().ToString('N'))"
$publishDirectory = Join-Path $temporaryDirectory "publish"
$installerPath = Join-Path $OutputDirectory "AsterDock-$RuntimeIdentifier.msi"
$projectPath = Join-Path $repoRoot "src/AsterDock.Host/AsterDock.Host.csproj"
$wixSource = Join-Path $repoRoot "build/windows/AsterDock.wxs"

New-Item -ItemType Directory -Path $publishDirectory, $OutputDirectory -Force | Out-Null

try {
    dotnet publish $projectPath `
        --configuration Release `
        --runtime $RuntimeIdentifier `
        --self-contained true `
        --output $publishDirectory `
        -p:PublishSingleFile=true `
        -p:BundleApplications=false `
        -p:Version=$Version
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    dotnet tool run wix build $wixSource `
        -arch $architecture `
        -d "PublishDir=$publishDirectory" `
        -d "ProductVersion=$Version" `
        -o $installerPath
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Output $installerPath
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}
