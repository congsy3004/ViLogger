@echo off
setlocal

set "PS_SCRIPT=%~dp0sync_latest_exe.ps1"
set "RAW_URL=https://raw.githubusercontent.com/congsy3004/ViLogger/main/tools/sync_latest_exe.ps1"

if not exist "%PS_SCRIPT%" (
    echo Downloading sync_latest_exe.ps1...
    powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -Uri $env:RAW_URL -OutFile $env:PS_SCRIPT -UseBasicParsing"
    if errorlevel 1 (
        echo.
        echo [ERROR] Failed to download sync_latest_exe.ps1.
        pause
        exit /b 1
    )
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PS_SCRIPT%" -RegisterTask
set "EC=%ERRORLEVEL%"
if %EC% NEQ 0 (
    echo.
    echo [ERROR] Setup exited with code %EC%.
)

echo.
pause
exit /b %EC%
