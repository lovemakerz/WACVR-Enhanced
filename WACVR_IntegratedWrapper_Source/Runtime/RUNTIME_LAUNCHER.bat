@echo off
setlocal EnableExtensions
cd /d "%WACVR_INTEGRATED_ROOT%"

rem Preserve the validated V1.4 console-host chain:
rem public EXE -> cmd.exe -> BAT -> PowerShell -> WACVR Core / WACCA
start "" /b powershell.exe -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "%WACVR_INTEGRATED_ROOT%\.__WACVR_V020_FOCUS_GUARD.ps1"

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%WACVR_INTEGRATED_ROOT%\.__WACVR_V020_AUTO.ps1"
set "RC=%ERRORLEVEL%"

exit /b %RC%
