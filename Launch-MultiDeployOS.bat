@echo off
setlocal
title MultiDeployOS
cd /d "%~dp0master\dist\win-unpacked"

if not exist "MultiDeployOS.exe" (
    echo [ERROR] MultiDeployOS.exe not found in master\dist\win-unpacked\
    echo Please run Build-Master-Exe.bat first.
    pause
    exit /b 1
)

start "" "MultiDeployOS.exe"
endlocal