<#
.SYNOPSIS
Builds the shared C/C++/Rust core with the same targets used by CI and the IDE.
#>
param(
    [ValidateSet('Native','X64','ARM64')][string]$Target = 'Native',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$Test
)
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'Use build-native-core.sh on macOS.' }
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskNative = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$taskCpu = if ($Target -eq 'Native') { $taskNative } else { $Target.ToLowerInvariant() }
if ($taskCpu -notin @('x64','arm64')) { throw "Unsupported architecture: $taskCpu" }
if ($Test -and $taskCpu -ne $taskNative) { throw 'Execute runtime checks on the matching target device.' }
if ($env:VSCMD_ARG_TGT_ARCH -ne $taskCpu) {
    $taskVswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (!(Test-Path -LiteralPath $taskVswhere)) { throw 'Install Visual Studio with Desktop development with C++, or enter its developer shell.' }
    $taskVs = & $taskVswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (!$taskVs) { throw 'No suitable Visual Studio C++ installation found.' }
    Import-Module (Join-Path $taskVs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
    Enter-VsDevShell -VsInstallPath $taskVs -SkipAutomaticLocation -DevCmdArguments "-arch=$taskCpu -host_arch=$taskNative"
}
function Invoke-NativeBuild([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Native build failed: $Executable" }
}
$taskTriple = if ($taskCpu -eq 'arm64') { 'aarch64-pc-windows-msvc' } else { 'x86_64-pc-windows-msvc' }
$taskOutput = Join-Path $taskRoot "out/native-$taskCpu-$($Configuration.ToLowerInvariant())"
Push-Location -LiteralPath $taskRoot
try {
    Invoke-NativeBuild rustup @('target','add',$taskTriple)
    $taskConfigure = @('--preset',"native-$($Configuration.ToLowerInvariant())",'-B',$taskOutput,"-DLUMA_RUST_TARGET=$taskTriple")
    if ($taskCpu -ne $taskNative) { $taskConfigure += @('-DCMAKE_SYSTEM_NAME=Windows',"-DCMAKE_SYSTEM_PROCESSOR=$taskCpu") }
    Invoke-NativeBuild cmake $taskConfigure
    Invoke-NativeBuild cmake @('--build',$taskOutput)
    if ($Test) { Invoke-NativeBuild ctest @('--test-dir',$taskOutput,'--output-on-failure') }
} finally { Pop-Location }
Write-Output "Native core: $taskOutput/engine/LumaRecodeCore.dll"
