<#
.SYNOPSIS
Downloads the repository's pinned CMake/Ninja into a development-only cache.
#>
param([string]$Directory)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (!$Directory) { $Directory = Join-Path $taskRoot 'out/build-tools' }
$taskDestination = [IO.Path]::GetFullPath($Directory)
$taskManifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'native-build-tools.json') -Raw | ConvertFrom-Json
$taskCpu = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$taskOs = if ($IsWindows) { 'windows' } elseif ($IsMacOS) { 'macos' } else { throw 'This tool installer supports Windows and macOS.' }
$taskHost = "$taskOs-$taskCpu"
$taskPaths = @()
foreach ($taskName in @('cmake', 'ninja')) {
    $taskTool = $taskManifest.$taskName
    $taskAsset = $taskTool.$taskHost
    if (!$taskAsset) { throw "No pinned $taskName download for $taskHost." }
    $taskInstall = Join-Path $taskDestination "$taskName-$($taskTool.version)"
    $taskMarker = Join-Path $taskInstall '.verified-sha256'
    if (!(Test-Path -LiteralPath $taskMarker) -or (Get-Content -LiteralPath $taskMarker -Raw).Trim() -ne $taskAsset.sha256) {
        New-Item -ItemType Directory -Path $taskInstall -Force | Out-Null
        $taskArchive = Join-Path $taskDestination $taskAsset.asset
        $taskRepository = if ($taskName -eq 'cmake') { 'Kitware/CMake' } else { 'ninja-build/ninja' }
        $taskUrl = "https://github.com/$taskRepository/releases/download/v$($taskTool.version)/$($taskAsset.asset)"
        Invoke-WebRequest -Uri $taskUrl -OutFile $taskArchive
        if ((Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskAsset.sha256) {
            throw "Checksum mismatch for $taskName $($taskTool.version)."
        }
        if ($taskAsset.asset.EndsWith('.zip')) {
            Expand-Archive -LiteralPath $taskArchive -DestinationPath $taskInstall -Force
        } else {
            & tar -xzf $taskArchive -C $taskInstall
            if ($LASTEXITCODE -ne 0) { throw "Cannot extract $taskName." }
        }
        [IO.File]::WriteAllText($taskMarker, $taskAsset.sha256)
    }
    if ($taskName -eq 'ninja') {
        $taskBin = $taskInstall
        if ($IsMacOS) { & chmod +x (Join-Path $taskBin 'ninja') }
    } elseif ($IsWindows) {
        $taskFolder = [IO.Path]::GetFileNameWithoutExtension($taskAsset.asset)
        $taskBin = Join-Path $taskInstall "$taskFolder/bin"
    } else {
        $taskBin = Join-Path $taskInstall "cmake-$($taskTool.version)-macos-universal/CMake.app/Contents/bin"
    }
    $taskPaths += $taskBin
    Write-Output "$taskName $($taskTool.version): $taskBin"
    if ($env:GITHUB_PATH) { [IO.File]::AppendAllText($env:GITHUB_PATH, $taskBin + "`n") }
}
$env:PATH = ($taskPaths -join [IO.Path]::PathSeparator) + [IO.Path]::PathSeparator + $env:PATH
