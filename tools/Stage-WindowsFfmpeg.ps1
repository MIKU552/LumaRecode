<#
.SYNOPSIS
Prepares an original FFmpeg Full distribution for the Windows packaging script.
.DESCRIPTION
Downloads a pinned upstream archive, verifies it, and keeps the upstream license
and complete tool directory together. This is for local validation; public
redistribution also needs the corresponding sources for that specific build.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('X64','Arm64')][string]$Target,
    [string]$Directory = (Join-Path $PSScriptRoot '../out/ffmpeg-windows')
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$builds = @{
    X64 = @{
        Repository = 'GyanD/codexffmpeg'
        File = 'ffmpeg-9.0.2-full_build.7z'
        SHA256 = 'f0e46253c70dfe902bac915dfb4224f0cf1b7c6eeab9da2ccc9a5581f9a71b13'
    }
    Arm64 = @{
        Repository = 'tordona/ffmpeg-win-arm64'
        File = 'ffmpeg-9.0.2-full-shared-win-arm64.7z'
        SHA256 = '95ccbfed25bb2f951e8d6af43d95a5ae7119e69f3f20c87217f7afc15041a3c3'
    }
}
$build = $builds[$Target]
$cache = (New-Item -ItemType Directory -Path ([IO.Path]::GetFullPath($Directory)) -Force).FullName
$archive = Join-Path $cache $build.File
$targetFolder = Join-Path $cache $Target.ToLowerInvariant()
gh release download 9.0.2 --repo $build.Repository --pattern $build.File --dir $cache --skip-existing
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $build.SHA256) { throw 'FFmpeg archive SHA-256 differs.' }
7z x $archive "-o$targetFolder" -y | Out-Null
$bins = @(Get-ChildItem -LiteralPath $targetFolder -File -Recurse -Filter 'ffmpeg.exe')
if ($bins.Count -ne 1 -or $bins[0].Directory.Name -ne 'bin') { throw 'Unexpected FFmpeg archive layout.' }
$payload = $bins[0].Directory.Parent.FullName
$notice = @"
FFmpeg 9.0.2 Full for Windows $Target

Original archive
https://github.com/$($build.Repository)/releases/download/9.0.2/$($build.File)
SHA-256 $($build.SHA256)

FFmpeg source revision
https://github.com/FFmpeg/FFmpeg/commit/946fcce07b6dcd0331c8cc609192aeff5e1924f8

Build upstream
https://github.com/$($build.Repository)

The package preserves the upstream license files. These binaries run as
separate executables. For public distribution, provide the corresponding
sources for FFmpeg and the libraries included by this exact build.
"@
[IO.File]::WriteAllText((Join-Path $payload 'BUILDINFO.txt'), $notice)
Write-Output $payload
