$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $PSScriptRoot
try {
    if (-not (Test-Path -LiteralPath '.venv/Scripts/python.exe')) {
        python -m venv .venv
        if ($LASTEXITCODE -ne 0) { throw 'Cannot create Python environment.' }
    }
    & .venv/Scripts/python.exe -m pip install 'PyInstaller==6.22.3'
    if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed.' }
    $taskPreviousPath = $env:PATH
    try {
        $env:PATH = "$env:SystemRoot/System32;$env:SystemRoot"
        & .venv/Scripts/python.exe -m PyInstaller --noconfirm --onefile --console --name ConverterWorker --add-data 'Worker/assets;assets' --add-data 'LICENSE;assets/FrameFlow' --add-data 'NOTICE;assets/FrameFlow' --distpath Payload --workpath worker-build --specpath . Worker/worker.py
        if ($LASTEXITCODE -ne 0) { throw 'Worker build failed.' }
    } finally { $env:PATH = $taskPreviousPath }
    dotnet publish App/VideoConverter.WinUI.csproj -c Release -r win-x64 -p:Platform=x64 -o dist
    if ($LASTEXITCODE -ne 0) { throw 'WinUI build failed.' }
    # Readable legal material for release preparation; the worker embeds it too.
    $taskLicenseOutput = Join-Path $PSScriptRoot 'dist/licenses'
    New-Item -ItemType Directory -Path $taskLicenseOutput -Force | Out-Null
    Copy-Item -LiteralPath 'LICENSE', 'NOTICE' -Destination $taskLicenseOutput -Force
    Copy-Item -LiteralPath 'Worker/assets/THIRD_PARTY.md', 'Worker/assets/license-manifest.json' -Destination $taskLicenseOutput -Force
    Get-ChildItem -LiteralPath 'Worker/assets' -File | Where-Object { $_.Name -match '(?i)license|notice|copying|authors' -and $_.Name -ne 'license-manifest.json' } | Copy-Item -Destination $taskLicenseOutput -Force
    Copy-Item -LiteralPath 'Worker/assets/OpenOSV' -Destination $taskLicenseOutput -Recurse -Force
    Write-Host 'Built dist/FrameFlow.exe'
    Write-Host 'Review dist/licenses and the release issues in Worker/assets/license-manifest.json before distributing a binary.'
} finally { Pop-Location }
