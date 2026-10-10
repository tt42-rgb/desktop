# Build native desktop fences. Users need neither Python nor .NET installed.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:OS -ne 'Windows_NT') { throw 'Windows is required for executable UI validation.' }
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet run --project windows/DesktopFences.Checks -c Release
    if ($LASTEXITCODE -ne 0) { throw 'File safety checks failed.' }
    dotnet publish windows/DesktopFences -c Release -r win-x64 --self-contained true -o dist/fences
    if ($LASTEXITCODE -ne 0) { throw 'Windows executable packaging failed.' }
    $executable = Join-Path $PWD 'dist/fences/桌面围栏.exe'
    if (-not (Test-Path -LiteralPath $executable)) { throw 'No Windows executable was created.' }
    New-Item -Path build -ItemType Directory -Force | Out-Null
    $resultPath = Join-Path $PWD ('build/fences-smoke-' + [guid]::NewGuid().ToString('N') + '.json')
    $process = Start-Process -FilePath $executable -ArgumentList @('--self-test', ('"' + $resultPath + '"')) -PassThru
    if (-not $process.WaitForExit(90000)) {
        Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
        throw 'The packaged Windows application did not finish its check in 90 seconds.'
    }
    $process.Refresh()
    if (-not (Test-Path -LiteralPath $resultPath)) { throw 'The executable produced no fresh check result.' }
    $result = Get-Content -LiteralPath $resultPath -Raw -Encoding utf8 | ConvertFrom-Json
    $summary = $result | ConvertTo-Json -Depth 6
    Write-Host $summary
    if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ('```json' + "`n" + $summary + "`n" + '```') }
    if ($process.ExitCode -ne 0 -or $result.ok -ne $true -or @($result.checks).Count -ne 23) {
        throw 'The packaged Windows checks failed. No release will be uploaded.'
    }
    $release = Join-Path $PWD 'build/windows-release'
    if (Test-Path -LiteralPath $release) { Remove-Item -LiteralPath $release -Recurse -Force }
    New-Item -Path $release -ItemType Directory | Out-Null
    Copy-Item -LiteralPath $executable -Destination (Join-Path $release '桌面围栏.exe')
    Copy-Item -LiteralPath 'docs/windows-readme.txt' -Destination (Join-Path $release '使用说明.txt')
    $hash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Value ($hash + '  桌面围栏.exe') -Encoding utf8
    Copy-Item -LiteralPath $resultPath -Destination (Join-Path $release '检查结果.json')
    Compress-Archive -Path (Join-Path $release '*') -DestinationPath 'dist/DesktopFences-Windows.zip' -Force
    Write-Host 'Ready: dist/DesktopFences-Windows.zip'
} finally { Pop-Location }
