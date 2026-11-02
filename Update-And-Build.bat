@echo off
chcp 65001 >nul
setlocal
title MultiDeployOS - Update and Build
cd /d "%~dp0"

echo ================================================================
echo  [MultiDeployOS] 최신 코드 동기화 및 마스터 앱 자동 빌드
echo ================================================================
echo.

echo [1/3] 로컬 실행으로 변경된 템플릿 설정 복원 (충돌 방지)...
git checkout -- MasterApp/config/ 2>nul
git restore MasterApp/config/ 2>nul

echo [2/3] GitHub 최신 코드 가져오기 (git pull)...
git pull origin main
if errorlevel 1 (
    echo.
    echo [경고] git pull 중 충돌이 발생했습니다. 
    echo 로컬 임시 변경사항을 초기화하고 최신 서버 상태로 강제 동기화합니다...
    git fetch origin
    git reset --hard origin/main
)

echo.
echo [3/3] 네이티브 마스터 앱 빌드 실행...
call "%~dp0Build-Native-Master.bat"

endlocal
