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

:: 4. 다중 PC 이미지 더빙 에이전트 자동 실행
echo Starting PeImagingAgent.exe...
X:\Windows\System32\PeImagingAgent.exe

:: 에이전트 종료 시(더빙 완료) 시스템 재부팅
echo Imaging process ended. Rebooting system in 5 seconds...
wpeutil reboot
