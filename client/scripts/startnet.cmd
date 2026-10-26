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

:: 4. 다중 PC 이미지 더빙 에이전트 자동 실행 루프
:AGENT_LOOP
echo Starting PeImagingAgent.exe...
if exist "X:\Windows\System32\PeImagingAgent.exe" (
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
