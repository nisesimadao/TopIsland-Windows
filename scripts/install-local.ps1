param(
    [string]$SourceDirectory = (Join-Path $PSScriptRoot '..\artifacts\publish'),
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'

$source = (Resolve-Path $SourceDirectory).Path
$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\TopIsland'
$exe = Join-Path $installRoot 'TopIsland.exe'

Get-Process TopIsland,TopIsland.BlurHost -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 250

if (Test-Path $installRoot) {
    Remove-Item $installRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
Copy-Item (Join-Path $source '*') $installRoot -Recurse -Force

$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$shortcutPath = Join-Path $startMenu 'TopIsland.lnk'
$wsh = New-Object -ComObject WScript.Shell
$shortcut = $wsh.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $installRoot
$shortcut.IconLocation = "$exe,0"
$shortcut.Description = 'TopIsland'
$shortcut.Save()

# Preserve the current startup preference, but point it at the installed executable.
$settingsPath = Join-Path $env:APPDATA 'TopIsland\settings.json'
if (Test-Path $settingsPath) {
    try {
        $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
        if ($settings.StartWithWindows -eq $true) {
            $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
            New-Item -Path $runKey -Force | Out-Null
            Set-ItemProperty -Path $runKey -Name 'TopIsland' -Value ('"' + $exe + '"')
        }
    }
    catch {
        Write-Warning "Could not preserve startup preference: $($_.Exception.Message)"
    }
}

if (-not $NoLaunch) {
    Start-Process $exe
}

Write-Output "Installed TopIsland to $installRoot"
Write-Output "Start Menu shortcut: $shortcutPath"
