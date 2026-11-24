<#
.SYNOPSIS
Creates Windows EXE, portable ZIP, per-user MSI, or unsigned MSIX packages.
.DESCRIPTION
Uses one published application per profile, then stages each FFmpeg variant.
Native AOT is the primary profile. JitExternal loads the installed .NET runtime;
both profiles carry app-local WinUI. An MSIX can use a development identity or
the Package/Identity values assigned by Partner Center.
#>
[CmdletBinding()]
param(
    [ValidateSet('X64','Arm64','Apx')][string]$Target = 'X64',
    [ValidateSet('Aot','JitBundled','JitExternal')][string]$Profile = 'Aot',
    [ValidateSet('Exe','Zip','Msi','Msix')][string[]]$Format = @('Exe','Zip','Msi'),
    [ValidateSet('External','Bundled','Both')][string]$Ffmpeg = 'External',
    [string]$FfmpegDirectory,
    [string]$OutputDirectory,
    [string]$StoreName = 'MIKU552.LumaRecode.Dev',
    [string]$StorePublisher = 'CN=MIKU552',
    [string]$StorePublisherDisplayName = 'MIKU552',
    [version]$StoreVersion,
    [switch]$UseEnvironmentalNativeTools,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$project = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$destination = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $project 'dist/windows' }
$applicationProject = [xml](Get-Content -LiteralPath (Join-Path $project 'App/VideoConverter.WinUI.csproj') -Raw)
$version = @($applicationProject.Project.PropertyGroup.Version | Where-Object { $_ })[0]
$versionParts = $version -split '-', 2
$cpu = if ($Target -eq 'Arm64') { 'arm64' } else { 'x64' }
$platform = if ($cpu -eq 'arm64') { 'ARM64' } else { 'x64' }
$compiler = if ($Profile -eq 'Aot') { 'Aot' } else { 'Jit' }
$deployment = if ($Profile -eq 'JitExternal') { 'FrameworkDependent' } else { 'Portable' }
$payloadPath = if ($compiler -eq 'Aot') { "dist/Aot-Portable-$Target" } else { "dist/$deployment-$Target" }
$variants = switch ($Ffmpeg) { 'Both' { 'external', 'bundled' } default { $Ffmpeg.ToLowerInvariant() } }
if ($Target -eq 'Apx' -and $Profile -ne 'Aot') { throw 'APX is an explicit AOT build target.' }
if ('Msix' -in $Format -and ($Profile -ne 'Aot' -or $Target -eq 'Apx')) {
    throw 'Build the Store package from the baseline x64 or ARM64 AOT application.'
}
if (-not $StoreVersion) { $StoreVersion = [version]($versionParts[0] + '.0') }
if ('Msix' -in $Format -and ($StoreVersion.Revision -ne 0 -or $StoreVersion.Major -eq 0)) {
    throw 'The MSIX version requires four fields, a nonzero major, and a zero revision for the Store.'
}

function Read-PeMachine([string]$Path) {
    # Verify the actual PE header for both app and bundled tools.
    $file = [IO.File]::OpenRead($Path)
    try {
        $reader = [IO.BinaryReader]::new($file)
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw "Not a Windows executable: $Path" }
        $file.Position = 0x3C
        $file.Position = $reader.ReadUInt32()
        if ($reader.ReadUInt32() -ne 0x4550) { throw "Invalid PE header: $Path" }
        return $reader.ReadUInt16()
    } finally { $file.Dispose() }
}

$machine = if ($cpu -eq 'arm64') { 0xaa64 } else { 0x8664 }
if ('bundled' -in $variants) {
    if (!$FfmpegDirectory) { throw 'Set -FfmpegDirectory to the prepared FFmpeg directory.' }
    $FfmpegDirectory = (Resolve-Path -LiteralPath $FfmpegDirectory).Path
    foreach ($name in @('ffmpeg.exe','ffprobe.exe')) {
        if ((Read-PeMachine (Join-Path $FfmpegDirectory "bin/$name")) -ne $machine) {
            throw "$name does not match $cpu."
        }
    }
    if (!(Test-Path -LiteralPath (Join-Path $FfmpegDirectory 'BUILDINFO.txt') -PathType Leaf)) {
        throw 'The bundle requires BUILDINFO.txt with upstream version and source information.'
    }
}

