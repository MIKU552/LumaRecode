<#
.SYNOPSIS
Resolves automatic and explicit build targets without starting a build.
#>
function Get-LumaBuildHost {
    if (-not $IsWindows) { throw 'LumaRecode builds require Windows.' }
    if (-not ('LumaRecode.BuildHost' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LumaRecode {
    public static class BuildHost {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsProcessorFeaturePresent(uint feature);

        public static string Architecture {
            get {
                ushort processMachine, nativeMachine;
                // Query the OS architecture, including when pwsh runs under emulation.
                if (!IsWow64Process2(new IntPtr(-1), out processMachine, out nativeMachine))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                switch (nativeMachine) {
                    case 0x8664: return "X64";
                    case 0xAA64: return "Arm64";
                    default: throw new PlatformNotSupportedException("An x64 or ARM64 Windows system is required.");
                }
            }
        }

        // This includes the OS enablement needed to execute APX instructions.
        public static bool ApxAvailable { get { return IsProcessorFeaturePresent(90); } }
    }
}
'@
    }
    [pscustomobject]@{
        Architecture = [LumaRecode.BuildHost]::Architecture
        ApxAvailable = [LumaRecode.BuildHost]::ApxAvailable
    }
}

function Resolve-LumaBuildTarget {
    param(
        [ValidateSet('Native','X64','Apx','Arm64')][string]$Target = 'Native',
        [ValidateSet('Jit','Aot')][string]$Compilation = 'Jit',
        [Parameter(Mandatory=$true)][ValidateSet('X64','Arm64')][string]$SystemArchitecture,
        [Parameter(Mandatory=$true)][bool]$ApxAvailable
    )
    $taskRequested = @{native='Native';x64='X64';apx='Apx';arm64='Arm64'}[$Target.ToLowerInvariant()]
    if ($taskRequested -eq 'Apx' -and $Compilation -ne 'Aot') { throw 'The APX target requires -Compilation Aot. X64 JIT uses runtime CPU detection.' }
    $taskResolved = if ($taskRequested -ne 'Native') { $taskRequested }
        elseif ($SystemArchitecture -eq 'Arm64') { 'Arm64' }
        elseif ($Compilation -eq 'Aot' -and $ApxAvailable) { 'Apx' }
        else { 'X64' }
    $taskArchitecture = if ($taskResolved -eq 'Arm64') { 'arm64' } else { 'x64' }
    [pscustomobject]@{
        RequestedTarget = $taskRequested
        ResolvedTarget = $taskResolved
        Architecture = $taskArchitecture
        Platform = if ($taskArchitecture -eq 'arm64') { 'ARM64' } else { 'x64' }
        RuntimeIdentifier = "win-$taskArchitecture"
        InstructionSet = if ($Compilation -eq 'Jit') { 'jit' }
            elseif ($taskRequested -eq 'Native') { 'native' }
            else { @{X64='x86-64-v2';Apx='apx';Arm64='armv8-a'}[$taskResolved] }
    }
}
