@echo off
REM ==============================================================================
REM CJ 무인 프로비저너 사용자 로그인 시점 실행 등록 스크립트 (RunOnce)
REM
REM [역할]
REM 데스크톱 화면이 표시되고 사용자가 처음 로그인하는 시점에 대화형 UI로
REM 보안 프로그램을 실행하고 [설치 시작] 버튼을 클릭하도록 등록합니다.
REM ==============================================================================

set "PROVISIONER_EXE=C:\Temp\CJ_Work\MultiDeploy\CJAutoProvisioner.exe"

echo [INFO] HKLM RunOnce 레지스트리에 무인 프로비저너 등록 중...
reg add "HKLM\Software\Microsoft\Windows\CurrentVersion\RunOnce" /v "CJAutoProvisioner" /t REG_SZ /d "\"%PROVISIONER_EXE%\"" /f

if errorlevel 1 (
    echo [ERROR] RunOnce 등록 실패 (관리자 권한으로 실행해 주세요.)
    exit /b 1
) else (
    echo [SUCCESS] RunOnce 등록 성공: 최초 로그인 시 1회 실행 후 자동 삭제됩니다.
)
exit /b 0

