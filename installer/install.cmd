@echo off
rem ASCII only: NSIS calls this, this calls PowerShell
set LOG=%TEMP%\xingshu-cast-cmd.log
set PAYLOAD=%~dp0
if "%PAYLOAD:~-1%"=="\" set PAYLOAD=%PAYLOAD:~0,-1%
echo [%DATE% %TIME%] payload=%PAYLOAD% target=%1 >> "%LOG%"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PAYLOAD%\install.ps1" -Payload "%PAYLOAD%" -Target "%1" >> "%LOG%" 2>&1
set RC=%ERRORLEVEL%
echo [%DATE% %TIME%] ps exit=%RC% >> "%LOG%"
exit /b %RC%
