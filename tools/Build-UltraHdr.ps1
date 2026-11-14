<#
.SYNOPSIS
Builds the pinned Ultra HDR codec with XMP and ISO gain-map metadata.
.DESCRIPTION
Run in an x64 MSVC developer shell with CMake, Ninja and NASM on PATH.
Only uhdr.dll is distributed; upstream command-line examples are not built.
#>
param([string]$WorkDirectory = (Join-Path $PSScriptRoot '../build-cache/ultrahdr'))
$ErrorActionPreference = 'Stop'
$taskRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskWork = [IO.Path]::GetFullPath($WorkDirectory).Replace('\','/')
New-Item -ItemType Directory -Path $taskWork -Force | Out-Null
$taskSources = @(
    @{name='libultrahdr';version='2.0.2';url='https://codeload.github.com/google/libultrahdr/zip/refs/tags/v2.0.2';sha256='854ae3cb55ebbc60c42d317dd83bb0796bb1027637abb1e17bfaff0304a8c15f';directory='libultrahdr-2.0.2'},
    @{name='libjpeg-turbo';version='3.2.1';url='https://codeload.github.com/libjpeg-turbo/libjpeg-turbo/zip/refs/tags/3.2.1';sha256='f18088e5528c2d3a615fa6f50de222fc46c92fb208ed29cd3ecd77be79cab42a';directory='libjpeg-turbo-3.2.1'}
)
foreach ($taskSource in $taskSources) {
    $taskArchive = "$taskWork/$($taskSource.name)-$($taskSource.version).zip"
    if (-not (Test-Path -LiteralPath $taskArchive)) { Invoke-WebRequest $taskSource.url -OutFile $taskArchive }
    if ((Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskSource.sha256) { throw "Source hash mismatch: $($taskSource.name)" }
    if (-not (Test-Path -LiteralPath "$taskWork/$($taskSource.directory)/CMakeLists.txt")) { Expand-Archive -LiteralPath $taskArchive -DestinationPath $taskWork }
}
function Invoke-CMake([string[]]$Arguments) {
    & cmake @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Native codec build failed.' }
}
Invoke-CMake -Arguments @('-S',"$taskWork/libjpeg-turbo-3.2.1",'-B',"$taskWork/jpeg-build",'-G','Ninja','-DCMAKE_BUILD_TYPE=Release','-DCMAKE_C_COMPILER=cl','-DCMAKE_CXX_COMPILER=cl',"-DCMAKE_INSTALL_PREFIX=$taskWork/jpeg-install",'-DENABLE_SHARED=OFF','-DENABLE_STATIC=ON','-DWITH_TURBOJPEG=OFF','-DWITH_SIMD=ON')
Invoke-CMake -Arguments @('--build',"$taskWork/jpeg-build",'--parallel')
Invoke-CMake -Arguments @('--install',"$taskWork/jpeg-build")
Invoke-CMake -Arguments @('-S',"$taskWork/libultrahdr-2.0.2",'-B',"$taskWork/uhdr-build",'-G','Ninja','-DCMAKE_BUILD_TYPE=Release','-DCMAKE_C_COMPILER=cl','-DCMAKE_CXX_COMPILER=cl','-DBUILD_SHARED_LIBS=ON','-DUHDR_BUILD_DEPS=OFF','-DUHDR_BUILD_EXAMPLES=OFF','-DUHDR_BUILD_TESTS=OFF','-DUHDR_ENABLE_HEIF=OFF','-DUHDR_ENABLE_INSTALL=OFF','-DUHDR_WRITE_XMP=ON','-DUHDR_WRITE_ISO=ON',"-DJPEG_LIBRARY=$taskWork/jpeg-install/lib/jpeg-static.lib", "-DJPEG_INCLUDE_DIR=$taskWork/jpeg-install/include")
Invoke-CMake -Arguments @('--build',"$taskWork/uhdr-build",'--parallel')
$taskNative = Join-Path $taskRepo 'App/Native'
Copy-Item -LiteralPath "$taskWork/uhdr-build/uhdr.dll" -Destination (Join-Path $taskNative 'uhdr.dll') -Force
$taskManifestPath = Join-Path $taskNative 'manifest.json'
$taskManifest = Get-Content -LiteralPath $taskManifestPath -Raw | ConvertFrom-Json -AsHashtable
$taskManifest.binary.sha256 = (Get-FileHash -LiteralPath (Join-Path $taskNative 'uhdr.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
$taskManifest.binary.bytes = (Get-Item -LiteralPath (Join-Path $taskNative 'uhdr.dll')).Length
$taskManifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $taskManifestPath -Encoding utf8
Write-Output "Built App/Native/uhdr.dll. Review the updated binary hash before committing."
