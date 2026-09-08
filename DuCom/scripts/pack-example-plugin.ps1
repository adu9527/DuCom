param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "samples\Example.HelloDuCom\Example.HelloDuCom.csproj"
$staging = Join-Path $root "samples\Example.HelloDuCom\artifacts\pack"

Write-Host "Building example plugin ($Configuration)..."
dotnet build $project -c $Configuration | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Example plugin build failed." }

$output = Join-Path $root "samples\Example.HelloDuCom\bin\$Configuration\net10.0"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force $staging | Out-Null

foreach ($file in @("Example.HelloDuCom.dll", "DuCom.Plugin.Sdk.dll", "plugin.manifest.json")) {
    Copy-Item (Join-Path $output $file) $staging
}

$packPath = Join-Path $root "samples\Example.HelloDuCom\artifacts\org.example.hello-ducom-1.0.0.dcpack"
if (Test-Path $packPath) { Remove-Item $packPath -Force }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($packPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in @("Example.HelloDuCom.dll", "DuCom.Plugin.Sdk.dll", "plugin.manifest.json")) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $staging $file), $file) | Out-Null
    }
}
finally { $zip.Dispose() }
Remove-Item $staging -Recurse -Force
Write-Host "Packed: $packPath"
