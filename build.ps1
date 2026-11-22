# Builds HallowGear.exe with the C# compiler that ships with Windows (.NET Framework 4.8) - nothing to install.
#   .\build.ps1          the app
#   .\build.ps1 -Tests   also tests\GearTests.exe (unit tests from fixture bytes: HID++ 2.0 and A50 message building / parsing,
#                        the read-only allow-lists, the battery state machine and alert rules, status.json, the family pipe of a
#                        test copy with made-up devices). The tests never open a real device.
param([switch]$Tests)
$ErrorActionPreference = 'Stop'
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf = "$fw\WPF"
$src = Join-Path $PSScriptRoot 'src'
$out = Join-Path $PSScriptRoot 'HallowGear.exe'
$refs = "$wpf\PresentationFramework.dll", "$wpf\PresentationCore.dll", "$wpf\WindowsBase.dll", "$fw\System.Xaml.dll",
    "$fw\System.Windows.Forms.dll", "$fw\System.Drawing.dll", "$fw\System.Runtime.Serialization.dll", "$fw\System.Core.dll",
    "$fw\System.IO.Compression.dll", "$fw\System.IO.Compression.FileSystem.dll", "$fw\System.Web.Extensions.dll", "$fw\System.Xml.dll",
    # 1.1: Bluetooth (WinRT Windows.Devices.Bluetooth, read-only) - Windows' own metadata and the .NET WinRT bridge
    "$fw\System.Runtime.dll", "$fw\System.Runtime.WindowsRuntime.dll", "$fw\System.Runtime.InteropServices.WindowsRuntime.dll",
    "$env:WINDIR\System32\WinMetadata\Windows.Foundation.winmd", "$env:WINDIR\System32\WinMetadata\Windows.Devices.winmd", "$env:WINDIR\System32\WinMetadata\Windows.Storage.winmd",
    # 1.2: Windows toast notifications (Windows.UI.Notifications) with the device icon; their XML is Windows.Data.Xml.Dom
    "$env:WINDIR\System32\WinMetadata\Windows.UI.winmd", "$env:WINDIR\System32\WinMetadata\Windows.Data.winmd"
$common = @('/nologo', '/optimize+', "/win32icon:$src\hallow.ico", "/resource:$src\Theme.xaml,HallowGear.Theme.xaml") + ($refs | ForEach-Object { "/reference:$_" })
$files = Get-ChildItem $src -Filter *.cs | ForEach-Object FullName
& "$fw\csc.exe" @($common + @('/target:winexe', "/out:$out") + $files)
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
"Built $out ($([math]::Round((Get-Item $out).Length / 1KB)) KB)"
if ($Tests) {
    $t = Join-Path $PSScriptRoot 'tests\GearTests.exe'
    & "$fw\csc.exe" @($common + @('/target:exe', "/out:$t", '/main:HallowGear.Tests.Run') + $files + (Get-ChildItem (Join-Path $PSScriptRoot 'tests') -Filter *Tests.cs | ForEach-Object FullName))
    if ($LASTEXITCODE -ne 0) { throw "Test build failed" }
    "Built $t"
}
