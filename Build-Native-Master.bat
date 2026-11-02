@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion
title MultiDeployOS - Native Master App Builder
cd /d "%~dp0"

echo ================================================================
echo  [MultiDeployOS] Building Windows Native C# Master App (.NET 8)
echo ================================================================
echo.

:: 1. 실행 중인 기존 마스터 프로세스가 있으면 자동 종료 (파일 잠금 에러 방지)
echo [INFO] 실행 중인 기존 마스터 앱 확인 및 종료...
taskkill /F /IM MultiDeployMaster.exe >nul 2>nul
timeout /t 1 /nobreak >nul

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
    echo ================================================================
    echo  [ERROR] 빌드에 실패했습니다! 위 에러 메시지를 확인하세요.
    echo ================================================================
    pause
    exit /b 1
)

:: 기본 설정 파일 동기화 보장 (기존 사용자 설정 보존)
if not exist "bin\config" mkdir "bin\config"
for %%F in ("MasterApp\config\*.json") do (
    if not exist "bin\config\%%~nxF" copy /y "%%F" "bin\config\" >nul 2>nul
)

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
echo [안내] 아무 키나 누르면 빌드된 최신 마스터 앱을 바로 실행합니다.
pause >nul
start "" "%~dp0bin\MultiDeployMaster.exe"
endlocal
