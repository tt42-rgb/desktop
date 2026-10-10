# Run on Windows with Python 3.12; end users do not need Python.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:OS -ne 'Windows_NT') {
    throw 'This script must run on Windows. Linux cannot produce a Windows executable.'
}

Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    python -m pip install --requirement requirements-build.txt
    if ($LASTEXITCODE -ne 0) { throw 'Packaging dependencies could not be installed.' }

    python -m unittest discover -s tests -v
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed. No executable will be distributed.' }

    python -m PyInstaller --noconfirm --clean --onefile --windowed `
        --name '桌面整理' --specpath build --workpath build/pyinstaller `
        --distpath dist main.py
    if ($LASTEXITCODE -ne 0) { throw 'Windows executable packaging failed.' }

    $executable = Join-Path $PWD 'dist/桌面整理.exe'
    if (-not (Test-Path -LiteralPath $executable)) { throw 'The executable was not created.' }

    # Exercise bundled Python, Tcl/Tk, Unicode paths and file operations.
    # A new output file is required, so old successful results cannot hide a failure.
    $resultPath = Join-Path $PWD ('build/smoke-' + [guid]::NewGuid().ToString('N') + '.json')
    $process = Start-Process -FilePath $executable -ArgumentList @(
        '--self-test', ('"' + $resultPath + '"')
    ) -PassThru
    if (-not $process.WaitForExit(60000)) {
        Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
        throw 'The packaged executable did not complete its self-test within 60 seconds.'
    }
    $process.Refresh()
    if ($process.ExitCode -ne 0) { throw 'The packaged executable failed its self-test.' }
    if (-not (Test-Path -LiteralPath $resultPath)) { throw 'No fresh self-test result was produced.' }
    $result = Get-Content -LiteralPath $resultPath -Raw -Encoding utf8 | ConvertFrom-Json
    if ($result.ok -ne $true -or @($result.checks).Count -ne 5) {
        throw ('The packaged self-test did not pass: ' + ($result | ConvertTo-Json -Depth 5))
    }

    $release = Join-Path $PWD 'build/windows-release'
    New-Item -Path $release -ItemType Directory -Force | Out-Null
    Copy-Item -LiteralPath $executable -Destination (Join-Path $release '桌面整理.exe') -Force
    Copy-Item -LiteralPath 'docs/windows-readme.txt' -Destination (Join-Path $release '使用说明.txt') -Force
    Compress-Archive -Path (Join-Path $release '*') -DestinationPath 'dist/DesktopOrganizer-Windows.zip' -Force
    Write-Host 'Windows executable and ZIP are ready. No Python installation is needed to run them.'
} finally {
    Pop-Location
}
