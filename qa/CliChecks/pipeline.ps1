# Exercise a real PowerShell pipeline: the WinExe launcher must preserve both
# UTF-8 standard handles and the child exit status through ConvertFrom-Json.
param([string]$Launcher,[string]$Video)
$ErrorActionPreference='Stop'
$taskResult = & $Launcher probe $Video --no-settings --accept-terms --json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $taskResult.kind -ne 'sdr' -or $taskResult.component_depths[0] -ne 10 -or $taskResult.path -ne $Video) { throw 'PowerShell Unicode probe pipeline failed.' }
$taskError = & $Launcher unknown --json | ConvertFrom-Json
if ($LASTEXITCODE -ne 2 -or $taskError.data.code -ne 'invalid_arguments') { throw 'PowerShell exit code propagation failed.' }
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$taskHelp = & $Launcher --lang zh --help | Out-String
if ($LASTEXITCODE -ne 0 -or $taskHelp -notmatch '同一个 EXE') { throw 'PowerShell UTF-8 help failed.' }
Write-Output 'PowerShell JSON pipeline and LASTEXITCODE passed.'
