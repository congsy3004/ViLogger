@echo off
setlocal EnableExtensions

cd /d "%~dp0"
set "REPO_DIR=%CD%"
set "PUB_EXE=%REPO_DIR%\publish\ARVSerialTool.exe"
set "DIST_DIR=%REPO_DIR%\dist"
set "DIST_EXE=%DIST_DIR%\ARVSerialTool.exe"
set "PENDING_FLAG=%DIST_DIR%\.build_pending"

where git >nul 2>nul
if errorlevel 1 (
    echo [ERROR] Git was not found on PATH. Install Git for Windows first.
    pause
    exit /b 1
)

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] .NET SDK was not found on PATH. Install .NET 8 SDK first.
    pause
    exit /b 1
)

set "CUR_BRANCH="
for /f "delims=" %%B in ('git rev-parse --abbrev-ref HEAD 2^>nul') do set "CUR_BRANCH=%%B"
if /i not "%CUR_BRANCH%"=="main" (
    echo [ERROR] Current branch is "%CUR_BRANCH%". Please switch to "main" first.
    pause
    exit /b 1
)

set "HAS_CHANGES="
for /f "delims=" %%S in ('git status --porcelain') do (
    if not defined HAS_CHANGES set "HAS_CHANGES=1"
)
if defined HAS_CHANGES (
    echo [ERROR] Uncommitted local changes detected:
    git status -s
    echo Please commit, stash, or discard local changes before running this script.
    pause
    exit /b 1
)

for /f "delims=" %%H in ('git rev-parse HEAD') do set "OLD_HEAD=%%H"
echo Pulling latest commits from origin/main...
git pull --ff-only origin main
if errorlevel 1 (
    echo.
    echo [ERROR] git pull failed.
    pause
    exit /b 1
)

for /f "delims=" %%H in ('git rev-parse HEAD') do set "NEW_HEAD=%%H"
for /f "delims=" %%H in ('git rev-parse --short HEAD') do set "SHORT_HEAD=%%H"
if not "%OLD_HEAD%"=="%NEW_HEAD%" (
    if not exist "%DIST_DIR%" mkdir "%DIST_DIR%"
    echo %NEW_HEAD%> "%PENDING_FLAG%"
)

set "NEED_BUILD="
if not "%OLD_HEAD%"=="%NEW_HEAD%" set "NEED_BUILD=1"
if not exist "%DIST_EXE%" set "NEED_BUILD=1"
if exist "%PENDING_FLAG%" set "NEED_BUILD=1"
if /i "%~1"=="--force" set "NEED_BUILD=1"

if not defined NEED_BUILD (
    echo.
    echo Already up to date ^(commit %SHORT_HEAD%^), and dist\ARVSerialTool.exe exists.
    echo Skipping build.
    echo.
    echo Executable path:
    echo   %DIST_EXE%
    echo.
    pause
    exit /b 0
)

powershell.exe -NoProfile -Command "$targets = @($env:DIST_EXE, $env:PUB_EXE) | ForEach-Object { [IO.Path]::GetFullPath($_) }; foreach ($p in Get-Process -Name 'ARVSerialTool' -ErrorAction SilentlyContinue) { try { $f = [IO.Path]::GetFullPath($p.MainModule.FileName); if ($targets -contains $f) { exit 2 } } catch {} }; exit 0"
if errorlevel 2 (
    echo.
    echo [WARNING] ARVSerialTool.exe is currently running.
    echo Please close ARVSerialTool and double-click update_and_build.bat again to finish building.
    echo.
    pause
    exit /b 2
)

echo.
echo Publishing ARVSerialTool.exe ^(commit %SHORT_HEAD%^)...
dotnet publish "%REPO_DIR%\src\EverLogger.App\EverLogger.App.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "%REPO_DIR%\publish"
if errorlevel 1 (
    echo.
    echo [ERROR] Build failed.
    pause
    exit /b 1
)

if not exist "%DIST_DIR%" mkdir "%DIST_DIR%"
copy /Y "%PUB_EXE%" "%DIST_EXE%" >nul
if errorlevel 1 (
    echo.
    echo [ERROR] Failed to copy executable to "%DIST_EXE%".
    pause
    exit /b 1
)

if exist "%PENDING_FLAG%" del /F /Q "%PENDING_FLAG%" >nul 2>nul

echo.
echo Build succeeded! ^(commit %SHORT_HEAD%^)
echo.
echo Run the executable at:
echo   %DIST_EXE%
echo.
pause
exit /b 0
