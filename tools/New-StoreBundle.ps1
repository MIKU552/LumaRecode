<#
.SYNOPSIS
Combines the x64 and ARM64 packages made with Package-Windows.ps1 -PartnerCenter.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$X64,
    [Parameter(Mandatory)][string]$Arm64,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$MakeAppx
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$identity = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../Packaging/StoreIdentity.json') -Raw | ConvertFrom-Json

function Read-PackageXml([string]$Path, [string]$EntryName) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $archive.GetEntry($EntryName)
        if (!$entry) { throw "$EntryName is missing from $Path" }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { return [xml]($reader.ReadToEnd()) } finally { $reader.Dispose() }
    } finally { $archive.Dispose() }
}

$sources = [ordered]@{ x64 = (Resolve-Path -LiteralPath $X64).Path; arm64 = (Resolve-Path -LiteralPath $Arm64).Path }
$version = $null
foreach ($cpu in $sources.Keys) {
    $manifest = (Read-PackageXml $sources[$cpu] 'AppxManifest.xml').Package
    if ($manifest.Identity.Name -cne $identity.name -or $manifest.Identity.Publisher -cne $identity.publisher -or
        $manifest.Properties.PublisherDisplayName -cne $identity.publisherDisplayName -or
        $manifest.Identity.ProcessorArchitecture -cne $cpu) {
        throw "Unexpected Store identity or architecture for $cpu."
    }
    $currentVersion = [version]$manifest.Identity.Version
    if ($currentVersion.Revision -ne 0 -or ($version -and $version -ne $currentVersion)) {
        throw 'Both packages must use the same Store version with a zero revision.'
    }
    $version = $currentVersion
}

if (!$MakeAppx) {
    if ($command = Get-Command 'makeappx.exe' -ErrorAction SilentlyContinue) {
        $MakeAppx = $command.Source
    } else {
        $sdkDirectory = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
        $cpu = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
        $MakeAppx = Get-ChildItem -LiteralPath $sdkDirectory -Directory -ErrorAction SilentlyContinue |
            Where-Object Name -Match '^10\.' | Sort-Object { [version]$_.Name } -Descending |
            ForEach-Object { Join-Path $_.FullName "$cpu/makeappx.exe" } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
    if (!$MakeAppx) { throw 'MakeAppx is required. Set -MakeAppx to the Windows SDK tool.' }
}

$out = (New-Item -ItemType Directory -Path ([IO.Path]::GetFullPath($OutputDirectory)) -Force).FullName
$assembly = [IO.Directory]::CreateTempSubdirectory('lumarecode-msix-')
try {
    foreach ($cpu in $sources.Keys) {
        Copy-Item -LiteralPath $sources[$cpu] -Destination (Join-Path $assembly.FullName "LumaRecode-$cpu.msix")
    }
    $bundle = Join-Path $out "LumaRecode-$version-store.msixbundle"
    & $MakeAppx bundle /d $assembly.FullName /p $bundle /bv $version.ToString() /o

    $manifest = (Read-PackageXml $bundle 'AppxMetadata/AppxBundleManifest.xml').Bundle
    $architectures = @($manifest.Packages.Package | Where-Object Type -eq 'application' | ForEach-Object Architecture)
    if ($manifest.Identity.Name -cne $identity.name -or $manifest.Identity.Publisher -cne $identity.publisher -or
        $manifest.Identity.Version -ne $version.ToString() -or
        (Compare-Object @('arm64','x64') @($architectures | Sort-Object))) {
        throw 'The generated bundle manifest does not match the Store identity, version or architectures.'
    }
    $upload = Join-Path $out "LumaRecode-$version-store.msixupload"
    $uploadArchive = [IO.Compression.ZipFile]::Open($upload, [IO.Compression.ZipArchiveMode]::Update)
    try {
        foreach ($entry in @($uploadArchive.Entries)) { $entry.Delete() }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $uploadArchive, $bundle, [IO.Path]::GetFileName($bundle),
            [IO.Compression.CompressionLevel]::NoCompression) | Out-Null
    } finally { $uploadArchive.Dispose() }
    Get-Item -LiteralPath $bundle, $upload
} finally {
    foreach ($file in $assembly.GetFiles()) { $file.Delete() }
    $assembly.Delete()
}
