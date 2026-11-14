<#
.SYNOPSIS
Checks source formatting, generated resources and core policy regressions.
.DESCRIPTION
Runs from any directory with the SDK in global.json and Python on PATH.
Integration encoding and GUI checks are documented separately in qa/README.md.
#>
param()
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $taskRoot
try {
    function Invoke-Check([string]$Executable, [string[]]$Arguments) {
        & $Executable @Arguments
        if ($LASTEXITCODE -ne 0) { throw "Check failed: $Executable $($Arguments -join ' ')" }
    }
    Invoke-Check dotnet @('format','whitespace','LumaRecode.slnx','--verify-no-changes','--exclude','App/ViewText.g.cs','--verbosity','quiet')
    Invoke-Check dotnet @('format','style','LumaRecode.slnx','--verify-no-changes','--diagnostics','IDE0005','IDE0011','IDE0042','IDE0059','IDE0062','IDE0251','IDE0330','--severity','info','--verbosity','quiet')
    foreach ($taskProject in @('BackendChecks','LanguageChecks','SerializationChecks','PerformanceChecks')) {
        $taskArguments = @('run','--project',"qa/$taskProject/$taskProject.csproj",'-c','Release')
        if ($taskProject -in @('BackendChecks','PerformanceChecks')) { $taskArguments += @('--','--check') }
        Invoke-Check dotnet $taskArguments
    }
    Invoke-Check python @('tools/Update-ViewText.py','--check')
    Invoke-Check python @('tools/generate_hlg_luts.py','--check')
    Invoke-Check python @('qa/DocumentationChecks/check_docs.py')
    Invoke-Check python @('qa/BuildChecks/check_build.py')
} finally { Pop-Location }
