@echo off
setlocal
title MultiDeployOS - Native Master App Builder
cd /d "%~dp0"

echo ================================================================
echo  [MultiDeployOS] Building Windows Native C# Master App (.NET 8)
echo ================================================================
echo.

taskkill /F /IM MultiDeployMaster.exe >nul 2>&1

set "DOTNET_CMD=dotnet"
where dotnet >nul 2>nul
if errorlevel 1 (
    if exist "%ProgramFiles%\dotnet\dotnet.exe" (
        set "DOTNET_CMD=%ProgramFiles%\dotnet\dotnet.exe"
    ) else if exist "%USERPROFILE%\.dotnet\dotnet.exe" (
        set "DOTNET_CMD=%USERPROFILE%\.dotnet\dotnet.exe"
    ) else (
        echo [ERROR] .NET 8 SDK is not installed.
        echo Please install .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
        pause
        exit /b 1
    )
)

echo [INFO] Using dotnet: %DOTNET_CMD%
echo [INFO] Publishing standalone native executable to bin...
echo.

"%DOTNET_CMD%" publish "MasterApp\MasterApp.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "bin"
if errorlevel 1 (
    echo.
    echo ================================================================
    echo  [ERROR] Build failed! Check the error messages above.
    echo ================================================================
    pause
    exit /b 1
)

REM Sync default configs
if not exist "bin\config" mkdir "bin\config"
for %%F in ("MasterApp\config\*.json") do (
    if not exist "bin\config\%%~nxF" copy /y "%%F" "bin\config\" >nul 2>nul
)
copy /y "MasterApp\config\partition_policies.json" "bin\config\" >nul 2>nul

REM Sync PXE assets
if not exist "bin\pxe" mkdir "bin\pxe"
if exist "MasterApp\pxe\snponly.efi" copy /y "MasterApp\pxe\snponly.efi" "bin\pxe\" >nul 2>nul
if exist "MasterApp\pxe\wimboot" copy /y "MasterApp\pxe\wimboot" "bin\pxe\" >nul 2>nul

REM Build & Sync WinPE Agent (PeImagingAgent.exe)
if exist "client\Build-Client-Agent.bat" (
    call "client\Build-Client-Agent.bat"
)

REM Build & Sync Provisioner assets
if exist "tools\CJAutoProvisioner\Build-Provisioner.bat" (
    call "tools\CJAutoProvisioner\Build-Provisioner.bat"
)

REM Sync Agent assets
if not exist "bin\agent" mkdir "bin\agent"
if exist "tools\CJAutoProvisioner\dist\CJAutoProvisioner.exe" (
    copy /y "tools\CJAutoProvisioner\dist\CJAutoProvisioner.exe" "bin\agent\" >nul 2>nul
    if not exist "bin\tools\CJAutoProvisioner\dist" mkdir "bin\tools\CJAutoProvisioner\dist"
    copy /y "tools\CJAutoProvisioner\dist\CJAutoProvisioner.exe" "bin\tools\CJAutoProvisioner\dist\" >nul 2>nul
)
if exist "tools\CJAutoProvisioner\SetupComplete.cmd" copy /y "tools\CJAutoProvisioner\SetupComplete.cmd" "bin\agent\" >nul 2>nul
if exist "client\build\PeImagingAgent.exe" (
    copy /y "client\build\PeImagingAgent.exe" "bin\agent\" >nul 2>nul
) else if exist "client\build\Release\PeImagingAgent.exe" (
    copy /y "client\build\Release\PeImagingAgent.exe" "bin\agent\" >nul 2>nul
)
if exist "client\scripts\startnet.cmd" copy /y "client\scripts\startnet.cmd" "bin\agent\" >nul 2>nul

echo.
echo ================================================================
echo  [SUCCESS] Native Master App built successfully!
echo  Output: %~dp0bin\MultiDeployMaster.exe
echo ================================================================
echo.
echo [SUCCESS] Starting updated MultiDeployMaster.exe...
start "" "%~dp0bin\MultiDeployMaster.exe"
endlocal
