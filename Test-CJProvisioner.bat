@echo off
setlocal enabledelayedexpansion
title MultiDeploy - CJ 보안 자동화 프로비저너 로컬 직접 테스트 도구
chcp 65001 > nul
cd /d "%~dp0"

echo ================================================================================
echo   [MultiDeploy] CJ 보안 소프트웨어 무인 프로비저너 로컬 직접 테스트 도구
echo ================================================================================
echo.
echo  이 도구는 배포(WinPE) 전에 현재 PC에서 직접:
echo   1) 사번(CJ_ID) 주입 및 setup_config.ini 설정 변경
echo   2) AutoSetup2.0.exe 보안 프로그램 자동 실행
echo   3) 프로그램 창 감지 및 [설치 시작] 버튼 자동 클릭
echo  위 전 과정을 즉시 시뮬레이션하고 테스트합니다.
echo.

set "TARGET_APP=C:\Temp\CJ_Work\AS_2.0\AutoSetup2.0.exe"
if not exist "%TARGET_APP%" set "TARGET_APP=C:\Temp\CJ_Work\AutoSetup2.0.exe"

if not exist "%TARGET_APP%" (
    echo [경고] 테스트 대상 프로그램(%TARGET_APP%)을 찾을 수 없습니다.
    echo C:\Temp\CJ_Work\AS_2.0 폴더에 AutoSetup2.0.exe 파일이 있는지 확인하세요.
    echo.
    pause
    exit /b 1
)

echo [감지] 대상 보안 프로그램 확인: %TARGET_APP%
echo.
echo [테스트 모드 선택]
echo   1. CASE A 테스트 : 사번 정상 발급 모드 (예: cjencmaster)
echo   2. CASE B 테스트 : 사번 공란 사전세팅 모드 (user_id 공란, sindoh_enabled=False)
echo.
set /p "CHOICE=선택 번호 (1 또는 2) [1]: "
if "%CHOICE%"=="" set "CHOICE=1"

set "TEST_ID=cjencmaster"
if "%CHOICE%"=="1" (
    set /p "USER_ID_INPUT=테스트할 사번 입력 [기본값: cjencmaster]: "
    if not "!USER_ID_INPUT!"=="" set "TEST_ID=!USER_ID_INPUT!"
    echo  -> CASE A 선택: 사번 '!TEST_ID!' 주입 모드로 테스트합니다.
) else (
    set "TEST_ID="
    echo  -> CASE B 선택: 사번 공란 모드로 테스트합니다.
)

:: 1. C:\Temp\CJ_Work\assigned_info.json 생성
if not exist "C:\Temp\CJ_Work" mkdir "C:\Temp\CJ_Work"
if not exist "C:\Temp\CJ_Work\MultiDeploy" mkdir "C:\Temp\CJ_Work\MultiDeploy"

(
    echo {
    echo   "enabled": true,
    echo   "assignedId": "!TEST_ID!"
    echo }
) > "C:\Temp\CJ_Work\assigned_info.json"
copy /y "C:\Temp\CJ_Work\assigned_info.json" "C:\Temp\CJ_Work\MultiDeploy\assigned_info.json" >nul 2>&1

echo.
echo [*] 테스트용 assigned_info.json 주입 완료 (assignedId: '!TEST_ID!')
echo [*] 프로비저너 실행 방식 선택:
echo   1. 무의존성 PowerShell 버전 (CJAutoProvisioner.ps1) - [권장 / 실전 배포 기본값]
echo   2. 네이티브 C# 독립 실행 파일 (CJAutoProvisioner.exe)
echo.
set /p "ENG_CHOICE=실행할 도구 선택 (1 또는 2) [1]: "
if "%ENG_CHOICE%"=="" set "ENG_CHOICE=1"

echo.
echo ================================================================================
echo [*] 프로비저너 동작을 시작합니다! (화면에 뜨는 창과 버튼 클릭을 관찰하세요)
echo ================================================================================
echo.

if "%ENG_CHOICE%"=="1" (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\CJAutoProvisioner\CJAutoProvisioner.ps1" -TimeoutSeconds 15
) else (
    copy /y "%~dp0bin\agent\CJAutoProvisioner.exe" "C:\Temp\CJ_Work\MultiDeploy\TestProvisioner.exe" >nul 2>&1
    "C:\Temp\CJ_Work\MultiDeploy\TestProvisioner.exe"
)

echo.
echo ================================================================================
echo [테스트 완료] 결과 요약:
echo ================================================================================
echo.
if exist "C:\Temp\CJ_Work\AS_2.0\setup_config.ini" (
    echo [설정 파일 C:\Temp\CJ_Work\AS_2.0\setup_config.ini 내용]
    type "C:\Temp\CJ_Work\AS_2.0\setup_config.ini"
)
echo.
if exist "C:\Temp\CJ_Work\provision.log" (
    echo [최근 프로비저닝 로그 C:\Temp\CJ_Work\provision.log]
    powershell -NoProfile -Command "Get-Content 'C:\Temp\CJ_Work\provision.log' -Tail 15"
)
echo.
pause

