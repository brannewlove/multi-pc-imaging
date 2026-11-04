@echo off
setlocal enabledelayedexpansion
title MultiDeploy - boot.wim 최신 에이전트 수동/원클릭 직접 주입 도구
chcp 65001 > nul
cd /d "%~dp0"

echo ================================================================================
echo   [MultiDeploy] WinPE boot.wim 최신 에이전트 직접 주입 도구
echo ================================================================================
echo.

:: 1. 관리자 권한 확인 및 자동 승격
openfiles >nul 2>&1
if %errorlevel% neq 0 (
    echo [*] DISM 작업을 위해 관리자 권한으로 승격합니다...
    powershell -NoProfile -Command "Start-Process cmd.exe -ArgumentList '/c \"\"%~f0\"\"' -Verb RunAs"
    exit /b
)

:: 2. 에이전트 실행 파일 검증 및 빌드 일시 확인
set "AGENT_EXE=%~dp0bin\agent\PeImagingAgent.exe"
if not exist "%AGENT_EXE%" set "AGENT_EXE=%~dp0client\build\Release\PeImagingAgent.exe"
if not exist "%AGENT_EXE%" set "AGENT_EXE=%~dp0client\build\PeImagingAgent.exe"

if not exist "%AGENT_EXE%" (
    echo [ERROR] 최신 PeImagingAgent.exe 바이너리를 찾을 수 없습니다!
    echo 먼저 client\Build-Client-Agent.bat 을 실행하여 빌드하세요.
    pause
    exit /b 1
)

echo [1단계] 주입할 최신 에이전트 바이너리 확인:
echo   - 경로: %AGENT_EXE%
powershell -NoProfile -Command "$f = Get-Item '%AGENT_EXE%'; Write-Host ('  - 용량: ' + [math]::round($f.Length/1MB, 2) + ' MB (' + $f.Length + ' 바이트) / 최종빌드: ' + $f.LastWriteTime)"
echo.

:: 3. 대상 boot.wim 파일 경로 결정
set "DEFAULT_WIM=C:\MasterImage\boot.wim"
if not exist "%DEFAULT_WIM%" (
    if exist "C:\images\boot.wim" set "DEFAULT_WIM=C:\images\boot.wim"
)

echo [2단계] 주입할 대상 boot.wim 파일 경로 확인:
if exist "%DEFAULT_WIM%" (
    echo 기본 감지된 경로: %DEFAULT_WIM%
    set /p USER_WIM=">> 대상 WIM 경로를 입력하거나 바로 Enter를 누르세요 [%DEFAULT_WIM%]: "
    if "!USER_WIM!"=="" set "TARGET_WIM=%DEFAULT_WIM%"
    if not "!USER_WIM!"=="" set "TARGET_WIM=!USER_WIM!"
) else (
    set /p TARGET_WIM=">> 대상 boot.wim 파일의 전체 경로를 입력하세요: "
)

set "TARGET_WIM=%TARGET_WIM:"=%"
if not exist "%TARGET_WIM%" (
    echo [ERROR] "%TARGET_WIM%" 파일을 찾을 수 없습니다!
    pause
    exit /b 1
)

attrib -r -s -h "%TARGET_WIM%" >nul 2>&1

echo.
echo ================================================================================
echo  주입 대상 WIM : %TARGET_WIM%
echo  주입 에이전트 : %AGENT_EXE%
echo ================================================================================
echo.

:: 4. 잔여 마운트 정리
echo [*] 기존 잔여 마운트 지점 정리 중 (dism /Cleanup-Wim)...
dism /Cleanup-Wim >nul 2>&1

set "MOUNT_DIR=C:\MultiDeploy_WinPE_Mount"
if exist "%MOUNT_DIR%" (
    dism /Unmount-Image /MountDir:"%MOUNT_DIR%" /Discard >nul 2>&1
    rd /s /q "%MOUNT_DIR%" >nul 2>&1
)
mkdir "%MOUNT_DIR%" >nul 2>&1

:: 5. 1번 인덱스 마운트 및 주입
set "PROV_EXE=%~dp0bin\agent\CJAutoProvisioner.exe"
set "STARTNET_CMD=%~dp0client\scripts\startnet.cmd"

