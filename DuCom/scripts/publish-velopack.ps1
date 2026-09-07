# Publishes DuCom and packs it with Velopack (installer + portable zip + update packages).
# Usage:  .\publish-velopack.ps1 [-Configuration Release]
# Upload EVERYTHING from artifacts\velopack-releases to the SAME GitHub release (tag V<Version>):
#   - DuCom-win-Setup.exe          installer for new users
#   - DuCom-win-portable.zip       portable archive for new users
#   - DuCom-win-full-<v>.nupkg     REQUIRED - the in-app Velopack updater downloads this
#   - (optional) DuCom-win-delta-<v>.nupkg, assets.{channel}.json
# Velopack requires a three-part semver. The four-part product version V a.b.c.d is mapped
# to a.b.(c*1000+d) so ordering always matches the full version.
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$solutionRoot = Resolve-Path (Join-Path $scriptDirectory "..")
$csproj = Join-Path $solutionRoot "src\DuCom\DuCom.csproj"
$publishDirectory = Join-Path $solutionRoot "artifacts\velopack-publish"
$outputDirectory = Join-Path $solutionRoot "artifacts\velopack-releases"

[xml]$project = Get-Content -LiteralPath $csproj
$version = @($project.Project.PropertyGroup | Where-Object { $_.Version })[0].Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Could not read <Version> from $csproj"
}

$parts = $version.Split('.')
$major = [int]$parts[0]
$minor = [int]$parts[1]
$patch = [int]$parts[2]
$revision = if ($parts.Count -gt 3) { [int]$parts[3] } else { 0 }
$packVersion = "$major.$minor.$($patch * 1000 + $revision)"

Write-Host "DuCom version: $version (Velopack pack version: $packVersion)"
Write-Host "Publishing self-contained single-file build..."

dotnet publish $csproj `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $publishDirectory

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Host "vpk tool not found; installing globally..."
    dotnet tool install -g vpk
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to install the vpk dotnet tool"
    }
}

if (Test-Path -LiteralPath $outputDirectory) {
    Remove-Item -LiteralPath $outputDirectory -Recurse -Force
}

Write-Host "Packing with Velopack..."
vpk pack `
    --packId DuCom `
    --packVersion $packVersion `
    --packTitle DuCom `
    --packDir $publishDirectory `
    --mainExe DuCom.exe `
    --outputDir $outputDirectory

if ($LASTEXITCODE -ne 0) {
    throw "vpk pack failed with exit code $LASTEXITCODE"
}

Write-Host ""
Write-Host "Velopack outputs created in: $outputDirectory"
Get-ChildItem -LiteralPath $outputDirectory | ForEach-Object { Write-Host "  $($_.Name)" }
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Create a GitHub release with tag V$version"
Write-Host "  2. Upload ALL files from $outputDirectory to that release"
Write-Host "     (the in-app Velopack updater needs the -full- nupkg asset)"
Write-Host "  3. Upload the portable DuCom.exe asset too so portable users can update"
