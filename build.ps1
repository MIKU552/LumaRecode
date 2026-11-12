param([ValidateSet('SelfContained','FrameworkDependent')][string]$Deployment = 'SelfContained')
$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $PSScriptRoot
try {
    $taskBundled = $Deployment -eq 'SelfContained'
    $taskRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    function Reset-GeneratedDirectory([string]$RelativePath) {
        $taskTarget = [IO.Path]::GetFullPath((Join-Path $taskRoot $RelativePath))
        if (-not $taskTarget.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid generated directory.' }
        if ($RelativePath -notin @('Payload/Worker','Payload/Runtime','dist/SelfContained','dist/FrameworkDependent')) { throw 'Unexpected generated directory.' }
        if (Test-Path -LiteralPath $taskTarget) { Remove-Item -LiteralPath $taskTarget -Recurse -Force }
        New-Item -ItemType Directory -Path $taskTarget -Force | Out-Null
    }
    $taskOutput = Join-Path $taskRoot "dist/$Deployment"
    if (-not (Get-Command gcc -ErrorAction SilentlyContinue)) { throw 'MinGW-w64 GCC is required to build the native console entry. See docs/dependencies.md.' }
    Reset-GeneratedDirectory "dist/$Deployment"
    Reset-GeneratedDirectory 'Payload/Worker'
    Get-ChildItem -LiteralPath 'Worker' -Filter '*.py' -File | Copy-Item -Destination 'Payload/Worker'
    Copy-Item -LiteralPath 'App/Strings.en.json' -Destination 'Payload/Worker/strings.en.json'
    New-Item -ItemType Directory -Path 'Payload/Worker/assets' -Force | Out-Null
    Get-ChildItem -LiteralPath 'Worker/assets' -Filter '*.cube' -File | Copy-Item -Destination 'Payload/Worker/assets'
    Copy-Item -LiteralPath 'Worker/assets/recommended-presets.json' -Destination 'Payload/Worker/assets'

    $taskPython = $null
    if ($taskBundled) {
        $taskCache = Join-Path $taskRoot 'build-cache'
        New-Item -ItemType Directory -Path $taskCache -Force | Out-Null
        $taskArchive = Join-Path $taskCache 'python-3.15.2-embed-amd64.zip'
        $taskExpected = 'd012f3aa29cf07436dae8aea1fec0fd195c2612a0b059b1f915ae0afb9ad1f62'
        if (-not (Test-Path -LiteralPath $taskArchive)) {
            Invoke-WebRequest -Uri 'https://www.python.org/ftp/python/3.15.2/python-3.15.2-embed-amd64.zip' -OutFile $taskArchive
        }
        if ((Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskExpected) { throw 'Official Python archive checksum mismatch.' }
        Reset-GeneratedDirectory 'Payload/Runtime'
        Expand-Archive -LiteralPath $taskArchive -DestinationPath 'Payload/Runtime'
        $taskPython = Join-Path $taskRoot 'Payload/Runtime'
    }

    if (Test-Path -LiteralPath 'build-cache/nuget') {
        dotnet restore App/VideoConverter.WinUI.csproj -r win-x64 --locked-mode --source https://api.nuget.org/v3/index.json --source (Join-Path $taskRoot 'build-cache/nuget')
    } else { dotnet restore App/VideoConverter.WinUI.csproj -r win-x64 --locked-mode }
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
    & (Join-Path $taskRoot 'tools/Collect-Licenses.ps1') -ProjectRoot $taskRoot -PythonRuntime $taskPython
    $taskPublish = @('publish','App/VideoConverter.WinUI.csproj','--no-restore','-c','Release','-r','win-x64','-p:Platform=x64','-o',$taskOutput)
    foreach ($taskName in @('SelfContained','WindowsAppSDKSelfContained','PublishSingleFile','IncludeAllContentForSelfExtract','EnableCompressionInSingleFile')) {
        $taskPublish += "-p:$taskName=$($taskBundled.ToString().ToLowerInvariant())"
    }
    dotnet @taskPublish
    if ($LASTEXITCODE -ne 0) { throw 'WinUI publish failed.' }
    gcc -Os -s -nostdlib -ffreestanding -fno-builtin -fno-stack-protector '-Wl,--subsystem,console' '-Wl,-e,FrameFlowEntry' Native/CliLauncher.c -lkernel32 -o (Join-Path $taskOutput 'FrameFlow.com')
    if ($LASTEXITCODE -ne 0) { throw 'Native console launcher build failed; install MinGW-w64 GCC.' }
    $taskImports = @(objdump -p (Join-Path $taskOutput 'FrameFlow.com') | Select-String 'DLL Name:' | ForEach-Object { ($_ -split 'DLL Name:')[1].Trim() })
    if ($LASTEXITCODE -ne 0 -or $taskImports.Count -ne 1 -or $taskImports[0] -ine 'KERNEL32.dll') { throw 'Unexpected runtime dependency in the native console launcher.' }
    $taskVersion = ([xml](Get-Content -LiteralPath 'App/VideoConverter.WinUI.csproj' -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if ($taskBundled) {
        $taskZip = Join-Path $taskRoot "dist/FrameFlow-$taskVersion-win-x64.zip"
        Compress-Archive -LiteralPath (Join-Path $taskOutput 'FrameFlow.exe'),(Join-Path $taskOutput 'FrameFlow.com') -DestinationPath $taskZip -Force
        Write-Output "Built GUI + CLI portable ZIP: $taskZip"
    }
    else {
        $taskZip = Join-Path $taskRoot "dist/FrameFlow-$taskVersion-win-x64-framework-dependent.zip"
        Compress-Archive -Path "$taskOutput/*" -DestinationPath $taskZip -Force
        Write-Output "Built framework-dependent ZIP: $taskZip"
    }
} finally { Pop-Location }
