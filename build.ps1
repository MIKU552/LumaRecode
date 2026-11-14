<#
.SYNOPSIS
Builds a Native, APX or ARM64 single-file GUI/CLI application.
.DESCRIPTION
Each target has isolated restore, payload, license and publish directories.
Native AOT targets the build CPU by default; APX explicitly requires APX hardware.
ARM64 cross-compilation requires the Visual Studio C++ ARM64 toolchain.
#>
param(
    [ValidateSet('Native','Apx','Arm64')][string]$Target = 'Native',
    [ValidateSet('SelfContained','FrameworkDependent')][string]$Deployment = 'SelfContained',
    [ValidateSet('Aot','Jit')][string]$Compilation = 'Aot',
    [switch]$UseEnvironmentalNativeTools
)
$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $PSScriptRoot
try {
    $Target = @{native='Native';apx='Apx';arm64='Arm64'}[$Target.ToLowerInvariant()]
    $taskArchitecture = if ($Target -eq 'Arm64') { 'arm64' } else { 'x64' }
    $taskPlatform = if ($Target -eq 'Arm64') { 'ARM64' } else { 'x64' }
    $taskIsa = @{Native='native';Apx='apx';Arm64='armv8-a'}[$Target]
    $taskRid = "win-$taskArchitecture"
    $taskBundled = $Deployment -eq 'SelfContained'
    $taskAot = $taskBundled -and $Compilation -eq 'Aot'
    if ($Target -eq 'Apx' -and -not $taskAot) { throw 'The APX target requires self-contained Native AOT.' }
    $taskRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $taskOutputRelative = if ($Target -eq 'Native') { "dist/$Deployment" } else { "dist/$Deployment-$Target" }
    $taskPayloadRelative = "dist/NativeAot/$Target"
    $taskOutput = Join-Path $taskRoot $taskOutputRelative
    $taskPayload = Join-Path $taskRoot $taskPayloadRelative
    $taskLegal = Join-Path $taskRoot "build-cache/licenses/$Target"

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
    $taskCommon = @('-r',$taskRid,"-p:Platform=$taskPlatform","-p:LumaBuildTarget=$Target")
    Invoke-DotNet (@('restore','App/VideoConverter.WinUI.csproj','--locked-mode',"-p:PublishAot=$($taskAot.ToString().ToLowerInvariant())") + $taskCommon)
    $taskLicenseArgs = @{
        ProjectRoot=$taskRoot;Architecture=$taskArchitecture;OutputDirectory=$taskLegal
        AssetsFile=(Join-Path $taskRoot "App/obj/$Target/project.assets.json")
    }
    if ($taskAot) {
        Invoke-DotNet (@('restore','Bundle/LumaRecode.Bundle.csproj','--locked-mode') + $taskCommon)
        $taskLicenseArgs.Add('AdditionalAssetsFile',(Join-Path $taskRoot "Bundle/obj/$Target/project.assets.json"))
    }
    & (Join-Path $taskRoot 'tools/Collect-Licenses.ps1') @taskLicenseArgs
    Reset-GeneratedDirectory $taskOutputRelative
    if ($taskAot) { Reset-GeneratedDirectory $taskPayloadRelative }
    $taskAppOutput = if ($taskAot) { $taskPayload } else { $taskOutput }
    $taskPublish = @('publish','App/VideoConverter.WinUI.csproj','--no-restore','-c','Release','-o',$taskAppOutput,
        "-p:PublishAot=$($taskAot.ToString().ToLowerInvariant())","-p:LumaLicenseDirectory=$taskLegal") + $taskCommon
    foreach ($taskName in @('SelfContained','WindowsAppSDKSelfContained')) {
        $taskPublish += "-p:$taskName=$($taskBundled.ToString().ToLowerInvariant())"
    }
    foreach ($taskName in @('PublishSingleFile','IncludeAllContentForSelfExtract','EnableCompressionInSingleFile')) {
        $taskPublish += "-p:$taskName=$(($taskBundled -and -not $taskAot).ToString().ToLowerInvariant())"
    }
    if ($taskAot) { $taskPublish += "-p:IlcInstructionSet=$taskIsa" }
    if ($taskAot -and $UseEnvironmentalNativeTools) { $taskPublish += '-p:IlcUseEnvironmentalTools=true' }
    Invoke-DotNet $taskPublish
    if ($taskAot) {
        Invoke-DotNet (@('publish','Bundle/LumaRecode.Bundle.csproj','--no-restore','-c','Release','-o',$taskOutput,
            "-p:LumaPayloadDirectory=$taskPayload") + $taskCommon)
    }
    $taskVersion = ([xml](Get-Content -LiteralPath 'App/VideoConverter.WinUI.csproj' -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if ($taskBundled) { Write-Output "Built $Target ($taskRid): $taskOutput/LumaRecode.exe" }
    else {
        $taskZip = Join-Path $taskRoot "dist/LumaRecode-$taskVersion-$taskRid-framework-dependent.zip"
        Compress-Archive -Path "$taskOutput/*" -DestinationPath $taskZip -Force
        Write-Output "Built framework-dependent ZIP: $taskZip"
    }
} finally { Pop-Location }
