@echo off
setlocal enabledelayedexpansion

title MultiDeploy - boot.wim 최신 에이전트 수동/원클릭 직접 주입 도구
chcp 65001 > nul
set "SCRIPT_DIR=%~dp0"
cd /d "%SCRIPT_DIR%"

echo ================================================================================
echo   [MultiDeploy] WinPE boot.wim 최신 에이전트 직접 주입 도구
echo ================================================================================
echo.

:: 1. 관리자 권한 확인 및 자동 승격 (이미 관리자 권한이면 바로 진행)
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [*] DISM 작업을 위해 관리자 권한으로 승격합니다...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "$p = Start-Process -FilePath 'cmd.exe' -ArgumentList @('/c', 'call', '\"%~f0\"', '\"%~1\"', '\"%~2\"') -Verb RunAs -Wait -PassThru; if ($p) { exit $p.ExitCode } else { exit 1 }"
    exit /b %errorlevel%
)

:: 2. 에이전트 실행 파일 검증
set "AGENT_EXE="
if not "%~2"=="" (
    set "CANDIDATE=%~2"
    set "CANDIDATE=!CANDIDATE:"=!"
    if exist "!CANDIDATE!" set "AGENT_EXE=!CANDIDATE!"
)
if not defined AGENT_EXE if exist "%SCRIPT_DIR%agent\PeImagingAgent.exe" set "AGENT_EXE=%SCRIPT_DIR%agent\PeImagingAgent.exe"
if not defined AGENT_EXE if exist "%SCRIPT_DIR%bin\agent\PeImagingAgent.exe" set "AGENT_EXE=%SCRIPT_DIR%bin\agent\PeImagingAgent.exe"
if not defined AGENT_EXE if exist "%SCRIPT_DIR%..\bin\agent\PeImagingAgent.exe" set "AGENT_EXE=%SCRIPT_DIR%..\bin\agent\PeImagingAgent.exe"
if not defined AGENT_EXE if exist "%SCRIPT_DIR%PeImagingAgent.exe" set "AGENT_EXE=%SCRIPT_DIR%PeImagingAgent.exe"
if not defined AGENT_EXE if exist "%SCRIPT_DIR%client\build\PeImagingAgent.exe" set "AGENT_EXE=%SCRIPT_DIR%client\build\PeImagingAgent.exe"
if not defined AGENT_EXE if exist "%SCRIPT_DIR%client\build\Release\PeImagingAgent.exe" set "AGENT_EXE=%SCRIPT_DIR%client\build\Release\PeImagingAgent.exe"
if not defined AGENT_EXE if exist "%SCRIPT_DIR%..\client\build\PeImagingAgent.exe" set "AGENT_EXE=%SCRIPT_DIR%..\client\build\PeImagingAgent.exe"
if not defined AGENT_EXE if exist "%SCRIPT_DIR%..\client\build\Release\PeImagingAgent.exe" set "AGENT_EXE=%SCRIPT_DIR%..\client\build\Release\PeImagingAgent.exe"
if not defined AGENT_EXE if exist "C:\Users\LeeJH\.gemini\antigravity-ide\scratch\multi-pc-imaging\bin\agent\PeImagingAgent.exe" set "AGENT_EXE=C:\Users\LeeJH\.gemini\antigravity-ide\scratch\multi-pc-imaging\bin\agent\PeImagingAgent.exe"
if not defined AGENT_EXE if exist "C:\Users\LeeJH\.gemini\antigravity-ide\scratch\multi-pc-imaging\client\build\PeImagingAgent.exe" set "AGENT_EXE=C:\Users\LeeJH\.gemini\antigravity-ide\scratch\multi-pc-imaging\client\build\PeImagingAgent.exe"

if not defined AGENT_EXE (
    echo [ERROR] 최신 PeImagingAgent.exe 바이너리를 찾을 수 없습니다
    echo 먼저 client\Build-Client-Agent.bat 을 실행하여 빌드하세요.
    if "%~1"=="" pause
    exit /b 1
)

echo [1단계] 주입할 최신 에이전트 바이너리 확인:
echo   - 경로: %AGENT_EXE%
powershell -NoProfile -Command "$f = Get-Item '%AGENT_EXE%'; Write-Host ('  - 용량: ' + [math]::round($f.Length/1MB, 2) + ' MB / 빌드: ' + $f.LastWriteTime)"
echo.

