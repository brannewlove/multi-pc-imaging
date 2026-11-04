@echo off
setlocal
title MultiDeploy - WinPE Native Agent Builder
cd /d "%~dp0.."

echo ================================================================
echo  [MultiDeploy] Building WinPE Native Client Agent (PeImagingAgent.exe)
echo ================================================================
echo.

if not exist "client\build" mkdir "client\build"
if not exist "client\build\Release" mkdir "client\build\Release"

set "BUILT=0"

where ninja >nul 2>nul
if %ERRORLEVEL% EQU 0 (
    echo [1/2] Building via ninja...
    ninja -C "client\build"
    if %ERRORLEVEL% EQU 0 set "BUILT=1"
)

if "%BUILT%"=="0" (
    where g++ >nul 2>nul
    if %ERRORLEVEL% EQU 0 (
        echo [2/2] Compiling via MinGW g++ static...
        g++ -std=c++17 -O2 -static -static-libgcc -static-libstdc++ -Iclient\src client\src\main.cpp client\src\hw_scanner.cpp client\src\net_client.cpp client\src\partition_engine.cpp client\src\deploy_engine.cpp -lws2_32 -liphlpapi -o "client\build\PeImagingAgent.exe"
        if %ERRORLEVEL% EQU 0 set "BUILT=1"
    )
)

if exist "client\build\PeImagingAgent.exe" (
    echo.
    echo [*] Syncing PeImagingAgent.exe to client\build\Release and bin\agent...
    copy /y "client\build\PeImagingAgent.exe" "client\build\Release\PeImagingAgent.exe" >nul 2>&1
    if not exist "bin\agent" mkdir "bin\agent"
    copy /y "client\build\PeImagingAgent.exe" "bin\agent\PeImagingAgent.exe" >nul 2>&1
    copy /y "client\scripts\startnet.cmd" "bin\agent\startnet.cmd" >nul 2>&1

    echo.
    echo ================================================================
    echo  [SUCCESS] PeImagingAgent.exe built and synced successfully!
    echo ================================================================
) else (
    echo.
    echo ================================================================
    echo  [ERROR] Build failed! Check compiler output above.
    echo ================================================================
    exit /b 1
)

endlocal
