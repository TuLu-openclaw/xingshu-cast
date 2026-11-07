# 星枢投屏 · 构建脚本（在 GitHub Actions 的 windows-latest 上运行）
# 做的事：下载 scrcpy 与 platform-tools → 用系统自带 csc 编译客户端 → 组装 build\stage
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $root 'build\stage'
$dl = Join-Path $root 'build\download'

Write-Host '== 1/4 准备目录 =='
if (Test-Path (Join-Path $root 'build')) { Remove-Item (Join-Path $root 'build') -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
New-Item -ItemType Directory -Path $dl -Force | Out-Null

Write-Host '== 2/4 下载 scrcpy 5.0.1 与 platform-tools =='
$scrcpyZip = Join-Path $dl 'scrcpy.zip'
$ptZip = Join-Path $dl 'platform-tools.zip'
curl.exe -L --fail --retry 5 --retry-all-errors -o $scrcpyZip 'https://github.com/Genymobile/scrcpy/releases/download/v5.0.1/scrcpy-win64-v5.0.1.zip'
curl.exe -L --fail --retry 5 --retry-all-errors -o $ptZip 'https://dl.google.com/android/repository/platform-tools-latest-windows.zip'
Expand-Archive -Path $scrcpyZip -DestinationPath $dl -Force
Expand-Archive -Path $ptZip -DestinationPath $dl -Force

$scrcpyDir = Get-ChildItem $dl -Directory -Filter 'scrcpy-win64-*' | Select-Object -First 1
if (-not $scrcpyDir) { throw '没解出 scrcpy 目录' }
if (-not (Test-Path (Join-Path $dl 'platform-tools\adb.exe'))) { throw '没解出 adb.exe' }

Write-Host '== 3/4 编译客户端 =='
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "找不到 csc.exe：$csc" }
$src = Join-Path $root 'src\CastConsole.cs'
$outExe = Join-Path $stage '星枢投屏.exe'
& $csc /nologo /target:winexe /platform:anycpu /optimize+ "/out:$outExe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll $src
if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }
if (-not (Test-Path $outExe)) { throw '编译后没有 exe' }

Write-Host '== 4/4 组装 stage =='
Copy-Item $scrcpyDir.FullName -Destination (Join-Path $stage $scrcpyDir.Name) -Recurse
Copy-Item (Join-Path $dl 'platform-tools') -Destination (Join-Path $stage 'platform-tools') -Recurse
Copy-Item (Join-Path $root '使用说明.txt') -Destination (Join-Path $stage '使用说明.txt') -Force

$size = (Get-ChildItem $stage -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host ("stage 体积：{0:N1} MB" -f ($size / 1MB))
Get-ChildItem $stage | ForEach-Object { Write-Host (' - ' + $_.Name) }
Write-Host 'BUILD_STAGE_OK'