:: 3. 프로비저너 및 startnet.cmd 탐색
set "PROV_EXE="
if exist "%SCRIPT_DIR%agent\CJAutoProvisioner.exe" set "PROV_EXE=%SCRIPT_DIR%agent\CJAutoProvisioner.exe"
if exist "%SCRIPT_DIR%bin\agent\CJAutoProvisioner.exe" set "PROV_EXE=%SCRIPT_DIR%bin\agent\CJAutoProvisioner.exe"
if exist "%SCRIPT_DIR%..\bin\agent\CJAutoProvisioner.exe" set "PROV_EXE=%SCRIPT_DIR%..\bin\agent\CJAutoProvisioner.exe"
if exist "%SCRIPT_DIR%CJAutoProvisioner.exe" set "PROV_EXE=%SCRIPT_DIR%CJAutoProvisioner.exe"
if exist "%SCRIPT_DIR%tools\CJAutoProvisioner\dist\CJAutoProvisioner.exe" set "PROV_EXE=%SCRIPT_DIR%tools\CJAutoProvisioner\dist\CJAutoProvisioner.exe"
if exist "%SCRIPT_DIR%..\tools\CJAutoProvisioner\dist\CJAutoProvisioner.exe" set "PROV_EXE=%SCRIPT_DIR%..\tools\CJAutoProvisioner\dist\CJAutoProvisioner.exe"
if not defined PROV_EXE if exist "C:\Users\LeeJH\.gemini\antigravity-ide\scratch\multi-pc-imaging\bin\agent\CJAutoProvisioner.exe" set "PROV_EXE=C:\Users\LeeJH\.gemini\antigravity-ide\scratch\multi-pc-imaging\bin\agent\CJAutoProvisioner.exe"

set "STARTNET_CMD="
if exist "%SCRIPT_DIR%agent\startnet.cmd" set "STARTNET_CMD=%SCRIPT_DIR%agent\startnet.cmd"
if exist "%SCRIPT_DIR%bin\agent\startnet.cmd" set "STARTNET_CMD=%SCRIPT_DIR%bin\agent\startnet.cmd"
if exist "%SCRIPT_DIR%..\bin\agent\startnet.cmd" set "STARTNET_CMD=%SCRIPT_DIR%..\bin\agent\startnet.cmd"
if exist "%SCRIPT_DIR%startnet.cmd" set "STARTNET_CMD=%SCRIPT_DIR%startnet.cmd"
if exist "%SCRIPT_DIR%client\scripts\startnet.cmd" set "STARTNET_CMD=%SCRIPT_DIR%client\scripts\startnet.cmd"
if exist "%SCRIPT_DIR%..\client\scripts\startnet.cmd" set "STARTNET_CMD=%SCRIPT_DIR%..\client\scripts\startnet.cmd"
if not defined STARTNET_CMD if exist "C:\Users\LeeJH\.gemini\antigravity-ide\scratch\multi-pc-imaging\bin\agent\startnet.cmd" set "STARTNET_CMD=C:\Users\LeeJH\.gemini\antigravity-ide\scratch\multi-pc-imaging\bin\agent\startnet.cmd"

:: 4. 대상 boot.wim 파일 경로 결정
set "TARGET_WIM=%~1"
if "!TARGET_WIM!"=="" (
    set "DEFAULT_WIM=D:\boot.wim"
    if not exist "!DEFAULT_WIM!" set "DEFAULT_WIM=C:\MasterImage\boot.wim"
    if not exist "!DEFAULT_WIM!" set "DEFAULT_WIM=C:\images\boot.wim"

    echo [2단계] 주입할 대상 boot.wim 파일 경로 확인:
    if exist "!DEFAULT_WIM!" (
        echo 기본 감지된 경로: !DEFAULT_WIM!
        set /p USER_WIM=">> 대상 WIM 경로를 입력하거나 바로 Enter를 누르세요 [!DEFAULT_WIM!]: "
        if "!USER_WIM!"=="" set "TARGET_WIM=!DEFAULT_WIM!"
        if not "!USER_WIM!"=="" set "TARGET_WIM=!USER_WIM!"
    ) else (
        set /p TARGET_WIM=">> 대상 boot.wim 파일의 전체 경로를 입력하세요: "
    )
) else (
    echo [2단계] 인자로 전달된 대상 boot.wim 경로: !TARGET_WIM!
)

set "TARGET_WIM=%TARGET_WIM:"=%"
if not exist "%TARGET_WIM%" (
    echo [ERROR] "%TARGET_WIM%" 파일을 찾을 수 없습니다
    if "%~1"=="" pause
    exit /b 1
)

attrib -r -s -h "%TARGET_WIM%" >nul 2>&1

echo.
echo ================================================================================
echo  주입 대상 WIM : %TARGET_WIM%
echo  주입 에이전트 : %AGENT_EXE%
echo ================================================================================
echo.

:: 5. 잔여 마운트 정리
echo [*] 기존 잔여 마운트 지점 정리 중 [dism /Cleanup-Wim]...
dism /Cleanup-Wim >nul 2>&1

