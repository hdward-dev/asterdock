param(
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts/release/apps"
}

$applications = @(Get-ChildItem -Path (Join-Path $repoRoot "src/*.Module/app.json") | Sort-Object FullName | ForEach-Object {
    $manifest = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
    # Home is supplied by the host and is not an installable application.
    if ($manifest.id -ne "home") {
        @{ Name = $manifest.id; Project = $_.Directory.Name }
    }
})

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
