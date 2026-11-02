@echo off
setlocal
title MultiDeployOS - Update and Build
cd /d "%~dp0"

echo ================================================================
echo  [MultiDeployOS] Git Sync and Native Master App Build
echo ================================================================
echo.

echo [1/3] Restoring local template configs to avoid merge conflicts...
git checkout -- MasterApp/config/ >nul 2>&1
git restore MasterApp/config/ >nul 2>&1

echo [2/3] Pulling latest updates from GitHub...
git pull origin main
if errorlevel 1 (
    echo.
    echo [WARNING] git pull encountered a conflict.
    echo Resetting local template changes to cleanly match origin/main...
    git fetch origin
    git reset --hard origin/main
)

echo.
echo [3/3] Building native master executable...
call "%~dp0Build-Native-Master.bat"

endlocal
