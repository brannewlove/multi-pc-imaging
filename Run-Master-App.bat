@echo off
setlocal
title MultiDeployOS Master Desktop App
cd /d "%~dp0master"

echo ================================================================
echo  [MultiDeployOS] Starting Desktop Application...
echo ================================================================
echo.

if not exist "node_modules\electron" (
    echo [INFO] Installing Electron dependencies...
    call npm install
)

echo [INFO] Launching MultiDeployOS Desktop App...
call npm start

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] Application stopped with error code %ERRORLEVEL%.
    pause
)
endlocal