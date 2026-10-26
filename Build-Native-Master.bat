@echo off
setlocal enabledelayedexpansion
title MultiDeployOS - Native Master App Builder
cd /d "%~dp0"

echo ================================================================
echo  [MultiDeployOS] Building Windows Native C# Master App (.NET 8)
echo ================================================================
echo.

set "DOTNET_CMD=dotnet"
where dotnet >nul 2>nul
if errorlevel 1 (
    if exist "%USERPROFILE%\.dotnet\dotnet.exe" (
        set "DOTNET_CMD=%USERPROFILE%\.dotnet\dotnet.exe"
    ) else (
        echo [ERROR] .NET 8 SDK not found.
        echo Please ensure .NET 8 SDK is installed.
        pause
        exit /b 1
    )
)

echo [INFO] Using dotnet: !DOTNET_CMD!
echo [INFO] Publishing standalone native executable to bin...
echo.

"!DOTNET_CMD!" publish "MasterApp\MasterApp.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "bin"
if errorlevel 1 (
    echo.
    echo [ERROR] Build failed.
    pause
    exit /b 1
)

echo.
echo ================================================================
echo  [SUCCESS] Native Master App built successfully!
echo  Output: %~dp0bin\MultiDeployMaster.exe
echo ================================================================
echo.
endlocal
