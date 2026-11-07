; 星枢投屏 · NSIS 安装包（向导由 NSIS 出，文件搬运交给 PowerShell，避开 NSIS 直写盘的问题）
Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"

!define APPNAME "星枢投屏"
!define COMPANY "星枢[未来科技]"
!define VERSION "1.1.0"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\XingshuCast"

Name "${APPNAME} ${VERSION}"
OutFile "..\release\xingshu-cast-setup-${VERSION}.exe"
InstallDir "$LOCALAPPDATA\XingshuCast\app"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 64

!define MUI_ICON "icon.ico"
!define MUI_UNICON "icon.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\XingshuCast.exe"
!define MUI_FINISHPAGE_RUN_TEXT "立即运行 ${APPNAME}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "说明.txt"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH


!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  ${IfNot} ${RunningX64}
    IfSilent +3 0
    MessageBox MB_ICONSTOP "安装失败（PowerShell 返回 $0）$\r$\n日志：%TEMP%\xingshu-cast-install.log"
    SetErrorLevel 2
    Abort
  ${EndIf}
FunctionEnd

Section "Install"
  ; 先解到临时目录（NSIS 写临时目录没问题），再交给 PowerShell 搬到安装目录
  SetOutPath "$PLUGINSDIR\payload"
  File /r "..\build\stage\*.*"
  File "install.ps1"
  File "install.cmd"
  File "uninstall.ps1"

  DetailPrint "正在安装到 $INSTDIR …"
  nsExec::ExecToLog '"$PLUGINSDIR\payload\install.cmd" "$INSTDIR"'
  Pop $0
  ${If} $0 != "0"
    SetErrorLevel 2
    StrCpy $2 ""
    ClearErrors
    FileOpen $3 "$TEMP\xingshu-cast-install.log" r
    ${IfNot} ${Errors}
      ${Do}
        FileRead $3 $4
        ${If} ${Errors}
          ${ExitDo}
        ${EndIf}
        StrCpy $2 "$2$4"
      ${Loop}
      FileClose $3
    ${EndIf}
    ${If} $2 == ""
      StrCpy $2 "（没拿到日志，请把 %TEMP%\xingshu-cast-install.log 和 xingshu-cast-cmd.log 发给作者）"
    ${EndIf}
    IfSilent +2 0
    MessageBox MB_ICONSTOP "安装失败（返回码 $0）$\r$\n$2"
    Abort
  ${EndIf}

SectionEnd
