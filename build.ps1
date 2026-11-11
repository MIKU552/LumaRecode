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
        & .venv/Scripts/python.exe -m PyInstaller --noconfirm --onefile --console --name ConverterWorker --add-data 'Worker/assets;assets' --distpath Payload --workpath worker-build --specpath . Worker/worker.py
        if ($LASTEXITCODE -ne 0) { throw 'Worker build failed.' }
    } finally { $env:PATH = $taskPreviousPath }
    dotnet publish App/VideoConverter.WinUI.csproj -c Release -r win-x64 -p:Platform=x64 -o dist
    if ($LASTEXITCODE -ne 0) { throw 'WinUI build failed.' }
    Write-Host 'Built dist/VideoConverter.WinUI_3.1.exe'
} finally { Pop-Location }
