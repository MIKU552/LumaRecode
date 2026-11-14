<#
.SYNOPSIS
Builds the pinned Ultra HDR codec with XMP and ISO gain-map metadata.
.DESCRIPTION
Run in an MSVC developer shell for the selected target with CMake and Ninja.
The x64 SIMD build additionally requires NASM.
Only uhdr.dll is distributed; upstream command-line examples are not built.
#>
param(
    [ValidateSet('x64','arm64')][string]$Architecture = 'x64',
    [string]$WorkDirectory = (Join-Path $PSScriptRoot '../build-cache/ultrahdr')
)
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
$taskJpegBuild = "$taskWork/jpeg-build-$Architecture"
$taskJpegInstall = "$taskWork/jpeg-install-$Architecture"
$taskUhdrBuild = "$taskWork/uhdr-build-$Architecture"
$taskCross = @('-DCMAKE_TRY_COMPILE_CONFIGURATION=Release','-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreadedDLL')
if ($Architecture -eq 'arm64') { $taskCross += @('-DCMAKE_SYSTEM_NAME=Windows','-DCMAKE_SYSTEM_PROCESSOR=ARM64') }
Invoke-CMake -Arguments (@('-S',"$taskWork/libjpeg-turbo-3.2.1",'-B',$taskJpegBuild,'-G','Ninja','-DCMAKE_BUILD_TYPE=Release','-DCMAKE_C_COMPILER=cl','-DCMAKE_CXX_COMPILER=cl',"-DCMAKE_INSTALL_PREFIX=$taskJpegInstall",'-DENABLE_SHARED=OFF','-DENABLE_STATIC=ON','-DWITH_TURBOJPEG=OFF','-DWITH_SIMD=ON') + $taskCross)
Invoke-CMake -Arguments @('--build',$taskJpegBuild,'--parallel','8')
Invoke-CMake -Arguments @('--install',$taskJpegBuild)
Invoke-CMake -Arguments (@('-S',"$taskWork/libultrahdr-2.0.2",'-B',$taskUhdrBuild,'-G','Ninja','-DCMAKE_BUILD_TYPE=Release','-DCMAKE_C_COMPILER=cl','-DCMAKE_CXX_COMPILER=cl','-DBUILD_SHARED_LIBS=ON','-DUHDR_BUILD_DEPS=OFF','-DUHDR_BUILD_EXAMPLES=OFF','-DUHDR_BUILD_TESTS=OFF','-DUHDR_ENABLE_HEIF=OFF','-DUHDR_ENABLE_INSTALL=OFF','-DUHDR_WRITE_XMP=ON','-DUHDR_WRITE_ISO=ON',"-DJPEG_LIBRARY=$taskJpegInstall/lib/jpeg-static.lib", "-DJPEG_INCLUDE_DIR=$taskJpegInstall/include") + $taskCross)
Invoke-CMake -Arguments @('--build',$taskUhdrBuild,'--parallel','8')
$taskNative = Join-Path $taskRepo "App/Native/$Architecture"
New-Item -ItemType Directory -Path $taskNative -Force | Out-Null
Copy-Item -LiteralPath "$taskUhdrBuild/uhdr.dll" -Destination (Join-Path $taskNative 'uhdr.dll') -Force
$taskManifestPath = Join-Path $taskRepo 'App/Native/manifest.json'
$taskManifest = Get-Content -LiteralPath $taskManifestPath -Raw | ConvertFrom-Json -AsHashtable
$taskManifest.binaries[$Architecture] = @{
    file="$Architecture/uhdr.dll";architecture="windows-$Architecture"
    sha256=(Get-FileHash -LiteralPath (Join-Path $taskNative 'uhdr.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
    bytes=(Get-Item -LiteralPath (Join-Path $taskNative 'uhdr.dll')).Length
}
$taskManifest.builds[$Architecture] = @{
    compiler="MSVC $Architecture";configuration='Release /MD';jpeg='static, SIMD, no TurboJPEG API'
    metadata=@('XMP','ISO 21496-1');examples=$false;heif=$false;script='tools/Build-UltraHdr.ps1'
}
$taskManifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $taskManifestPath -Encoding utf8
Write-Output "Built App/Native/$Architecture/uhdr.dll."
