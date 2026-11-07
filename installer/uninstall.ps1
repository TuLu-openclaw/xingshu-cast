# 星枢投屏 · 卸载
param([switch]$Quiet)
$ErrorActionPreference = 'SilentlyContinue'
$target = Split-Path -Parent $MyInvocation.MyCommand.Path

Get-Process 'XingshuCast' | Stop-Process -Force
foreach ($d in @([Environment]::GetFolderPath('Desktop'), (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'))) {
    $lnk = Join-Path $d '星枢投屏.lnk'
    if (Test-Path $lnk) { Remove-Item $lnk -Force }
}
$prog = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\星枢投屏'
if (Test-Path $prog) { Remove-Item $prog -Recurse -Force }
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\XingshuCast' -Recurse -Force

Start-Sleep -Milliseconds 600
Remove-Item $target -Recurse -Force