for /L %%I in (1,1,2) do (
    echo --------------------------------------------------------------------------------
    echo [Index %%I] boot.wim 마운트 시도 중...
    dism /Mount-Image /ImageFile:"%TARGET_WIM%" /Index:%%I /MountDir:"%MOUNT_DIR%"
    if !errorlevel! equ 0 (
        echo [+] [Index %%I] 마운트 성공! 최신 바이너리 전수 주입 중...

        REM 표준 System32 및 루트 주입
        if not exist "%MOUNT_DIR%\Windows\System32" mkdir "%MOUNT_DIR%\Windows\System32" >nul 2>&1
        copy /y "%AGENT_EXE%" "%MOUNT_DIR%\Windows\System32\PeImagingAgent.exe" >nul 2>&1
        copy /y "%AGENT_EXE%" "%MOUNT_DIR%\PeImagingAgent.exe" >nul 2>&1
        if exist "%STARTNET_CMD%" copy /y "%STARTNET_CMD%" "%MOUNT_DIR%\Windows\System32\startnet.cmd" >nul 2>&1

        if exist "%PROV_EXE%" (
            copy /y "%PROV_EXE%" "%MOUNT_DIR%\Windows\System32\CJAutoProvisioner.exe" >nul 2>&1
            copy /y "%PROV_EXE%" "%MOUNT_DIR%\CJAutoProvisioner.exe" >nul 2>&1
        )

        REM 커스텀 WinPE (PEApp, Tools, Program Files 등) 전수 자동 탐색 덮어쓰기
        powershell -NoProfile -Command "Get-ChildItem -Path '%MOUNT_DIR%' -Filter 'PeImagingAgent.exe' -Recurse -File -Force -ErrorAction SilentlyContinue | ForEach-Object { Copy-Item -Path '%AGENT_EXE%' -Destination $_.FullName -Force; Write-Host ('  [WIM 갱신 확인] ' + $_.FullName) }"

        if exist "%PROV_EXE%" (
            powershell -NoProfile -Command "Get-ChildItem -Path '%MOUNT_DIR%' -Filter 'CJAutoProvisioner.exe' -Recurse -File -Force -ErrorAction SilentlyContinue | ForEach-Object { Copy-Item -Path '%PROV_EXE%' -Destination $_.FullName -Force; Write-Host ('  [WIM 갱신 확인] ' + $_.FullName) }"
        )

        echo [*] [Index %%I] 변경 사항 영구 저장(Commit) 및 언마운트 중... (약 15~30초 소요)
        dism /Unmount-Image /MountDir:"%MOUNT_DIR%" /Commit
        if !errorlevel! neq 0 (
            echo [-] Commit 1차 지연, 3초 후 재시도...
            timeout /t 3 /nobreak >nul
            dism /Unmount-Image /MountDir:"%MOUNT_DIR%" /Commit
        )
        echo [+] [Index %%I] boot.wim 최신 에이전트 주입 및 저장 완료!
    ) else (
        echo [*] [Index %%I] 이미지가 없거나 마운트 생략.
    )
)

if exist "%MOUNT_DIR%" rd /s /q "%MOUNT_DIR%" >nul 2>&1

:: 6. WIM 폴더 및 USB 루트도 함께 최신본 동기화
for %%P in ("%TARGET_WIM%") do (
    set "WDIR=%%~dpP"
    set "WDRV=%%~dP"
)
copy /y "%AGENT_EXE%" "!WDIR!\PeImagingAgent.exe" >nul 2>&1
if not exist "!WDIR!\agent" mkdir "!WDIR!\agent" >nul 2>&1
copy /y "%AGENT_EXE%" "!WDIR!\agent\PeImagingAgent.exe" >nul 2>&1
if exist "%PROV_EXE%" (
    copy /y "%PROV_EXE%" "!WDIR!\CJAutoProvisioner.exe" >nul 2>&1
    copy /y "%PROV_EXE%" "!WDIR!\agent\CJAutoProvisioner.exe" >nul 2>&1
)

for %%U in (D E F G H I J K L M N) do (
    if exist "%%U:\agent\PeImagingAgent.exe" copy /y "%AGENT_EXE%" "%%U:\agent\PeImagingAgent.exe" >nul 2>&1
    if exist "%%U:\PeImagingAgent.exe" copy /y "%AGENT_EXE%" "%%U:\PeImagingAgent.exe" >nul 2>&1
)

echo.
echo ================================================================================
echo  [★ 성공] boot.wim 내부 및 관련 폴더에 최신 에이전트 주입이 완료되었습니다!
echo  이제 타깃 PC를 재부팅하시면 [★ WinPE 최신 에이전트 주입 확인 메시지] 배너가
echo  화면에 5초간 멈추며 확실하게 출력됩니다!
echo ================================================================================
echo.
pause

