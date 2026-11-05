@echo off
setlocal
cd /d "%~dp0"
title MultiDeploy CJ Provisioner Test
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\CJAutoProvisioner\Test-Provisioner.ps1"
echo.
pause
