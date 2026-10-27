$ErrorActionPreference = 'Stop'
$python = Get-Command py.exe -ErrorAction SilentlyContinue
if ($python) {
    & $python.Source -3.12 -m venv (Join-Path $PSScriptRoot '.venv')
} else {
    $python = Get-Command python.exe -ErrorAction SilentlyContinue
    if (-not $python) { throw 'Python 3.12 is required. Install it from python.org, then run Setup again.' }
    & $python.Source -m venv (Join-Path $PSScriptRoot '.venv')
}

$venvPython = Join-Path $PSScriptRoot '.venv\Scripts\python.exe'
& $venvPython -m pip install --disable-pip-version-check -r (Join-Path $PSScriptRoot 'requirements.txt')
if ($LASTEXITCODE -ne 0) { throw 'Telegram dependencies did not install. Check the network connection and try Setup again.' }
Write-Host 'Telegram module runtime is ready.'
