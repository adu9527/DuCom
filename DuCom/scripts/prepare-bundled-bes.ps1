# Prepares private BES runtime files for embedding; never adds private sources to Git.
# Returns a directory with <plugin-id>/<version>/<runtime files> for DuCom.Factory resources.
param(
    [string]$Configuration = "Release",
    [string]$PackagePath
)

$ErrorActionPreference = "Stop"
$solutionRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$privateRoot = Join-Path $solutionRoot "plugins\BesDownload"
if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $packScript = Join-Path $privateRoot "packaging\pack.ps1"
    if (-not (Test-Path -LiteralPath $packScript -PathType Leaf)) {
        throw "Private BES build is unavailable. Supply -BesPluginPackage <private.dcpack>, or explicitly use -WithoutBesPlugin for a public build."
    }
    # The private packer owns only these known local artifact directories.
    Write-Host "Building private BES runtime package..."
    & $packScript -Configuration $Configuration | Out-Host
    $manifest = Get-Content -LiteralPath (Join-Path $privateRoot "src\BesDownload.Plugin\plugin.manifest.json") -Raw | ConvertFrom-Json
    $PackagePath = Join-Path $privateRoot "artifacts\internal.ducom.bes-download-$($manifest.version).dcpack"
}
$PackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
try {
    $manifestEntry = $archive.GetEntry("plugin.manifest.json")
    if ($null -eq $manifestEntry) { throw "BES package has no root plugin.manifest.json." }
    $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($manifest.id -cne "internal.ducom.bes-download" -or $manifest.version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$') {
        throw "Expected a versioned internal.ducom.bes-download package."
    }
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        if ($name.StartsWith('/') -or $name.Contains(':') -or ($name.Split('/') -contains '..') -or ($name.Split('/') -contains '.')) {
            throw "Unsafe BES package path: $name"
        }
        if ($name.EndsWith('/')) { continue }
        if (-not $seen.Add($name)) { throw "Duplicate BES package path: $name" }
        if ([System.IO.Path]::GetExtension($name) -notin @('.dll', '.exe', '.json', '.bin', '.txt')) {
            throw "BES package contains a non-runtime file: $name"
        }
    }
    if (-not ($seen | Where-Object { $_ -like 'helpers/win-x86/programmer/*.bin' })) {
        throw "BES package has no programmer BIN resources."
    }
    $requiredFiles = @($manifest.entryAssembly, 'BesDownload.Domain.dll', 'DuCom.Plugin.Sdk.dll', 'helpers/win-x86/BesBurnRunner.exe', 'helpers/win-x86/lib_transfer.dll')
    foreach ($helper in $manifest.nativeHelpers) {
        $requiredFiles += $helper.entryPoint
        $requiredFiles += @($helper.dependencies)
    }
    foreach ($required in $requiredFiles) {
        if ([string]::IsNullOrWhiteSpace($required) -or -not $seen.Contains($required.Replace('\', '/'))) {
            throw "BES package is missing a required runtime file: $required"
        }
    }
    $resourceRoot = Join-Path $solutionRoot ("artifacts\bundled-bes\" + [Guid]::NewGuid().ToString('N'))
    $destination = Join-Path $resourceRoot "$($manifest.id)\$($manifest.version)"
    [System.IO.Directory]::CreateDirectory($destination) | Out-Null
    [System.IO.Compression.ZipFileExtensions]::ExtractToDirectory($archive, $destination)
    Write-Host "Embedding BES $($manifest.version) from private runtime package (no sources or debug symbols)."
    return $resourceRoot
}
finally { $archive.Dispose() }
