@echo off
setlocal

rem Set REMOTE_PROJECT to your Antigravity Remote Project name, or leave empty to use your default remote project.
set "REMOTE_PROJECT=ARVSerialTool"

set "AGY_EXE="
for /f "delims=" %%I in ('where agy 2^>nul') do (
    if not defined AGY_EXE set "AGY_EXE=%%~fI"
)
if not defined AGY_EXE if exist "%LOCALAPPDATA%\agy\bin\agy.exe" set "AGY_EXE=%LOCALAPPDATA%\agy\bin\agy.exe"

if not defined AGY_EXE (
    echo [ERROR] 'agy' CLI was not found on PATH or at "%LOCALAPPDATA%\agy\bin\agy.exe".
    pause
    exit /b 1
)

if exist "%~dp0..\EverLogger.sln" cd /d "%~dp0.."

if defined REMOTE_PROJECT (
    "%AGY_EXE%" --remote-agent --project "%REMOTE_PROJECT%" %*
) else (
    "%AGY_EXE%" --remote-agent %*
)
set "EC=%ERRORLEVEL%"
if %EC% NEQ 0 (
    echo.
    echo [ERROR] agy exited with code %EC%.
    echo Ensure Remote Project "%REMOTE_PROJECT%" exists at https://antigravity.google
    echo or edit REMOTE_PROJECT in tools\start_remote_agent.bat.
    pause
    exit /b %EC%
)

exit /b 0
