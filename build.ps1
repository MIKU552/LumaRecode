<#
.SYNOPSIS
Builds an automatic Native, explicit x64, APX or ARM64 GUI/CLI application.
.DESCRIPTION
Each target has isolated restore, payload, license and publish directories.
Native AOT is the default. Native selects the build CPU's instruction set.
Select JIT for a framework-dependent application.
ARM64 AOT cross-compilation requires the Visual Studio C++ ARM64 toolchain.
#>
param(
    [ValidateSet('Native','X64','Apx','Arm64')][string]$Target = 'Native',
    [ValidateSet('SelfContained','Portable','FrameworkDependent')][string]$Deployment = 'SelfContained',
    [ValidateSet('Jit','Aot')][string]$Compilation = 'Aot',
    [switch]$SkipArchive,
    [switch]$UseEnvironmentalNativeTools
)
$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $PSScriptRoot
try {
    . (Join-Path $PSScriptRoot 'tools/BuildTarget.ps1')
    if ($Compilation -eq 'Aot' -and $Deployment -eq 'FrameworkDependent') { throw 'Use -Compilation Jit for a framework-dependent deployment.' }
    $taskHost = Get-LumaBuildHost
    $taskPlan = Resolve-LumaBuildTarget -Target $Target -Compilation $Compilation -SystemArchitecture $taskHost.Architecture -ApxAvailable $taskHost.ApxAvailable
    $Target = $taskPlan.RequestedTarget
    $taskArchitecture = $taskPlan.Architecture
    $taskPlatform = $taskPlan.Platform
    $taskIsa = $taskPlan.InstructionSet
    $taskRid = $taskPlan.RuntimeIdentifier
    $taskBundled = $Deployment -eq 'SelfContained'
    $taskIncludeRuntime = $Deployment -ne 'FrameworkDependent'
    $taskAot = $Compilation -eq 'Aot'
    if ($taskPlan.ResolvedTarget -eq 'Apx' -and -not $taskAot) { throw 'The APX target requires self-contained Native AOT.' }
    if ($Target -eq 'Native' -and $taskAot) {
        # The AOT compiler cannot infer another architecture's native extensions.
        # Detect an emulated SDK before restoring or removing any output files.
        $taskSdkRid = & dotnet msbuild Core/LumaRecode.Core.csproj -nologo -getProperty:NETCoreSdkRuntimeIdentifier
        if ($LASTEXITCODE -ne 0) { throw 'Cannot determine the .NET SDK architecture.' }
        if (($taskSdkRid -join '').Trim() -ne $taskRid) {
            throw "Native CPU optimization requires the $taskRid .NET SDK. Use that SDK or select -Target $($taskPlan.ResolvedTarget) for cross-compilation."
        }
    }
    Write-Output "Target: $Target -> $($taskPlan.ResolvedTarget) ($taskRid), compilation: $Compilation, instruction set: $taskIsa"
    $taskRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $taskOutputRelative = if ($Target -eq 'Native') { "dist/$Deployment" } else { "dist/$Deployment-$Target" }
    if ($taskAot) { $taskOutputRelative = $taskOutputRelative.Replace('dist/','dist/Aot-') }
    $taskPayloadRelative = "dist/NativeAot/$Target"
    $taskOutput = Join-Path $taskRoot $taskOutputRelative
    $taskPayload = Join-Path $taskRoot $taskPayloadRelative
    $taskLegal = Join-Path $taskRoot "build-cache/licenses/$Compilation/$Target"

    function Invoke-DotNet([string[]]$Arguments) {
        & dotnet @Arguments
        if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed." }
    }
    function Reset-GeneratedDirectory([string]$RelativePath) {
        if ($RelativePath -notin @($taskOutputRelative,$taskPayloadRelative)) { throw 'Unexpected generated directory.' }
        $taskPath = [IO.Path]::GetFullPath((Join-Path $taskRoot $RelativePath))
        if (-not $taskPath.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid generated directory.' }
        for ($taskAncestor = $taskPath; $taskAncestor -ne $taskRoot; $taskAncestor = [IO.Path]::GetDirectoryName($taskAncestor)) {
            if ((Test-Path -LiteralPath $taskAncestor) -and ((Get-Item -LiteralPath $taskAncestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Generated directory uses a reparse point.' }
        }
        if (Test-Path -LiteralPath $taskPath) { Remove-Item -LiteralPath $taskPath -Recurse -Force }
        New-Item -ItemType Directory -Path $taskPath -Force | Out-Null
    }

    Invoke-DotNet @('run','--project','qa/LanguageChecks/LanguageChecks.csproj','-c','Release')
    & (Join-Path $taskRoot 'tools/Build-NativeCore.ps1') -Target $taskArchitecture -Configuration Release
    $taskCommon = @('-r',$taskRid,"-p:Platform=$taskPlatform","-p:LumaBuildTarget=$Target","-p:LumaResolvedTarget=$($taskPlan.ResolvedTarget)","-p:LumaCompilation=$Compilation")
    Invoke-DotNet (@('restore','App/VideoConverter.WinUI.csproj','--locked-mode',"-p:PublishAot=$($taskAot.ToString().ToLowerInvariant())") + $taskCommon)
    $taskLicenseArgs = @{
        ProjectRoot=$taskRoot;Architecture=$taskArchitecture;OutputDirectory=$taskLegal
        AssetsFile=(Join-Path $taskRoot "App/obj/$Compilation/$Target/project.assets.json")
    }
    if ($taskAot -and $taskBundled) {
        Invoke-DotNet (@('restore','Bundle/LumaRecode.Bundle.csproj','--locked-mode') + $taskCommon)
        $taskLicenseArgs.Add('AdditionalAssetsFile',(Join-Path $taskRoot "Bundle/obj/$Compilation/$Target/project.assets.json"))
    }
    & (Join-Path $taskRoot 'tools/Collect-Licenses.ps1') @taskLicenseArgs
    Reset-GeneratedDirectory $taskOutputRelative
    if ($taskAot -and $taskBundled) { Reset-GeneratedDirectory $taskPayloadRelative }
    $taskAppOutput = if ($taskAot -and $taskBundled) { $taskPayload } else { $taskOutput }
    $taskPublish = @('publish','App/VideoConverter.WinUI.csproj','--no-restore','-c','Release','-o',$taskAppOutput,
        "-p:PublishAot=$($taskAot.ToString().ToLowerInvariant())","-p:LumaLicenseDirectory=$taskLegal") + $taskCommon
    # Keep WinUI app-local in every format. FrameworkDependent only changes
    # how CoreCLR is found, so desktop users have a single external runtime.
    $taskPublish += "-p:SelfContained=$($taskIncludeRuntime.ToString().ToLowerInvariant())"
    $taskPublish += '-p:WindowsAppSDKSelfContained=true'
    foreach ($taskName in @('PublishSingleFile','IncludeAllContentForSelfExtract','EnableCompressionInSingleFile')) {
        $taskPublish += "-p:$taskName=$(($taskBundled -and -not $taskAot).ToString().ToLowerInvariant())"
    }
    if ($taskAot) { $taskPublish += "-p:IlcInstructionSet=$taskIsa" }
    if ($taskAot -and $UseEnvironmentalNativeTools) { $taskPublish += '-p:IlcUseEnvironmentalTools=true' }
    Invoke-DotNet $taskPublish
    if ($taskAot -and $taskBundled) {
        Invoke-DotNet (@('publish','Bundle/LumaRecode.Bundle.csproj','--no-restore','-c','Release','-o',$taskOutput,
            "-p:LumaPayloadDirectory=$taskPayload") + $taskCommon)
    }
    $taskVersion = ([xml](Get-Content -LiteralPath 'App/VideoConverter.WinUI.csproj' -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if ($taskBundled -or $SkipArchive) { Write-Output "Built $Target ($taskRid): $taskOutput/LumaRecode.exe" }
    else {
        $taskKind = if ($taskAot) { 'aot-portable' } elseif ($taskIncludeRuntime) { 'portable' } else { 'framework-dependent' }
        $taskZip = Join-Path $taskRoot "dist/LumaRecode-$taskVersion-$taskRid-$taskKind.zip"
        Compress-Archive -Path "$taskOutput/*" -DestinationPath $taskZip -Force
        Write-Output "Built $taskKind ZIP: $taskZip"
    }
} finally { Pop-Location }
