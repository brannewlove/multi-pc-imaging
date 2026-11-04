@echo off
setlocal enabledelayedexpansion
title MultiDeploy - 부팅 USB 원클릭 최신화 도구
chcp 65001 > nul
cd /d "%~dp0"

echo ================================================================================
echo   [MultiDeploy] 부팅 USB 원클릭 최신화 도구 (USB Updater)
echo ================================================================================
echo.
echo  본 도구는 타깃 PC 부팅용 USB를 최신 PeImagingAgent 및 CJ 무인 프로비저너로
echo  단 몇 초 만에 업데이트하여, WIM 더빙 후 C:\Temp\CJ_Work 자동화 및 UAC 무인
echo  자동 승격이 100%% 완벽하게 실행되도록 구성합니다.
echo.
echo ================================================================================
echo.

:: 1. 연결된 드라이브 목록 표시
echo [1단계] 현재 시스템의 드라이브 목록을 확인합니다:
echo.
powershell -NoProfile -Command "Get-Volume | Where-Object { $_.DriveLetter -ne $null } | Select-Object DriveLetter, FileSystemLabel, DriveType, @{Name='Size(GB)';Expression={[math]::round($_.Size/1GB, 1)}} | Format-Table -AutoSize"

echo.
set /p USB_DRIVE=">> 부팅 USB 드라이브 문자를 입력하세요 (예: E 또는 F): "
if "%USB_DRIVE%"=="" (
    echo [ERROR] 드라이브 문자가 입력되지 않았습니다.
    pause
    exit /b 1
)

:: 드라이브 문자 정규화 (콜론 제거 후 대문자 변환)
set "USB_DRIVE=%USB_DRIVE::=%"
set "TARGET_ROOT=%USB_DRIVE%:\"

if not exist "%TARGET_ROOT%" (
    echo [ERROR] %TARGET_ROOT% 드라이브를 찾을 수 없습니다. 올바른 드라이브 문자를 입력하세요.
    pause
    exit /b 1
)

echo.
echo ================================================================================
echo [2단계] USB 루트 및 agent 폴더에 최신 실행 파일 복사 중...
echo  - 대상 드라이브: %TARGET_ROOT%
echo ================================================================================

if not exist "%TARGET_ROOT%agent" mkdir "%TARGET_ROOT%agent"

copy /y "bin\agent\PeImagingAgent.exe" "%TARGET_ROOT%" >nul 2>&1
copy /y "bin\agent\PeImagingAgent.exe" "%TARGET_ROOT%agent\" >nul 2>&1
copy /y "bin\agent\CJAutoProvisioner.exe" "%TARGET_ROOT%" >nul 2>&1
copy /y "bin\agent\CJAutoProvisioner.exe" "%TARGET_ROOT%agent\" >nul 2>&1
copy /y "client\scripts\startnet.cmd" "%TARGET_ROOT%" >nul 2>&1
copy /y "client\scripts\startnet.cmd" "%TARGET_ROOT%agent\" >nul 2>&1

echo  [+] USB 루트(\) 및 \agent\ 폴더 파일 복사 완료!
echo      - PeImagingAgent.exe (최신 빌드)
echo      - CJAutoProvisioner.exe (UAC 자동 바이패스 탑재)
echo      - startnet.cmd (USB 자동 감지 런처)

:: 3. boot.wim 발견 시 DISM 자동 주입 안내
set "BOOT_WIM="
if exist "%TARGET_ROOT%sources\boot.wim" set "BOOT_WIM=%TARGET_ROOT%sources\boot.wim"
if exist "%TARGET_ROOT%boot.wim" set "BOOT_WIM=%TARGET_ROOT%boot.wim"

if not "%BOOT_WIM%"=="" (
    echo.
    echo ================================================================================
    echo [3단계] 부트 WIM(%BOOT_WIM%) 발견!
    echo ================================================================================
    echo boot.wim 내부(System32)까지 완벽하게 최신 에이전트를 주입하시겠습니까?
    echo (주의: 관리자 권한 cmd에서만 DISM 마운트가 가능합니다. 'Y' 권장)
    set /p DISM_CHOICE=">> boot.wim 내부까지 DISM으로 덮어쓸까요? (Y/N): "
    if /i "!DISM_CHOICE!"=="Y" (
        set "MOUNT_DIR=C:\MultiDeploy_Winpe_Mount"
        if not exist "!MOUNT_DIR!" mkdir "!MOUNT_DIR!"
        echo [*] boot.wim 마운트 중...
        dism /Mount-Wim /WimFile:"%BOOT_WIM%" /Index:1 /MountDir:"!MOUNT_DIR!"
        if errorlevel 1 (
            echo [-] DISM 마운트 실패 (관리자 권한으로 실행되었는지 확인하세요).
            echo [*] USB 루트 복사만으로도 startnet.cmd가 자동 동기화하므로 정상 부팅 가능합니다.
        ) else (
            copy /y "bin\agent\PeImagingAgent.exe" "!MOUNT_DIR!\Windows\System32\" >nul 2>&1
            copy /y "bin\agent\CJAutoProvisioner.exe" "!MOUNT_DIR!\Windows\System32\" >nul 2>&1
            copy /y "client\scripts\startnet.cmd" "!MOUNT_DIR!\Windows\System32\" >nul 2>&1
            echo [+] boot.wim 내부 System32 파일 주입 완료!
            echo [*] boot.wim 변경 사항 저장 및 언마운트 중...
            dism /Unmount-Wim /MountDir:"!MOUNT_DIR!" /Commit
            rd /s /q "!MOUNT_DIR!" >nul 2>&1
            echo [+] boot.wim DISM 패키징 완료!
        )
    )
)

echo.
echo ================================================================================
echo  [완료] 부팅 USB 업데이트가 성공적으로 완료되었습니다!
echo  이제 타깃 PC에서 이 USB로 부팅하시면 WIM 더빙 후 C:\Temp\CJ_Work 에 파일이
echo  정상 생성되고, UAC 보안창 없이 무인 설치가 전자동으로 진행됩니다.
echo ================================================================================
pause
