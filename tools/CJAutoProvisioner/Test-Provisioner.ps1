# ==============================================================================
# CJ 보안 소프트웨어 무인 프로비저너 로컬 직접 테스트 도구 (PowerShell)
# ==============================================================================
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host "  [MultiDeploy] CJ 보안 소프트웨어 무인 프로비저너 로컬 직접 테스트 도구" -ForegroundColor Cyan
Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "이 도구는 배포(WinPE) 전에 현재 PC에서 직접:"
Write-Host "  1) 사번(CJ_ID) 주입 및 setup_config.ini 설정 변경"
Write-Host "  2) AutoSetup2.0.exe 보안 프로그램 자동 실행"
Write-Host "  3) 프로그램 창 감지 및 [설치 시작] 버튼 자동 클릭"
Write-Host "위 전 과정을 즉시 시뮬레이션하고 테스트합니다."
Write-Host ""

$appCandidates = @(
    "C:\Temp\CJ_Work\AS_2.0\AutoSetup2.0.exe",
    "C:\Temp\CJ_Work\AutoSetup2.0.exe",
    "C:\Temp\CJ_Work\AutoSetup 1.17.exe",
    "C:\Temp\CJ_Work\AutoSetup1.16.exe"
)
$appPath = $null
foreach ($c in $appCandidates) {
    if (Test-Path $c) { $appPath = $c; break }
}
if (-not $appPath -and (Test-Path "C:\Temp\CJ_Work")) {
    $matches = Get-ChildItem -Path "C:\Temp\CJ_Work" -Filter "AutoSetup*.exe" -Recurse -File -ErrorAction SilentlyContinue
    if ($matches -and $matches.Count -gt 0) { $appPath = $matches[0].FullName }
}

if (-not $appPath) {
    Write-Host "[경고] 테스트 대상 프로그램(AutoSetup2.0.exe)을 찾을 수 없습니다." -ForegroundColor Red
    Write-Host "C:\Temp\CJ_Work\AS_2.0 폴더에 AutoSetup2.0.exe 파일이 있는지 확인하세요."
    Write-Host ""
    Read-Host "엔터 키를 누르면 종료합니다..."
    exit 1
}

Write-Host "[감지] 대상 보안 프로그램 확인: $appPath" -ForegroundColor Green
Write-Host ""
Write-Host "--------------------------------------------------------------------------------"
Write-Host " [테스트 모드 선택]"
Write-Host "   1. CASE A 테스트 : 사번 정상 발급 모드 (기본값: cjencmaster)"
Write-Host "   2. CASE B 테스트 : 사번 공란 사전세팅 모드 (user_id 공란, sindoh_enabled=False)"
Write-Host "--------------------------------------------------------------------------------"
$choice = Read-Host "선택 번호 (1 또는 2) [기본값: 1]"
if ([string]::IsNullOrWhiteSpace($choice)) { $choice = "1" }

$testId = "cjencmaster"
if ($choice -eq "1") {
    $customId = Read-Host "테스트할 사번 입력 [기본값: cjencmaster]"
    if (-not [string]::IsNullOrWhiteSpace($customId)) { $testId = $customId.Trim() }
    Write-Host " -> CASE A 선택: 사번 '$testId' 주입 모드로 테스트합니다." -ForegroundColor Yellow
} else {
    $testId = ""
    Write-Host " -> CASE B 선택: 사번 공란 모드로 테스트합니다." -ForegroundColor Yellow
}

# 1. assigned_info.json 생성
$cjWork = "C:\Temp\CJ_Work"
$deployDir = "C:\Temp\CJ_Work\MultiDeploy"
if (-not (Test-Path $cjWork)) { New-Item -ItemType Directory -Force -Path $cjWork | Out-Null }
if (-not (Test-Path $deployDir)) { New-Item -ItemType Directory -Force -Path $deployDir | Out-Null }

$jsonObj = @{
    enabled = $true
    assignedId = $testId
}
$jsonStr = $jsonObj | ConvertTo-Json
[System.IO.File]::WriteAllText("$cjWork\assigned_info.json", $jsonStr, [System.Text.Encoding]::UTF8)
[System.IO.File]::WriteAllText("$deployDir\assigned_info.json", $jsonStr, [System.Text.Encoding]::UTF8)
Write-Host ""
Write-Host "[*] 테스트용 assigned_info.json 주입 완료 (assignedId: '$testId')" -ForegroundColor Green

# 2. 실행
Write-Host ""
Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host "[*] 프로비저너 동작을 시작합니다! (화면에 뜨는 창과 버튼 클릭을 관찰하세요)" -ForegroundColor Cyan
Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host ""

$ps1Script = Join-Path $PSScriptRoot "CJAutoProvisioner.ps1"
if (Test-Path $ps1Script) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ps1Script -TimeoutSeconds 20
} else {
    Write-Host "[오류] CJAutoProvisioner.ps1 스크립트를 찾을 수 없습니다: $ps1Script" -ForegroundColor Red
}

Write-Host ""
Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host "[테스트 완료] 결과 요약" -ForegroundColor Cyan
Write-Host "================================================================================" -ForegroundColor Cyan

$iniPath = "C:\Temp\CJ_Work\AS_2.0\setup_config.ini"
if (-not (Test-Path $iniPath)) { $iniPath = "C:\Temp\CJ_Work\setup_config.ini" }
if (Test-Path $iniPath) {
    Write-Host ""
    Write-Host "[설정 파일 $iniPath 내용]" -ForegroundColor Green
    Get-Content $iniPath | ForEach-Object { Write-Host "  $_" }
}

$logPath = "C:\Temp\CJ_Work\provision.log"
if (-not (Test-Path $logPath)) { $logPath = "C:\Temp\CJ_Work\MultiDeploy\provision.log" }
if (Test-Path $logPath) {
    Write-Host ""
    Write-Host "[최근 프로비저닝 로그 $logPath]" -ForegroundColor Green
    Get-Content $logPath -Tail 15 | ForEach-Object { Write-Host "  $_" }
}

Write-Host ""
Read-Host "테스트가 완료되었습니다. 창을 닫으려면 엔터 키를 누르세요..."
