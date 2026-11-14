<#
.SYNOPSIS
Samples FFmpeg children owned by one GUI process during preview interactions.
.DESCRIPTION
Uses process counters only. Peak working set includes native allocations;
the script does not launch, trim or interact with the application.
#>
param(
    [Parameter(Mandatory=$true)][int]$RootProcessId,
    [Parameter(Mandatory=$true)][string]$Label,
    [ValidateRange(1,60)][int]$DurationSeconds = 30,
    [string]$OutputFile
)
$ErrorActionPreference = 'Stop'
$taskTimer = [Diagnostics.Stopwatch]::StartNew()
$taskPeaks = @{}
while ($taskTimer.Elapsed.TotalSeconds -lt $DurationSeconds) {
    # PATH entries may be launch shims. Include their FFmpeg descendants rather
    # than mistaking a small wrapper process for the decoder's working set.
    $taskCandidates = @(Get-CimInstance Win32_Process -Filter "Name='ffmpeg.exe'")
    $taskFamily = [Collections.Generic.HashSet[uint32]]::new()
    [void]$taskFamily.Add([uint32]$RootProcessId)
    do {
        $taskAdded = $false
        foreach ($taskCandidate in $taskCandidates) {
            if ($taskFamily.Contains([uint32]$taskCandidate.ParentProcessId)) {
                $taskAdded = $taskFamily.Add([uint32]$taskCandidate.ProcessId) -or $taskAdded
            }
        }
    } while ($taskAdded)
    $taskCandidates | Where-Object { $taskFamily.Contains([uint32]$_.ProcessId) } | ForEach-Object {
        $taskChild = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
        if ($taskChild) {
            $taskKey = [string]$_.ProcessId
            try { $taskPeaks[$taskKey] = [Math]::Max([long]$taskPeaks[$taskKey], $taskChild.PeakWorkingSet64) }
            catch [InvalidOperationException] { } # The child may exit between counter reads.
        }
    }
    Start-Sleep -Milliseconds 90
}
$taskRecord = [ordered]@{label=$Label;duration_seconds=$DurationSeconds;ffmpeg_peak_working_set_bytes=$taskPeaks}
$taskJson = $taskRecord | ConvertTo-Json -Depth 4 -Compress
if ($OutputFile) { $taskJson | Add-Content -LiteralPath $OutputFile -Encoding utf8 }
$taskJson
