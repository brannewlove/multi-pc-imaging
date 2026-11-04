@echo off
:: UTF-8 코드페이지 설정 (콘솔 한글 깨짐 방지)
chcp 65001 > nul
title WinPE Multi-PC Imaging Client Initializer
echo ========================================================
echo   Initializing WinPE Network and Environment...
echo ========================================================

:: 1. WinPE 기본 네트워크 스택 및 DHCP 초기화
wpeinit

:: 2. 네트워크 IP 할당 대기 (2~3초)
ping 127.0.0.1 -n 3 > nul

:: 3. 네트워크 어댑터 IP 상태 확인
ipconfig | findstr /i "IPv4"

:: 3-1. 연결된 USB/로컬 드라이브에서 최신 에이전트 및 프로비저너 자동 동기화
echo Checking for updated agent binaries on connected USB drives...
for %%D in (C D E F G H I J K L M N O P Q R S T U V W Y Z) do (
    if exist "%%D:\agent\PeImagingAgent.exe" (
        echo  - [FOUND] Syncing %%D:\agent\PeImagingAgent.exe to WinPE RAM...
        copy /y "%%D:\agent\PeImagingAgent.exe" "X:\Windows\System32\PeImagingAgent.exe" >nul 2>&1
        copy /y "%%D:\agent\CJAutoProvisioner.exe" "X:\Windows\System32\CJAutoProvisioner.exe" >nul 2>&1
    )
    if exist "%%D:\PeImagingAgent.exe" (
        echo  - [FOUND] Syncing %%D:\PeImagingAgent.exe to WinPE RAM...
        copy /y "%%D:\PeImagingAgent.exe" "X:\Windows\System32\PeImagingAgent.exe" >nul 2>&1
        copy /y "%%D:\CJAutoProvisioner.exe" "X:\Windows\System32\CJAutoProvisioner.exe" >nul 2>&1
    )
)

:: 4. 다중 PC 이미지 더빙 에이전트 자동 실행 루프
:AGENT_LOOP
echo Starting PeImagingAgent.exe...
if exist "X:\Windows\System32\PeImagingAgent_latest.exe" (
    X:\Windows\System32\PeImagingAgent_latest.exe
) else if exist "X:\Windows\System32\PeImagingAgent.exe" (
    X:\Windows\System32\PeImagingAgent.exe
) else if exist "X:\PeImagingAgent.exe" (
    X:\PeImagingAgent.exe
) else (
    PeImagingAgent.exe
)

echo.
echo PeImagingAgent가 종료되었습니다. 5초 후 자동으로 재시작합니다... (중지하려면 Ctrl+C)
ping 127.0.0.1 -n 6 > nul
goto AGENT_LOOP