function New-StoreAssets([string]$Stage) {
    Add-Type -AssemblyName System.Drawing
    $folder = New-Item -ItemType Directory -Path (Join-Path $Stage 'StoreAssets')
    $image = [Drawing.Image]::FromFile((Join-Path $project 'Brand/LumaRecode.png'))
    try {
        foreach ($asset in @{StoreLogo=50;Square44x44Logo=44;Square150x150Logo=150}.GetEnumerator()) {
            $bitmap = [Drawing.Bitmap]::new($asset.Value, $asset.Value)
            try {
                $graphics = [Drawing.Graphics]::FromImage($bitmap)
                try {
                    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                    $graphics.DrawImage($image, 0, 0, $asset.Value, $asset.Value)
                } finally { $graphics.Dispose() }
                $bitmap.Save((Join-Path $folder.FullName ($asset.Key + '.png')), [Drawing.Imaging.ImageFormat]::Png)
            } finally { $bitmap.Dispose() }
        }
    } finally { $image.Dispose() }
}

function Find-MakeAppx {
    if ($command = Get-Command 'makeappx.exe' -ErrorAction SilentlyContinue) { return $command.Source }
    $sdk = $applicationProject.Project.ItemGroup.PackageReference | Where-Object Include -eq 'Microsoft.Windows.SDK.BuildTools'
    $packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
    $hostCpu = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    $tool = Get-ChildItem -LiteralPath (Join-Path $packages "microsoft.windows.sdk.buildtools/$($sdk.Version)/bin") -Recurse -Filter 'makeappx.exe' |
        Where-Object {$_.Directory.Name -eq $hostCpu} | Select-Object -First 1
    if (!$tool) { throw 'MakeAppx was not found in the Windows SDK or NuGet build tools.' }
    return $tool.FullName
}

