<#
.SYNOPSIS
Downloads pinned FFmpeg/ffprobe for native-architecture software integration checks.
#>
param([string]$Directory)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (!$Directory) { $Directory = Join-Path $taskRoot 'out/media-test-tools' }
$taskDestination = [IO.Path]::GetFullPath($Directory)
$taskManifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'media-test-tools.json') -Raw | ConvertFrom-Json
$taskCpu = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$taskOs = if ($IsWindows) { 'windows' } elseif ($IsMacOS) { 'macos' } else { throw 'Supported test hosts: Windows and macOS.' }
$taskAssets = $taskManifest.platforms."$taskOs-$taskCpu"
if (!$taskAssets) { throw "No pinned media test tools for $taskOs-$taskCpu." }
$taskFolders = @()
foreach ($taskAsset in $taskAssets) {
    $taskName = [IO.Path]::GetFileName(([Uri]$taskAsset.url).AbsolutePath)
    $taskInstall = Join-Path $taskDestination "$($taskManifest.version)-$taskOs-$taskCpu-$([IO.Path]::GetFileNameWithoutExtension($taskName))"
    $taskMarker = Join-Path $taskInstall '.verified-sha256'
    if (!(Test-Path -LiteralPath $taskMarker) -or (Get-Content -LiteralPath $taskMarker -Raw).Trim() -ne $taskAsset.sha256) {
        New-Item -ItemType Directory -Path $taskInstall -Force | Out-Null
        $taskArchive = Join-Path $taskDestination $taskName
        for ($taskAttempt = 0; $taskAttempt -lt 3; $taskAttempt++) {
            try {
                Invoke-WebRequest -Uri $taskAsset.url -OutFile $taskArchive -ConnectionTimeoutSeconds 20 -OperationTimeoutSeconds 180
                break
            } catch {
                # gh uses the release API, which also works on networks where
                # the browser download endpoint cannot complete a TLS handshake.
                $taskUri = [Uri]$taskAsset.url
                $taskSegments = $taskUri.AbsolutePath.Trim('/').Split('/')
                if ($taskUri.Host -eq 'github.com' -and $taskSegments.Count -eq 6 -and
                    $taskSegments[2] -eq 'releases' -and $taskSegments[3] -eq 'download' -and
                    (Get-Command gh -ErrorAction SilentlyContinue)) {
                    & gh release download $taskSegments[4] --repo "$($taskSegments[0])/$($taskSegments[1])" --pattern $taskName --dir $taskDestination --clobber
                    if ($LASTEXITCODE -eq 0) { break }
                }
                if ($taskAttempt -eq 2) { throw }
                Start-Sleep -Seconds 2
            }
        }
        if ((Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskAsset.sha256) {
            throw "Checksum mismatch: $taskName"
        }
        if ($taskName.EndsWith('.zip')) {
            Expand-Archive -LiteralPath $taskArchive -DestinationPath $taskInstall -Force
        } else {
            $taskExtractor = Get-Command 7z -ErrorAction SilentlyContinue
            if ($taskExtractor) {
                & $taskExtractor.Source x $taskArchive "-o$taskInstall" -y -bsp0 -bso0
            } else {
                & tar -xf $taskArchive -C $taskInstall
            }
            if ($LASTEXITCODE -ne 0) { throw "Cannot extract $taskName; install 7-Zip for this archive." }
        }
        [IO.File]::WriteAllText($taskMarker, $taskAsset.sha256)
        Remove-Item -LiteralPath $taskArchive
    }
    $taskFolders += $taskInstall
}

function Read-MediaToolArchitecture([string]$Path) {
    $taskStream = [IO.File]::OpenRead($Path)
    $taskReader = [IO.BinaryReader]::new($taskStream)
    try {
        if ($taskReader.ReadUInt16() -eq 0x5a4d) {
            $taskStream.Position = 0x3c
            $taskOffset = $taskReader.ReadUInt32()
            $taskStream.Position = $taskOffset + 4
            switch ($taskReader.ReadUInt16()) { 0x8664 { return 'x64' }; 0xaa64 { return 'arm64' } }
        } else {
            $taskStream.Position = 0
            if ($taskReader.ReadUInt32() -eq [uint32]4277009103 -and $taskReader.ReadUInt32() -eq 0x0100000c) { return 'arm64' }
        }
        throw "Unexpected media-tool executable format: $Path"
    } finally {
        $taskReader.Dispose()
    }
}

foreach ($taskTool in @('ffmpeg','ffprobe')) {
    $taskFileName = if ($IsWindows) { "$taskTool.exe" } else { $taskTool }
    $taskMatches = @($taskFolders | ForEach-Object { Get-ChildItem -LiteralPath $_ -Recurse -File -Filter $taskFileName })
    if ($taskMatches.Count -ne 1) { throw "Expected one $taskFileName in the pinned test tools." }
    $taskExecutable = $taskMatches[0].FullName
    if ((Read-MediaToolArchitecture $taskExecutable) -ne $taskCpu) { throw "$taskTool architecture differs from $taskCpu." }
    if ($IsMacOS) {
        & chmod +x $taskExecutable
        if ($LASTEXITCODE -ne 0) { throw "Cannot mark $taskTool executable." }
        & codesign --verify --strict $taskExecutable
        if ($LASTEXITCODE -ne 0) { throw "Invalid signature for $taskTool." }
    }
    $taskVersion = @(& $taskExecutable -version)
    if ($LASTEXITCODE -ne 0 -or $taskVersion[0] -notmatch ([regex]::Escape($taskManifest.version) + '(?=$|[^0-9.])')) {
        throw "Unexpected version for $taskTool."
    }
    $taskKey = "LUMA_TEST_$($taskTool.ToUpperInvariant())"
    [Environment]::SetEnvironmentVariable($taskKey, $taskExecutable, 'Process')
    if ($env:GITHUB_ENV) { [IO.File]::AppendAllText($env:GITHUB_ENV, "$taskKey=$taskExecutable`n") }
    Write-Output "$taskTool $taskCpu`: $($taskVersion[0])"
}
