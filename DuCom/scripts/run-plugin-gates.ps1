# Automated DPP/1 checks: unit + integration tests (sandboxed workers), the published-host
# plugin smoke, and the example .dcpack build. Run from the solution root.
# Usage:  powershell -File scripts\run-plugin-gates.ps1 [-Configuration Release]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$solutionRoot = Resolve-Path (Join-Path $scriptDirectory "..")

Write-Host "== [1/5] Building solution =="
dotnet build (Join-Path $solutionRoot "DuCom.slnx") -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Solution build failed." }

Write-Host "== [2/5] Publishing single-file host (sandbox worker form) =="
& (Join-Path $scriptDirectory "build-worker-host.ps1") -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "Worker host publish failed." }

Write-Host "== [3/5] Running plugin tests (spawns real sandboxed workers) =="
dotnet test (Join-Path $solutionRoot "tests\DuCom.Plugins.Tests\DuCom.Plugins.Tests.csproj") -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "Plugin tests failed." }

Write-Host "== [4/5] Plugin smoke from the published host =="
$hostExecutable = Join-Path $solutionRoot "artifacts\worker-host\DuCom.exe"
$smokePluginRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("DuCom-plugin-smoke-" + [Guid]::NewGuid().ToString("N"))
$previousPluginRoot = $env:DUCOM_PLUGIN_ROOT
$env:DUCOM_PLUGIN_ROOT = $smokePluginRoot
$process = Start-Process -FilePath $hostExecutable -ArgumentList "--plugins-smoke" -PassThru -WindowStyle Hidden
if (-not $process.WaitForExit(120000)) {
    $process.Kill()
    throw "Plugins smoke timed out."
}
if ($process.ExitCode -ne 0) { throw "Plugins smoke failed with exit code $($process.ExitCode)." }
Remove-Item -LiteralPath $smokePluginRoot -Recurse -Force -ErrorAction SilentlyContinue
$env:DUCOM_PLUGIN_ROOT = $previousPluginRoot
Write-Host "Plugins smoke passed."

Write-Host "== [5/5] Building example package =="
& (Join-Path $scriptDirectory "pack-example-plugin.ps1") -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "Example package build failed." }

Write-Host ""
Write-Host "Automated checks passed. Review skipped tests and the remaining protocol release gates separately."
