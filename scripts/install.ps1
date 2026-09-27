<#
.SYNOPSIS
  Installs SnagItOpen for the current user (no admin rights needed).
.DESCRIPTION
  Copies the published app to %LOCALAPPDATA%\Programs\SnagItOpen, creates a Start menu shortcut
  (and optionally a desktop shortcut), can start SnagItOpen in the tray at sign-in, and launches it.
  Run scripts\publish.ps1 first, or pass -Source with a folder containing SnagItOpen.exe.
  Uninstall with:  powershell -ExecutionPolicy Bypass -File "%LOCALAPPDATA%\Programs\SnagItOpen\uninstall.ps1"
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1 -StartWithWindows -DesktopShortcut
#>
param(
    [string]$Source,
    [switch]$StartWithWindows,
    [switch]$DesktopShortcut,
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $Source) { $Source = Join-Path $root "artifacts\win-x64" }
if (-not (Test-Path (Join-Path $Source "SnagItOpen.exe"))) {
    throw "SnagItOpen.exe was not found in '$Source'. Run scripts\publish.ps1 first."
}

$target = Join-Path $env:LOCALAPPDATA "Programs\SnagItOpen"
$exe = Join-Path $target "SnagItOpen.exe"

# Stop a running copy so its files can be replaced.
Get-Process -Name "SnagItOpen" -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Closing the running SnagItOpen..."
    $_.CloseMainWindow() | Out-Null
    if (-not $_.WaitForExit(3000)) { $_.Kill() }
}

Write-Host "Installing to $target"
if (Test-Path $target) { Remove-Item -LiteralPath $target -Recurse -Force }
New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item -Path (Join-Path $Source "*") -Destination $target -Recurse -Force

$shell = New-Object -ComObject WScript.Shell
function New-Shortcut([string]$path, [string]$arguments = "") {
    $lnk = $shell.CreateShortcut($path)
    $lnk.TargetPath = $exe
    $lnk.Arguments = $arguments
    $lnk.WorkingDirectory = $target
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = "SnagItOpen screen capture and image combiner"
    $lnk.Save()
}

$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
New-Shortcut (Join-Path $startMenu "SnagItOpen.lnk")
if ($DesktopShortcut) { New-Shortcut (Join-Path ([Environment]::GetFolderPath("Desktop")) "SnagItOpen.lnk") }

$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
if ($StartWithWindows) {
    New-ItemProperty -Path $runKey -Name "SnagItOpen" -Value "`"$exe`" --tray" -PropertyType String -Force | Out-Null
    Write-Host "SnagItOpen will start in the tray when you sign in."
}

# Uninstaller placed next to the app.
$uninstall = @'
$ErrorActionPreference = "Continue"
Get-Process -Name "SnagItOpen" -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "SnagItOpen" -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\SnagItOpen.lnk") -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath("Desktop")) "SnagItOpen.lnk") -ErrorAction SilentlyContinue
$dir = Join-Path $env:LOCALAPPDATA "Programs\SnagItOpen"
Start-Process powershell -WindowStyle Hidden -ArgumentList "-NoProfile -Command Start-Sleep 1; Remove-Item -LiteralPath '$dir' -Recurse -Force"
Write-Host "SnagItOpen was removed. Your captures and settings remain in $env:LOCALAPPDATA\SnagItOpen (delete that folder to remove them)."
'@
Set-Content -LiteralPath (Join-Path $target "uninstall.ps1") -Value $uninstall -Encoding UTF8

Write-Host "Installed. Start menu: SnagItOpen."
if (-not $NoLaunch) { Start-Process -FilePath $exe -ArgumentList "--tray" -WorkingDirectory $target }
