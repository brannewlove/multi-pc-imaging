@echo off
setlocal
title MultiDeployOS - Exe Builder
cd /d "%~dp0master"

echo ================================================================
echo  [MultiDeployOS] Building Standalone Portable .exe Package...
echo ================================================================
echo.

if not exist "node_modules\electron-builder" (
    echo [INFO] Installing electron-builder dependencies...
    call npm install
)

echo [INFO] Running electron-builder (target: Windows portable)...
call npm run build:exe

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] Build failed with error code %ERRORLEVEL%.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo ================================================================
echo  [SUCCESS] Build Complete! Output location: master\dist\
echo ================================================================
echo.
pause
endlocal