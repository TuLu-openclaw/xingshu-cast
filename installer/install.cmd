@echo off
rem ASCII only: NSIS calls this, this calls PowerShell
set LOG=%TEMP%\xingshu-cast-cmd.log
echo [%DATE% %TIME%] payload=%~dp0 target=%1 >> "%LOG%"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -Payload "%~dp0" -Target "%1" >> "%LOG%" 2>&1
echo [%DATE% %TIME%] ps exit=%ERRORLEVEL% >> "%LOG%"
exit /b %ERRORLEVEL%
