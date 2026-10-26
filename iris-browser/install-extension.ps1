# Copies the extension to a stable folder and opens Chrome's extensions page.
# Chrome 137+ no longer accepts --load-extension, so the one-time "Load unpacked" click stays manual.
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'extension'
$target = Join-Path $env:LOCALAPPDATA 'Iris\browser-extension'
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force
Set-Clipboard -Value $target

$chrome = $null
foreach ($key in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe',
                 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe') {
    $entry = Get-ItemProperty $key -ErrorAction SilentlyContinue
    if ($entry) { $chrome = $entry.'(default)'; break }
}

Write-Host "Extension copied to: $target (path is on your clipboard)"
Write-Host '1. Turn on Developer mode (top right).'
Write-Host '2. Click Load unpacked and paste that folder.'
Write-Host '3. In Iris, ask her to pair the browser, then type the code into the extension popup.'
if ($chrome) { Start-Process $chrome 'chrome://extensions' } else { Write-Host 'Chrome was not found; open chrome://extensions yourself.' }
