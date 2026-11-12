param([string]$Launcher,[string]$Video)
$ErrorActionPreference='Stop'
$taskResult = & $Launcher probe $Video --no-settings --accept-terms --json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $taskResult.kind -ne 'sdr' -or $taskResult.component_depths[0] -ne 10) { throw 'PowerShell probe pipeline failed.' }
$taskError = & $Launcher unknown --json | ConvertFrom-Json
if ($LASTEXITCODE -ne 2 -or $taskError.data.code -ne 'invalid_arguments') { throw 'PowerShell exit code propagation failed.' }
Write-Output 'PowerShell JSON pipeline and LASTEXITCODE passed.'
