@echo off
chcp 65001 >nul
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
    if exist "%ProgramFiles%\dotnet\dotnet.exe" (
        set "DOTNET_CMD=%ProgramFiles%\dotnet\dotnet.exe"
    ) else if exist "%USERPROFILE%\.dotnet\dotnet.exe" (
        set "DOTNET_CMD=%USERPROFILE%\.dotnet\dotnet.exe"
    ) else (
        echo [ERROR] .NET 8 SDK가 설치되어 있지 않습니다.
        echo.
        echo 아래 명령어로 .NET 8 SDK를 설치하거나 공식 사이트에서 설치해주세요:
        echo   [PowerShell/CMD] winget install Microsoft.DotNet.SDK.8
        echo   [웹 다운로드] https://dotnet.microsoft.com/download/dotnet/8.0
        echo.
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

:: 기본 설정 파일 동기화 보장
if not exist "bin\config" mkdir "bin\config"
copy /y "MasterApp\config\*.json" "bin\config\" >nul 2>nul

:: PXE UEFI 64-bit iPXE + wimboot 부트 에셋 동기화
if not exist "bin\pxe" mkdir "bin\pxe"
if exist "MasterApp\pxe\snponly.efi" copy /y "MasterApp\pxe\snponly.efi" "bin\pxe\" >nul 2>nul
if exist "MasterApp\pxe\wimboot" copy /y "MasterApp\pxe\wimboot" "bin\pxe\" >nul 2>nul

:: WinPE 원클릭 에이전트 자동 주입용 바이너리 동기화
if not exist "bin\agent" mkdir "bin\agent"
if exist "client\build\Release\PeImagingAgent.exe" (
    copy /y "client\build\Release\PeImagingAgent.exe" "bin\agent\" >nul 2>nul
) else if exist "client\build\PeImagingAgent.exe" (
    copy /y "client\build\PeImagingAgent.exe" "bin\agent\" >nul 2>nul
)
if exist "client\scripts\startnet.cmd" copy /y "client\scripts\startnet.cmd" "bin\agent\" >nul 2>nul

echo.
echo ================================================================
echo  [SUCCESS] Native Master App built successfully!
echo  Output: %~dp0bin\MultiDeployMaster.exe
echo ================================================================
echo.
endlocal