set "MOUNT_DIR=C:\MultiDeploy_WinPE_Mount"
if exist "%MOUNT_DIR%" (
    dism /Unmount-Image /MountDir:"%MOUNT_DIR%" /Discard >nul 2>&1
    rd /s /q "%MOUNT_DIR%" >nul 2>&1
)
mkdir "%MOUNT_DIR%" >nul 2>&1

:: 6. WIM 인덱스별 마운트 및 주입
set PATCH_SUCCESS_COUNT=0

for /L %%I in (1,1,2) do (
    echo --------------------------------------------------------------------------------
    echo [Index %%I] boot.wim 마운트 시도 중...
    dism /Mount-Image /ImageFile:"%TARGET_WIM%" /Index:%%I /MountDir:"%MOUNT_DIR%"
    if !errorlevel! equ 0 (
        echo [+] [Index %%I] 마운트 성공. 최신 바이너리 전수 주입 중...

        if not exist "%MOUNT_DIR%\Windows\System32" mkdir "%MOUNT_DIR%\Windows\System32" >nul 2>&1
        copy /y "%AGENT_EXE%" "%MOUNT_DIR%\Windows\System32\PeImagingAgent.exe" >nul 2>&1
        copy /y "%AGENT_EXE%" "%MOUNT_DIR%\PeImagingAgent.exe" >nul 2>&1
        if exist "%STARTNET_CMD%" copy /y "%STARTNET_CMD%" "%MOUNT_DIR%\Windows\System32\startnet.cmd" >nul 2>&1

        if exist "%PROV_EXE%" (
            copy /y "%PROV_EXE%" "%MOUNT_DIR%\Windows\System32\CJAutoProvisioner.exe" >nul 2>&1
            copy /y "%PROV_EXE%" "%MOUNT_DIR%\CJAutoProvisioner.exe" >nul 2>&1
        )

        if exist "%MOUNT_DIR%\agent" copy /y "%AGENT_EXE%" "%MOUNT_DIR%\agent\PeImagingAgent.exe" >nul 2>&1
        if exist "%MOUNT_DIR%\PEApp" copy /y "%AGENT_EXE%" "%MOUNT_DIR%\PEApp\PeImagingAgent.exe" >nul 2>&1
        if exist "%MOUNT_DIR%\Tools" copy /y "%AGENT_EXE%" "%MOUNT_DIR%\Tools\PeImagingAgent.exe" >nul 2>&1

        if exist "%PROV_EXE%" (
            if exist "%MOUNT_DIR%\agent" copy /y "%PROV_EXE%" "%MOUNT_DIR%\agent\CJAutoProvisioner.exe" >nul 2>&1
            if exist "%MOUNT_DIR%\PEApp" copy /y "%PROV_EXE%" "%MOUNT_DIR%\PEApp\CJAutoProvisioner.exe" >nul 2>&1
            if exist "%MOUNT_DIR%\Tools" copy /y "%PROV_EXE%" "%MOUNT_DIR%\Tools\CJAutoProvisioner.exe" >nul 2>&1
        )

        echo [*] [Index %%I] 변경 사항 영구 저장 [Commit] 및 언마운트 중...
        dism /Unmount-Image /MountDir:"%MOUNT_DIR%" /Commit
        if !errorlevel! neq 0 (
            echo [-] Commit 1차 지연, 3초 후 재시도...
            timeout /t 3 /nobreak >nul 2>&1
            dism /Unmount-Image /MountDir:"%MOUNT_DIR%" /Commit
        )
        if !errorlevel! neq 0 (
            echo [-] Commit 실패, 마운트 취소 [Discard] 처리합니다...
            dism /Unmount-Image /MountDir:"%MOUNT_DIR%" /Discard >nul 2>&1
        ) else (
            set /a PATCH_SUCCESS_COUNT+=1
            echo [+] [Index %%I] boot.wim 최신 에이전트 주입 및 저장 완료.
        )
    ) else (
        echo [*] [Index %%I] 이미지가 없거나 마운트 생략.
    )
)

if exist "%MOUNT_DIR%" rd /s /q "%MOUNT_DIR%" >nul 2>&1

:: 7. WIM 폴더 및 USB 드라이브 최신본 동기화
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

if !PATCH_SUCCESS_COUNT! gtr 0 (
    echo.
    echo ================================================================================
    echo  [★ 성공] boot.wim 내부 및 관련 폴더에 최신 에이전트 주입이 완료되었습니다.
    echo  (총 !PATCH_SUCCESS_COUNT!개 인덱스 갱신 성공)
    echo ================================================================================
    echo.
    if "%~1"=="" (
        pause
    ) else (
        echo [*] 마스터 프로그램 연동 모드: 완료되었습니다.
        timeout /t 2 >nul 2>&1
    )
    exit /b 0
) else (
    echo.
    echo [ERROR] WIM 인덱스 주입에 실패했습니다.
    if "%~1"=="" pause
    exit /b 1
)
