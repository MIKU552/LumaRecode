param(
    [ValidateSet('SelfContained','FrameworkDependent')][string]$Deployment = 'SelfContained',
    [ValidateSet('Aot','Jit')][string]$Compilation = 'Aot',
    [switch]$UseEnvironmentalNativeTools
)
$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $PSScriptRoot
try {
    # Fail before publishing if the static localization map no longer matches XAML.
    dotnet run --project qa/LanguageChecks/LanguageChecks.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'View localization/resource checks failed.' }
    $taskBundled = $Deployment -eq 'SelfContained'
    $taskAot = $taskBundled -and $Compilation -eq 'Aot'
    $taskRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    function Reset-GeneratedDirectory([string]$RelativePath) {
        $taskTarget = [IO.Path]::GetFullPath((Join-Path $taskRoot $RelativePath))
        if (-not $taskTarget.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid generated directory.' }
        if ($RelativePath -notin @('dist/SelfContained','dist/FrameworkDependent','dist/NativeAot')) { throw 'Unexpected generated directory.' }
        if (Test-Path -LiteralPath $taskTarget) { Remove-Item -LiteralPath $taskTarget -Recurse -Force }
        New-Item -ItemType Directory -Path $taskTarget -Force | Out-Null
    }
    $taskOutput = Join-Path $taskRoot "dist/$Deployment"
    Reset-GeneratedDirectory "dist/$Deployment"
    $taskRestore = @('restore','App/VideoConverter.WinUI.csproj','-r','win-x64','--locked-mode',"-p:PublishAot=$($taskAot.ToString().ToLowerInvariant())")
    if (Test-Path -LiteralPath 'build-cache/nuget') {
        $taskRestore += @('--source','https://api.nuget.org/v3/index.json','--source',(Join-Path $taskRoot 'build-cache/nuget'))
    }
    dotnet @taskRestore
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
    $taskLicenseArgs = @{ProjectRoot=$taskRoot}
    if ($taskAot) {
        dotnet restore Bundle/LumaRecode.Bundle.csproj -r win-x64 --locked-mode
        if ($LASTEXITCODE -ne 0) { throw 'Bundle dependency restore failed.' }
        $taskLicenseArgs.Add('AdditionalAssetsFile',(Join-Path $taskRoot 'Bundle/obj/project.assets.json'))
    }
    & (Join-Path $taskRoot 'tools/Collect-Licenses.ps1') @taskLicenseArgs
    $taskAppOutput = if ($taskAot) { Join-Path $taskRoot 'dist/NativeAot' } else { $taskOutput }
    if ($taskAot) { Reset-GeneratedDirectory 'dist/NativeAot' }
    $taskPublish = @('publish','App/VideoConverter.WinUI.csproj','--no-restore','-c','Release','-r','win-x64','-p:Platform=x64',"-p:PublishAot=$($taskAot.ToString().ToLowerInvariant())",'-o',$taskAppOutput)
    foreach ($taskName in @('SelfContained','WindowsAppSDKSelfContained')) {
        $taskPublish += "-p:$taskName=$($taskBundled.ToString().ToLowerInvariant())"
    }
    foreach ($taskName in @('PublishSingleFile','IncludeAllContentForSelfExtract','EnableCompressionInSingleFile')) {
        $taskPublish += "-p:$taskName=$(($taskBundled -and -not $taskAot).ToString().ToLowerInvariant())"
    }
    if ($taskAot -and $UseEnvironmentalNativeTools) { $taskPublish += '-p:IlcUseEnvironmentalTools=true' }
    dotnet @taskPublish
    if ($LASTEXITCODE -ne 0) { throw 'WinUI publish failed.' }
    if ($taskAot) {
        dotnet publish Bundle/LumaRecode.Bundle.csproj --no-restore -c Release -r win-x64 -o $taskOutput
        if ($LASTEXITCODE -ne 0) { throw 'Single-file bundle publish failed.' }
    }
    $taskVersion = ([xml](Get-Content -LiteralPath 'App/VideoConverter.WinUI.csproj' -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if ($taskBundled) { Write-Output "Built single GUI + CLI EXE: $taskOutput/LumaRecode.exe" }
    else {
        $taskZip = Join-Path $taskRoot "dist/LumaRecode-$taskVersion-win-x64-framework-dependent.zip"
        Compress-Archive -Path "$taskOutput/*" -DestinationPath $taskZip -Force
        Write-Output "Built framework-dependent ZIP: $taskZip"
    }
} finally { Pop-Location }
