param(
    [string]$Configuration = "Release"
)

# Publishes the self-contained single-file host used for plugin worker testing and local
# plugin development. Workers run the same executable in an AppContainer; only the
# self-contained single-file form embeds the runtime, which the sandbox requires (the
# machine-wide dotnet install is not readable from an AppContainer without elevation).
$ErrorActionPreference = "Stop"
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$solutionRoot = Resolve-Path (Join-Path $scriptDirectory "..")
$csproj = Join-Path $solutionRoot "src\DuCom\DuCom.csproj"
$output = Join-Path $solutionRoot "artifacts\worker-host"

dotnet publish $csproj `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $output

if ($LASTEXITCODE -ne 0) { throw "Host publish failed." }
Write-Host "Host published to $output"
