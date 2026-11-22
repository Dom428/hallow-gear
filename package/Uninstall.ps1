# Hallow Gear uninstaller (run from Windows Settings > Apps, or directly).
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Hallow Gear'
Add-Type -AssemblyName System.Windows.Forms
if ([System.Windows.Forms.MessageBox]::Show("Uninstall Hallow Gear?", 'Uninstall Hallow Gear', 'YesNo', 'Question') -ne 'Yes') { return }
if (Get-Process HallowGear -ErrorAction SilentlyContinue) { try { & (Join-Path $dest "HallowGear.exe") --cmd quit | Out-Null; Start-Sleep -Milliseconds 1500 } catch { } }
Get-Process HallowGear -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'HallowGear' -ErrorAction SilentlyContinue
foreach ($dir in [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop')) { Remove-Item (Join-Path $dir 'Hallow Gear.lnk') -ErrorAction SilentlyContinue }
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HallowGear' -Recurse -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:APPDATA 'HallowGear') -Recurse -Force -ErrorAction SilentlyContinue
Start-Process cmd.exe -ArgumentList "/c timeout /t 2 /nobreak >nul & rmdir /s /q `"$dest`"" -WindowStyle Hidden
[System.Windows.Forms.MessageBox]::Show("Hallow Gear is uninstalled.", 'Hallow Gear') | Out-Null
