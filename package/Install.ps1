# Hallow Gear installer: copies the app to your user folder (no admin needed) and adds Start menu + Desktop shortcuts.
# Hallow Gear only reads your gear's battery - it never changes a device setting.
#   Install.ps1 [-NoStart]   (-NoStart: don't open the app afterwards)
param([switch]$NoStart)
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Hallow Gear'
New-Item -ItemType Directory -Force $dest | Out-Null
$wasRunning = [bool](Get-Process HallowGear -ErrorAction SilentlyContinue)
if ($wasRunning) { try { $old = Join-Path $dest "HallowGear.exe"; if (Test-Path $old) { & $old --cmd quit | Out-Null; Start-Sleep -Milliseconds 1500 } } catch { } }
Get-Process HallowGear -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Copy-Item (Join-Path $PSScriptRoot 'HallowGear.exe') $dest -Force
foreach ($f in 'README.txt', 'Uninstall.ps1', 'FontAwesome-LICENSE.txt') { Copy-Item (Join-Path $PSScriptRoot $f) $dest -Force -ErrorAction SilentlyContinue }
$exe = Join-Path $dest 'HallowGear.exe'
# Windows Settings > Apps entry (per user, no admin)
$u = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HallowGear'
New-Item $u -Force | Out-Null
$ver = (Get-Item $exe).VersionInfo.FileVersion -replace '\.0\.0$', ''
$vals = @{ DisplayName = 'Hallow Gear'; DisplayVersion = $ver; Publisher = 'Hallow'; DisplayIcon = $exe; InstallLocation = $dest
    UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$(Join-Path $dest 'Uninstall.ps1')`""
    URLInfoAbout = 'https://github.com/Dom428/hallow-gear' }
foreach ($k in $vals.Keys) { Set-ItemProperty $u -Name $k -Value $vals[$k] }
Set-ItemProperty $u -Name NoModify -Value 1 -Type DWord; Set-ItemProperty $u -Name NoRepair -Value 1 -Type DWord
Set-ItemProperty $u -Name EstimatedSize -Value ([int]((Get-Item $exe).Length / 1KB)) -Type DWord
$w = New-Object -ComObject WScript.Shell
foreach ($dir in [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop')) {
    $s = $w.CreateShortcut((Join-Path $dir 'Hallow Gear.lnk'))
    $s.TargetPath = $exe; $s.WorkingDirectory = $dest; $s.Description = 'Battery and charging of your Logitech gear'; $s.Save()
}
if ($NoStart) { Write-Host "Hallow Gear is installed. It's in your Start menu and on your Desktop."; return }
# after an update it comes back the way it was (in the tray if it was running); a fresh install opens the window
if ($wasRunning) { Start-Process $exe -ArgumentList '--tray' } else { Start-Process $exe }
Write-Host "Hallow Gear is installed. It's in your Start menu and on your Desktop."
