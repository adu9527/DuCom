# Publishes the portable single-file DuCom.exe for GitHub Releases.
# Usage:  .\publish-portable.ps1 [-Configuration Release]
# After publishing, create a GitHub release with tag V<Version> (for example V0.0.0.4)
# and upload artifacts\portable\DuCom.exe WITHOUT renaming it - the in-app portable
# updater looks for an asset named exactly "DuCom.exe".
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$solutionRoot = Resolve-Path (Join-Path $scriptDirectory "..")
$csproj = Join-Path $solutionRoot "src\DuCom\DuCom.csproj"
$outputDirectory = Join-Path $solutionRoot "artifacts\portable"

[xml]$project = Get-Content -LiteralPath $csproj
$version = @($project.Project.PropertyGroup | Where-Object { $_.Version })[0].Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Could not read <Version> from $csproj"
}

Write-Host "DuCom version: $version"
Write-Host "Publishing portable single-file build..."

dotnet publish $csproj `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $outputDirectory

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$portableExe = Join-Path $outputDirectory "DuCom.exe"
if (-not (Test-Path -LiteralPath $portableExe)) {
    throw "Expected output was not found: $portableExe"
}

Write-Host ""
Write-Host "Portable build created: $portableExe"
Write-Host "Next steps:"
Write-Host "  1. Create a GitHub release with tag V$version"
Write-Host "  2. Upload the exe ASSET NAMED EXACTLY 'DuCom.exe' (rename before uploading if needed)"
Write-Host "  3. Put the change log into the release description; it is shown in the update window"
