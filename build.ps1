$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
Push-Location -LiteralPath $taskRoot
try {
    if (-not (Test-Path -LiteralPath '.venv/Scripts/python.exe')) {
        python -m venv .venv
        if ($LASTEXITCODE -ne 0) { throw 'Cannot create Python environment.' }
    }
    & .venv/Scripts/python.exe -m pip install -r requirements.txt
    if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed.' }
    $taskPreviousPath = $env:PATH
    try {
        # Keep unrelated third-party ICU/UCRT DLLs on PATH out of the executable.
        $env:PATH = "$env:SystemRoot/System32;$env:SystemRoot"
        & .venv/Scripts/python.exe -m PyInstaller --noconfirm --clean --onefile --windowed --name VideoConverter --icon video-converter.ico --add-data 'src/assets;assets' --distpath dist --workpath build --specpath . src/main.py
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    } finally {
        $env:PATH = $taskPreviousPath
    }
    Write-Host 'Built dist/VideoConverter.exe'
} finally {
    Pop-Location
}
