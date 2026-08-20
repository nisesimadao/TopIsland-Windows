$ErrorActionPreference = 'Stop'

Get-Process TopIsland -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 200

$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\TopIsland'
$shortcutPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\TopIsland.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

if (Test-Path $shortcutPath) {
    Remove-Item $shortcutPath -Force
}
if (Test-Path $runKey) {
    Remove-ItemProperty -Path $runKey -Name 'TopIsland' -ErrorAction SilentlyContinue
}
if (Test-Path $installRoot) {
    Remove-Item $installRoot -Recurse -Force
}

Write-Output 'TopIsland was removed. User settings in %APPDATA%\TopIsland were left intact.'
