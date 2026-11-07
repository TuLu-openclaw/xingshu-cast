# 星枢投屏

无线投屏控制台（Windows / .NET Framework 4.x / WinForms）。

- 主屏镜像（多窗同一块屏）
- 独立实例（每窗一块虚拟屏，Android 12+）
- 多设备「全部投屏」
- USB 连接 / USB 转无线（tcpip 5555）
- 拖 APK 安装；有 root 自动走 su，无 root 自动推包到手机 Download

## 构建安装包

安装包由 GitHub Actions 在 `windows-latest` 上构建（本机 makensis 不可用）：

1. `scripts/build.ps1` 下载 scrcpy 5.0.1 与 platform-tools，用系统 csc 编译 `src/CastConsole.cs`
2. `installer/xingshu-cast.nsi` 打成 NSIS 安装包
3. 产物上传到 Release

本地手动构建（需要 NSIS 与 .NET Framework）：

```powershell
./scripts/build.ps1
cd installer
makensis /INPUTCHARSET UTF8 xingshu-cast.nsi
```

## 源码

`src/CastConsole.cs` —— 单文件 WinForms 客户端（界面、设备发现、多开、安装逻辑都在里面）。