Push-Location -LiteralPath $project
$staging = $null
try {
    if (!$SkipBuild) {
        & ./build.ps1 -Target $Target -Compilation $compiler -Deployment $deployment -SkipArchive -UseEnvironmentalNativeTools:$UseEnvironmentalNativeTools
    }
    $payload = (Resolve-Path -LiteralPath $payloadPath).Path
    if ((Read-PeMachine (Join-Path $payload 'LumaRecode.exe')) -ne $machine) { throw 'Application architecture differs from the selected target.' }
    if ('Msi' -in $Format) { dotnet tool restore }
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $stagingRoot = (New-Item -ItemType Directory -Path (Join-Path $project 'out/windows-package-stage') -Force).FullName
    if ((Get-Item -LiteralPath $stagingRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Staging root uses a reparse point.' }
    $staging = (New-Item -ItemType Directory -Path (Join-Path $stagingRoot ([guid]::NewGuid().ToString('N')))).FullName
    foreach ($variant in $variants) {
        $stage = (New-Item -ItemType Directory -Path (Join-Path $staging $variant)).FullName
        Get-ChildItem -LiteralPath $payload -Force | Where-Object Extension -ne '.pdb' |
            Copy-Item -Destination $stage -Recurse
        if ($variant -eq 'bundled') {
            $toolParent = (New-Item -ItemType Directory -Path (Join-Path $stage 'Tools')).FullName
            Copy-Item -LiteralPath $FfmpegDirectory -Destination (Join-Path $toolParent 'FFmpeg') -Recurse
        }
        $stem = "LumaRecode-$version-$($Target.ToLowerInvariant())-$($Profile.ToLowerInvariant())-ffmpeg-$variant"
        if ('Zip' -in $Format) {
            Compress-Archive -Path (Join-Path $stage '*') -DestinationPath (Join-Path $destination "$stem-portable.zip") -Force
        }
        if ('Msi' -in $Format) {
            $oldRollForward = $env:DOTNET_ROLL_FORWARD
            try {
                # WiX is a developer tool targeting .NET 6; reuse the installed
                # SDK runtime to run it without imposing that dependency on users.
                $env:DOTNET_ROLL_FORWARD = 'Major'
                dotnet wix build Packaging/Installer.wxs -arch $cpu -pdbtype none -d "Version=$($versionParts[0])" -d "Payload=$stage" -o (Join-Path $destination "$stem.msi")
            } finally { $env:DOTNET_ROLL_FORWARD = $oldRollForward }
        }
        if ('Msix' -in $Format) {
            New-StoreAssets $stage
            $manifest = [xml](Get-Content -LiteralPath 'Packaging/Package.appxmanifest' -Raw)
            $manifest.Package.Identity.SetAttribute('Name', $StoreName)
            $manifest.Package.Identity.SetAttribute('Publisher', $StorePublisher)
            $manifest.Package.Identity.SetAttribute('Version', $StoreVersion.ToString())
            $manifest.Package.Identity.SetAttribute('ProcessorArchitecture', $cpu)
            $manifest.Package.Properties.PublisherDisplayName = $StorePublisherDisplayName
            $manifest.Save((Join-Path $stage 'AppxManifest.xml'))
            & (Find-MakeAppx) pack /d $stage /p (Join-Path $destination "$stem.msix") /o
            # The Store manifest and logos are only used by MSIX.
            Remove-Item -LiteralPath (Join-Path $stage 'AppxManifest.xml')
            foreach ($logo in Get-ChildItem -LiteralPath (Join-Path $stage 'StoreAssets') -File) {
                Remove-Item -LiteralPath $logo.FullName
            }
            Remove-Item -LiteralPath (Join-Path $stage 'StoreAssets')
        }
        if ('Exe' -in $Format) {
            $single = Join-Path $staging "$variant-exe"
            $runtime = ($Profile -ne 'JitExternal').ToString().ToLowerInvariant()
            $properties = @('-r',"win-$cpu","-p:Platform=$platform","-p:LumaBuildTarget=$Target",
                "-p:LumaResolvedTarget=$Target","-p:LumaCompilation=$compiler","-p:SelfContained=$runtime")
            dotnet restore Bundle/LumaRecode.Bundle.csproj --locked-mode @properties
            # Capture the launcher runtime notices after staging the standalone
            # app formats, whose dependency graph does not include the launcher.
            & ./tools/Collect-Licenses.ps1 -ProjectRoot $project -Architecture $cpu `
                -AssetsFile (Join-Path $project "App/obj/$compiler/$Target/project.assets.json") `
                -AdditionalAssetsFile (Join-Path $project "Bundle/obj/$compiler/$Target/project.assets.json") `
                -OutputDirectory (Join-Path $stage 'licenses')
            dotnet publish Bundle/LumaRecode.Bundle.csproj --no-restore -c Release -o $single "-p:LumaPayloadDirectory=$stage" @properties
            Copy-Item -LiteralPath (Join-Path $single 'LumaRecode.exe') -Destination (Join-Path $destination "$stem.exe") -Force
        }
    }
    Get-ChildItem -LiteralPath $destination -File | Where-Object Name -like "LumaRecode-$version-$($Target.ToLowerInvariant())-$($Profile.ToLowerInvariant())-*"
} finally {
    if ($staging -and (Test-Path -LiteralPath $staging)) {
        $absolute = (Resolve-Path -LiteralPath $staging).Path
        if ([IO.Path]::GetDirectoryName($absolute) -eq $stagingRoot) { Remove-Item -LiteralPath $absolute -Recurse -Force }
    }
    Pop-Location
}
