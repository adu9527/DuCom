param(
    [Parameter(Mandatory = $true)]
    [string]$Port,

    [Parameter(Mandatory = $true)]
    [int]$BaudRate,

    [int]$DurationMinutes = 10,

    [string]$Device = "Unknown",

    [string]$Notes = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$reportDirectory = Join-Path $repoRoot "reports\hardware"
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$reportBase = Join-Path $reportDirectory "hardware-$timestamp-$Port"
$systemLogDirectory = Join-Path $repoRoot "src\DuCom\bin\Debug\net10.0-windows\Logs\System_log"
$sessionLogDirectory = Join-Path $repoRoot "src\DuCom\bin\Debug\net10.0-windows\Logs"

New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null

$start = Get-Date
Write-Host "DuCom hardware gate"
Write-Host "Port: $Port"
Write-Host "Baud: $BaudRate"
Write-Host "Device: $Device"
Write-Host "Duration: $DurationMinutes minute(s)"
Write-Host ""
Write-Host "1. Start DuCom and connect $Port at $BaudRate."
Write-Host "2. Keep the device producing its normal/high-rate workload."
Write-Host "3. Exercise scrolling, selection, copying, freeze, and disconnect."
Write-Host "4. Return here after at least $DurationMinutes minute(s)."
Read-Host "Press Enter when the hardware run is complete"
$end = Get-Date

$latestSystemLog = Get-ChildItem -LiteralPath $systemLogDirectory -Filter "ducom-*.log" -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
$sessionLogs = Get-ChildItem -LiteralPath $sessionLogDirectory -Filter "$Port-*.txt" -File -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -ge $start.AddMinutes(-1) } |
    Sort-Object LastWriteTime

$result = Read-Host "Result (Pass/Fail/Observation)"
$observedFaults = Read-Host "Observed faults or data loss (None if not observed)"
$systemLogPath = if ($null -ne $latestSystemLog) { $latestSystemLog.FullName } else { $null }
$sessionLogArray = @($sessionLogs)
$report = [ordered]@{
    SchemaVersion = 1
    RecordedAt = (Get-Date).ToString("o")
    Port = $Port
    BaudRate = $BaudRate
    Device = $Device
    RequestedDurationMinutes = $DurationMinutes
    StartedAt = $start.ToString("o")
    FinishedAt = $end.ToString("o")
    ActualDurationSeconds = [math]::Round(($end - $start).TotalSeconds, 1)
    Result = $result
    ObservedFaultsOrLoss = $observedFaults
    Notes = $Notes
    SystemLog = $systemLogPath
    SessionLogs = @($sessionLogArray | ForEach-Object {
        [ordered]@{
            Path = $_.FullName
            Bytes = $_.Length
            LastWriteTime = $_.LastWriteTime.ToString("o")
        }
    })
}

$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$reportBase.json" -Encoding utf8

$markdown = @(
    "# DuCom Hardware Gate"
    ""
    "- Recorded: $($report.RecordedAt)"
    "- Port: $Port"
    "- Baud rate: $BaudRate"
    "- Device: $Device"
    "- Requested duration: $DurationMinutes minute(s)"
    "- Actual duration: $($report.ActualDurationSeconds) second(s)"
    "- Result: $result"
    "- Observed faults or loss: $observedFaults"
    "- System log: $($report.SystemLog)"
    "- Notes: $Notes"
    ""
    "## Session Logs"
    ""
)
if ($sessionLogArray.Count -eq 0) {
    $markdown += "No matching session log was found."
} else {
    foreach ($file in $sessionLogArray) {
        $markdown += "- ``$($file.FullName)`` - $($file.Length) bytes"
    }
}
$markdown | Set-Content -LiteralPath "$reportBase.md" -Encoding utf8

Write-Host "Hardware report written:"
Write-Host "$reportBase.json"
Write-Host "$reportBase.md"
