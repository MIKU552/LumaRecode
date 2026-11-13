param([Parameter(Mandatory=$true)][string]$ProjectRoot, [string]$AdditionalAssetsFile)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath($ProjectRoot)
$taskOutput = Join-Path $taskRoot 'licenses'
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskAssets = Get-Content -LiteralPath (Join-Path $taskRoot 'App/obj/project.assets.json') -Raw | ConvertFrom-Json
$taskAllAssets = @($taskAssets)
if ($AdditionalAssetsFile) { $taskAllAssets += Get-Content -LiteralPath $AdditionalAssetsFile -Raw | ConvertFrom-Json }
$taskPackages = @($taskAllAssets | ForEach-Object { $_.packageFolders.PSObject.Properties.Name } | Sort-Object -Unique)
$taskRecords = [Collections.Generic.List[object]]::new()
$taskByHash = @{}
# Stable key ordering keeps tracked provenance identical across clean builds.
function ConvertTo-CanonicalObject($Value) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [Collections.IDictionary]) {
        $taskSorted = [ordered]@{}
        foreach ($taskKey in @($Value.Keys | Sort-Object)) { $taskSorted[$taskKey] = ConvertTo-CanonicalObject $Value[$taskKey] }
        return $taskSorted
    }
    if ($Value -is [Collections.IEnumerable] -and $Value -isnot [string]) {
        $taskArray = @(foreach ($taskEntry in $Value) { ConvertTo-CanonicalObject $taskEntry })
        return ,$taskArray
    }
    return $Value
}
$taskLibraries = @($taskAllAssets | ForEach-Object { $_.libraries.PSObject.Properties } | Sort-Object Name -Unique)
if (-not ($taskLibraries.Name -match '^Microsoft.WindowsAppSDK.WinUI/')) { throw 'WinUI legal provenance is missing from the resolved dependency graphs.' }
foreach ($taskLibrary in $taskLibraries) {
    if ($taskLibrary.Value.type -ne 'package') { continue }
    $taskIdentity = $taskLibrary.Name.Split('/')
    $taskId = $taskIdentity[0]; $taskVersion = $taskIdentity[1]
    $taskPackage = $null
    foreach ($taskFolder in $taskPackages) {
        $taskCandidate = Join-Path $taskFolder $taskLibrary.Value.path
        if (Test-Path -LiteralPath $taskCandidate) { $taskPackage = $taskCandidate; break }
    }
    if (-not $taskPackage) { throw "Package not found: $($taskLibrary.Name)" }
    $taskSpec = Get-ChildItem -LiteralPath $taskPackage -Filter '*.nuspec' -File | Select-Object -First 1
    [xml]$taskXml = Get-Content -LiteralPath $taskSpec.FullName -Raw
    $taskLicenseNode = $taskXml.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='license']")
    $taskFiles = [Collections.Generic.List[object]]::new()
    foreach ($taskFile in (Get-ChildItem -LiteralPath $taskPackage -File -Recurse | Where-Object { $_.Name -match '(?i)license|notice|copying|authors' -and $_.Extension -in '.txt','.md','' } | Sort-Object FullName)) {
        $taskHash = (Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if (-not $taskByHash.ContainsKey($taskHash)) {
            $taskRelative = [IO.Path]::GetRelativePath($taskPackage, $taskFile.FullName)
            $taskName = "$taskId-$taskVersion-$($taskRelative -replace '[\\/]', '_')"
            Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $taskOutput $taskName) -Force
            $taskByHash[$taskHash] = $taskName
        }
        if ($taskFile.Name -match '(?i)license' -and (Get-Content -LiteralPath $taskFile.FullName -Raw) -match 'MICROSOFT WINDOWS APP SDK ENGINEERING PREVIEW') {
            throw "Preview license found in a production dependency: $($taskLibrary.Name)"
        }
        $taskFiles.Add(@{file=$taskByHash[$taskHash];source_file_in_package=[IO.Path]::GetRelativePath($taskPackage, $taskFile.FullName);sha256=$taskHash})
    }
    $taskPackageHash = Get-Content -LiteralPath (Join-Path $taskPackage "$($taskId.ToLowerInvariant()).$taskVersion.nupkg.sha512") -Raw
    $taskRecords.Add(@{package=$taskId;version=$taskVersion;source="https://www.nuget.org/packages/$taskId/$taskVersion";package_sha512_base64=$taskPackageHash.Trim();license=if ($taskLicenseNode) {$taskLicenseNode.InnerText} else {'See package terms'};files=$taskFiles.ToArray()})
}
# Runtime and apphost packs are implicit SDK dependencies, outside assets.libraries.
$taskRuntimeRecords = [Collections.Generic.List[object]]::new()
$taskImplicit = @($taskAllAssets | ForEach-Object { $_.project.frameworks.PSObject.Properties.Value.downloadDependencies } | Sort-Object name -Unique)
foreach ($taskDependency in $taskImplicit) {
    $taskRuntime = $taskDependency.name.ToLowerInvariant()
    $taskRuntimeVersion = ($taskDependency.version.Trim('[',']').Split(',')[0]).Trim()
    $taskDirectory = $null
    foreach ($taskFolder in $taskPackages) {
        $taskCandidate = Join-Path $taskFolder "$taskRuntime/$taskRuntimeVersion"
        if (Test-Path -LiteralPath $taskCandidate) { $taskDirectory = $taskCandidate; break }
    }
    if (-not $taskDirectory) { throw "Implicit dependency legal evidence missing: $taskRuntime/$taskRuntimeVersion" }
    $taskFiles = @()
    foreach ($taskFile in (Get-ChildItem -LiteralPath $taskDirectory -File | Where-Object Name -Match '(?i)license|notice' | Sort-Object Name)) {
        $taskName = "$taskRuntime-$taskRuntimeVersion-$($taskFile.Name)"
        Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $taskOutput $taskName) -Force
        $taskFiles += @{file=$taskName;sha256=(Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    }
    if (-not $taskFiles -and $taskRuntime -eq 'microsoft.windows.sdk.net.ref') {
        foreach ($taskName in @('Windows-SDK-Net-Ref-LICENSE.rtf','Windows-SDK-Net-Ref-LICENSE.txt','Windows-SDK-REDIST.html')) {
            $taskPath = Join-Path $taskOutput $taskName
            if (-not (Test-Path -LiteralPath $taskPath)) { throw "Missing retained Windows SDK terms: $taskName" }
            $taskFiles += @{file=$taskName;sha256=(Get-FileHash -LiteralPath $taskPath -Algorithm SHA256).Hash.ToLowerInvariant();source='https://aka.ms/WinSDKLicenseURL'}
        }
    }
    if (-not $taskFiles) { throw "No runtime legal texts: $taskRuntime" }
    $taskRuntimeRecords.Add(@{package=$taskRuntime;version=$taskRuntimeVersion;source="https://www.nuget.org/packages/$taskRuntime/$taskRuntimeVersion";files=$taskFiles})
}
$taskDotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
foreach ($taskFile in @('LICENSE.txt','ThirdPartyNotices.txt')) {
    Copy-Item -LiteralPath (Join-Path $taskDotnetRoot $taskFile) -Destination (Join-Path $taskOutput "DotNet-SDK-11.0.100-$taskFile") -Force
}
Copy-Item -LiteralPath (Join-Path $taskRoot 'LICENSE'), (Join-Path $taskRoot 'NOTICE') -Destination $taskOutput -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'Assets/OpenOSV') -Destination $taskOutput -Recurse -Force
$taskVersion = ([xml](Get-Content -LiteralPath (Join-Path $taskRoot 'App/VideoConverter.WinUI.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$taskFontRecords = Get-Content -LiteralPath (Join-Path $taskOutput 'Fonts/manifest.json') -Raw | ConvertFrom-Json -AsHashtable
foreach ($taskFontRecord in $taskFontRecords) {
    $taskFontPath = if ($taskFontRecord.file.EndsWith('.ttf')) {
        Join-Path $taskRoot "App/Assets/Fonts/$($taskFontRecord.file)"
    } else { Join-Path $taskOutput "Fonts/$($taskFontRecord.file)" }
    if ((Get-FileHash -LiteralPath $taskFontPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskFontRecord.sha256) {
        throw "Bundled font or license checksum mismatch: $($taskFontRecord.file)"
    }
}
$taskManifest = [ordered]@{schema_version=2;project_license='MIT';app_version=$taskVersion;reviewed_on='2026-11-12';packages=$taskRecords.ToArray();implicit_runtime_packs=$taskRuntimeRecords.ToArray();dotnet_sdk='11.0.100';backend='native-dotnet';distribution_basis=@('Windows App SDK production package terms, section 3, for files placed with the application by NuGet','.NET Library License for designated Windows binaries and single-file runtime; MIT and notices for remaining files; Windows SDK terms where applicable','Windows SDK license and its Microsoft.Windows.SDK.NET.Ref REDIST list for unmodified WinRT projection binaries','Unmodified OpenOSV LUT under Apache-2.0 with its original LICENSE and NOTICE');third_party_rights_separate=$true}
$taskManifest['fonts'] = @{license='OFL-1.1';sources='Fonts/manifest.json';families=@('Source Sans 3','Source Han Sans CN','Source Han Sans JP','Source Code Pro')}
$taskManifest.distribution_basis += 'Unmodified Source Sans 3, Source Han Sans and Source Code Pro fonts under SIL OFL 1.1, with original copyrights and license texts'
ConvertTo-CanonicalObject $taskManifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $taskOutput 'manifest.json') -Encoding utf8
Write-Output "Collected legal text for $($taskRecords.Count) resolved NuGet packages."
