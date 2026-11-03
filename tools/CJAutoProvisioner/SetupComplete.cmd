@echo off
REM ==============================================================================
REM CJ 보안 소프트웨어 무인 프로비저너 자동 실행 스크립트 (SetupComplete.cmd)
REM 
REM [역할]
REM Windows 설치 및 OOBE 완료 직후 최초 시스템 부팅 시점에 1회 자동 실행됩니다.
REM 
REM [배치 위치]
REM C:\Windows\Setup\Scripts\SetupComplete.cmd
REM (배포 마스터 이미지 또는 WinPE 단계에서 W:\Windows\Setup\Scripts\ 에 복사)
REM ==============================================================================

set "LOG_FILE=C:\Temp\CJ_Work\MultiDeploy\provision.log"
set "PROVISIONER_DIR=C:\Temp\CJ_Work\MultiDeploy"
set "PROVISIONER_EXE=%PROVISIONER_DIR%\CJAutoProvisioner.exe"

if not exist "%PROVISIONER_DIR%" mkdir "%PROVISIONER_DIR%"

echo [%DATE% %TIME%] [SetupComplete.cmd] Windows OOBE 완료 후 시스템 초기 부팅 감지 >> "%LOG_FILE%"

if exist "%PROVISIONER_EXE%" (
    echo [%DATE% %TIME%] [SetupComplete.cmd] CJAutoProvisioner 실행: %PROVISIONER_EXE% >> "%LOG_FILE%"
    start "" "%PROVISIONER_EXE%"
) else (
    REM 만약 C# exe 대신 PowerShell 버전을 사용하는 경우
    if exist "%PROVISIONER_DIR%\CJAutoProvisioner.ps1" (
        echo [%DATE% %TIME%] [SetupComplete.cmd] PowerShell 프로비저너 실행 >> "%LOG_FILE%"
        powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -File "%PROVISIONER_DIR%\CJAutoProvisioner.ps1"
    ) else (
        echo [%DATE% %TIME%] [SetupComplete.cmd] 프로비저너 실행 파일을 찾을 수 없습니다: %PROVISIONER_EXE% >> "%LOG_FILE%"
    )
)

exit /b 0
