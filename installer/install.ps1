# 星枢投屏 · 安装步骤（由 NSIS 安装器调用：NSIS 只负责向导，文件搬运交给 PowerShell）
param(
    [Parameter(Mandatory = $true)][string]$Payload,
    [Parameter(Mandatory = $true)][string]$Target
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Target)) { New-Item -ItemType Directory -Path $Target -Force | Out-Null }

# 1) 拷程序文件（跳过脚本自身）
Get-ChildItem $Payload -Force | Where-Object { $_.Name -notin @('install.ps1', 'uninstall.ps1') } | ForEach-Object {
    Copy-Item $_.FullName -Destination (Join-Path $Target $_.Name) -Recurse -Force
}

# 2) 快捷方式
$exe = Join-Path $Target 'XingshuCast.exe'
if (-not (Test-Path $exe)) { throw "安装后找不到 $exe" }
$sh = New-Object -ComObject WScript.Shell
foreach ($dir in @([Environment]::GetFolderPath('Desktop'), (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'))) {
    if (-not (Test-Path $dir)) { continue }
    $lnk = Join-Path $dir '星枢投屏.lnk'
    $s = $sh.CreateShortcut($lnk)
    $s.TargetPath = $exe
    $s.WorkingDirectory = $Target
    $s.IconLocation = $exe
    $s.Save()
}
$prog = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\星枢投屏'
if (-not (Test-Path $prog)) { New-Item -ItemType Directory -Path $prog -Force | Out-Null }
$u = Join-Path $Target 'uninstall.ps1'
$s2 = $sh.CreateShortcut((Join-Path $prog '卸载 星枢投屏.lnk'))
$s2.TargetPath = 'powershell.exe'
$s2.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $u + '"'
$s2.WorkingDirectory = $Target
$s2.Save()

# 3) 卸载脚本
Copy-Item (Join-Path $Payload 'uninstall.ps1') -Destination $u -Force

# 4) 注册表卸载项
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\XingshuCast'
New-Item -Path $key -Force | Out-Null
Set-ItemProperty $key -Name DisplayName -Value '星枢投屏'
Set-ItemProperty $key -Name DisplayVersion -Value '1.1.0'
Set-ItemProperty $key -Name Publisher -Value '星枢[未来科技]'
Set-ItemProperty $key -Name InstallLocation -Value $Target
Set-ItemProperty $key -Name DisplayIcon -Value $exe
Set-ItemProperty $key -Name UninstallString -Value ('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + $u + '"')
Set-ItemProperty $key -Name QuietUninstallString -Value ('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + $u + '" -Quiet')
Set-ItemProperty $key -Name NoModify -Value 1 -Type DWord
Set-ItemProperty $key -Name NoRepair -Value 1 -Type DWord

Write-Host 'INSTALL_PS_OK'
