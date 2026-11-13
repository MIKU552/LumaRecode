<#
.SYNOPSIS
Samples one already-running application process without altering its working set.
.DESCRIPTION
Selects by full executable path, waits for a comparable process age and records
three samples. Private working set, committed bytes and total resident memory
are separate metrics; this script does not launch or interact with the GUI.
#>
param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$Label,
    [string]$OutputFile,
    [ValidateRange(0,60)][int]$AgeSeconds = 30
)
$ErrorActionPreference='Stop'
$taskPath=[IO.Path]::GetFullPath($Executable)
$taskName=[IO.Path]::GetFileName($taskPath).Replace("'","''")
$taskMatches=@(Get-CimInstance Win32_Process -Filter "Name='$taskName'" | Where-Object { $_.ExecutablePath -ieq $taskPath })
if($taskMatches.Count -ne 1){throw "Expected one process at the given path, found $($taskMatches.Count)."}
$taskProcess=$taskMatches[0]
$taskWait=[int][math]::Ceiling($AgeSeconds-([datetime]::Now-$taskProcess.CreationDate).TotalSeconds)
if($taskWait -gt 0){Start-Sleep -Seconds $taskWait}
$taskSamples=@(for($taskIndex=0;$taskIndex -lt 3;$taskIndex++) {
    $taskCounter=Get-CimInstance Win32_PerfRawData_PerfProc_Process -Filter "IDProcess=$($taskProcess.ProcessId)"
    if(-not $taskCounter){throw 'Process exited or memory counters are unavailable.'}
    $taskCurrent=Get-Process -Id $taskProcess.ProcessId
    [pscustomobject]@{
        process_age_seconds=[math]::Round(([datetime]::Now-$taskProcess.CreationDate).TotalSeconds,3)
        private_working_set_mib=[math]::Round($taskCounter.WorkingSetPrivate/1MB,3)
        private_commit_mib=[math]::Round($taskCounter.PrivateBytes/1MB,3)
        total_working_set_mib=[math]::Round($taskCounter.WorkingSet/1MB,3)
        cpu_seconds=[math]::Round($taskCurrent.TotalProcessorTime.TotalSeconds,3)
        handles=[int]$taskCounter.HandleCount
        threads=[int]$taskCounter.ThreadCount
    }
    if($taskIndex -lt 2){Start-Sleep -Seconds 1}
})
$taskRecord=[ordered]@{label=$Label; target_age_seconds=$AgeSeconds; samples=$taskSamples}
$taskJson=$taskRecord | ConvertTo-Json -Depth 4 -Compress
if($OutputFile){$taskJson | Add-Content -LiteralPath $OutputFile -Encoding utf8}
$taskJson
