@echo off
chcp 65001 >nul
setlocal
title MultiDeployOS - Windows Native Master App
cd /d "%~dp0"

if not exist "%~dp0bin\MultiDeployMaster.exe" (
    echo ================================================================
    echo  [MultiDeployOS] Native Master binary not found. Building now...
    echo ================================================================
    call "%~dp0Build-Native-Master.bat"
    if errorlevel 1 (
        echo [ERROR] Build failed.
        pause
        exit /b 1
    )
)

echo [INFO] Starting MultiDeployOS Windows Native Master App...
start "" "%~dp0bin\MultiDeployMaster.exe"
endlocal
