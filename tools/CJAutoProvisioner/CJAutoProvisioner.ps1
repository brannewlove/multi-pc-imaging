# ==============================================================================
# CJ 보안 소프트웨어 설치 자동화 무인 프로비저너 (PowerShell Standalone Version)
# ==============================================================================
[CmdletBinding()]
param(
    [string]$AppPath = "C:\Temp\CJ_Work\AS_2.0\AutoSetup2.0.exe",
    [string]$WindowTitleExact = "CJ 보안 소프트웨어 설치 자동화 v2.0",
    [string]$WindowTitlePartial = "CJ 보안 소프트웨어",
    [string]$ButtonText = "설치 시작",
    [string]$ConfigPath = "C:\Temp\CJ_Work\AS_2.0\setup_config.ini",
    [string]$LogPath = "C:\Temp\CJ_Work\MultiDeploy\provision.log",
    [int]$TimeoutSeconds = 15
)

# ------------------------------------------------------------------------------
# 0. 로깅 함수
# ------------------------------------------------------------------------------
function Write-ProvisionLog {
    param([string]$Message)
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    $line = "[$timestamp] $Message"
    Write-Host $line

    try {
        $logDir = [System.IO.Path]::GetDirectoryName($LogPath)
        if (-not (Test-Path $logDir)) {
            New-Item -ItemType Directory -Force -Path $logDir | Out-Null
        }
        Add-Content -Path $LogPath -Value $line -Encoding UTF8
    } catch {}
}

Write-ProvisionLog "================================================================================"
Write-ProvisionLog "[CJ 무인 프로비저너] PowerShell Auto-Provisioner v1.0.0 시작"
Write-ProvisionLog "================================================================================"

# ------------------------------------------------------------------------------
# 1. Step 1: assigned_info.json 탐색 및 파싱
# ------------------------------------------------------------------------------
Write-ProvisionLog "[Step 1] 할당 정보(assigned_info.json) 확인 중..."
$candidatePaths = @(
    "$PSScriptRoot\assigned_info.json",
    "C:\Temp\CJ_Work\MultiDeploy\assigned_info.json",
    "C:\Temp\CJ_Work\assigned_info.json",
    "C:\Temp\assigned_info.json",
    "C:\Windows\Setup\Scripts\assigned_info.json",
    "C:\Windows\Panther\assigned_info.json"
)

$foundJson = $null
foreach ($p in $candidatePaths) {
    if (Test-Path $p) {
        $foundJson = $p
        break
    }
}

$isEnabled = $true
$assignedId = ""

if ($foundJson) {
    Write-ProvisionLog "[FOUND] 할당 정보 파일 발견: $foundJson"
    try {
        $raw = Get-Content -Path $foundJson -Raw -Encoding UTF8
        if ($raw -match '"(?:enabled|provisionerEnabled|autoSetupEnabled)"\s*:\s*(true|false)') {
            $isEnabled = [bool]::Parse($matches[1])
        }
        if ($raw -match '"(?:assignedId|cjId|userId|cj_id|id)"\s*:\s*"([^"]*)"') {
            $assignedId = $matches[1].Trim()
        }
    } catch {
        Write-ProvisionLog "[WARNING] assigned_info.json 파싱 실패: $_"
    }
} else {
    Write-ProvisionLog "[INFO] assigned_info.json 없음 -> CASE B (공란 모드)로 진행합니다."
}

if (-not $isEnabled) {
    Write-ProvisionLog "[INFO] 무인 프로비저너가 비활성화(enabled=false)되어 있습니다. 작업을 건너뜁니다."
    exit 0
}

$isCaseA = -not [string]::IsNullOrWhiteSpace($assignedId)
if ($isCaseA) {
    Write-ProvisionLog "[CASE A 감지] 정상 발급 모드: 주입된 CJ_ID = '$assignedId'"
} else {
    Write-ProvisionLog "[CASE B 감지] 사전 세팅 모드: CJ_ID 공란 (user_id='', sindoh_enabled=False)"
}

# ------------------------------------------------------------------------------
# 2. Step 2: setup_config.ini 수정
# ------------------------------------------------------------------------------
Write-ProvisionLog "[Step 2] 초기 설정 파일 수정 중... ($ConfigPath)"
if (-not (Test-Path $ConfigPath)) {
    Write-ProvisionLog "[ERROR] 설정 파일이 존재하지 않습니다: $ConfigPath"
} else {
    try {
        # 백업
        Copy-Item -Path $ConfigPath -Destination "$ConfigPath.bak" -Force
        Write-ProvisionLog "[BACKUP] 원본 설정 파일 백업 생성 완료"

        $lines = Get-Content -Path $ConfigPath -Encoding UTF8
        $newLines = @()
        $cleanId = $assignedId.Trim('"', "'", ' ')

        $userIdFound = $false
        $sindohFound = $false

        foreach ($line in $lines) {
            if ($line -match '^\s*user_id\s*=') {
                $userIdFound = $true
                if ($isCaseA) {
                    $newLines += "user_id = $cleanId"
                    Write-ProvisionLog "[CONFIG EDIT] user_id -> '$cleanId'"
                } else {
                    $newLines += "user_id = "
                    Write-ProvisionLog "[CONFIG EDIT] user_id -> 공란"
                }
            } elseif ($line -match '^\s*sindoh_enabled\s*=') {
                $sindohFound = $true
                if (-not $isCaseA) {
                    $newLines += "sindoh_enabled = False"
                    Write-ProvisionLog "[CONFIG EDIT] sindoh_enabled -> False"
                } else {
                    $newLines += $line
                }
            } else {
                $newLines += $line
            }
        }

        if (-not $userIdFound) {
            if ($isCaseA) { $newLines += "user_id = $cleanId" } else { $newLines += "user_id = " }
        }
        if (-not $sindohFound -and -not $isCaseA) {
            $newLines += "sindoh_enabled = False"
        }

        Set-Content -Path $ConfigPath -Value $newLines -Encoding UTF8
        Write-ProvisionLog "[SUCCESS] 설정 파일 저장 완료"
    } catch {
        Write-ProvisionLog "[ERROR] 설정 파일 수정 실패: $_"
    }
}

# ------------------------------------------------------------------------------
# 3. Step 3: 보안 프로그램 프로세스 실행
# ------------------------------------------------------------------------------
Write-ProvisionLog "[Step 3] 보안 프로그램 실행 중... ($AppPath)"
if (-not (Test-Path $AppPath)) {
    Write-ProvisionLog "[ERROR] 대상 실행 파일을 찾을 수 없습니다: $AppPath"
} else {
    try {
        $appDir = [System.IO.Path]::GetDirectoryName($AppPath)
        $proc = Start-Process -FilePath $AppPath -WorkingDirectory $appDir -PassThru
        Write-ProvisionLog "[PROCESS] 프로세스 시작됨 (PID: $($proc.Id))"
    } catch {
        Write-ProvisionLog "[ERROR] 프로세스 실행 실패: $_"
    }
}

# ------------------------------------------------------------------------------
# 4. Step 4: 창 감지 및 [설치 시작] 버튼 자동 클릭 (Win32 P/Invoke)
# ------------------------------------------------------------------------------
Write-ProvisionLog "[Step 4] 창 감지 및 버튼 클릭 대기 중..."

$win32 = @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class Win32Helper {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Auto)]
    public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet=CharSet.Auto)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet=CharSet.Auto)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    public const uint BM_CLICK = 0x00F5;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const int SW_RESTORE = 9;

    public static IntPtr FindWindowByPartial(string partial) {
        IntPtr result = IntPtr.Zero;
        EnumWindows((hWnd, lParam) => {
            if (!IsWindowVisible(hWnd)) return true;
            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            if (sb.ToString().IndexOf(partial, StringComparison.OrdinalIgnoreCase) >= 0) {
                result = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static IntPtr FindButton(IntPtr parentHwnd, string buttonText) {
        IntPtr btn = IntPtr.Zero;
        EnumChildWindows(parentHwnd, (hWnd, lParam) => {
            var sb = new StringBuilder(256);
            GetWindowText(hWnd, sb, sb.Capacity);
            string txt = sb.ToString().Trim();
            if (txt.IndexOf(buttonText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                txt.IndexOf("설치 시작", StringComparison.OrdinalIgnoreCase) >= 0 ||
                txt.IndexOf("시작", StringComparison.OrdinalIgnoreCase) >= 0) {
                btn = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return btn;
    }
}
"@
Add-Type -TypeDefinition $win32 -ErrorAction SilentlyContinue

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$targetHwnd = [IntPtr]::Zero

while ((Get-Date) -lt $deadline) {
    $targetHwnd = [Win32Helper]::FindWindow($null, $WindowTitleExact)
    if ($targetHwnd -eq [IntPtr]::Zero) {
        $targetHwnd = [Win32Helper]::FindWindowByPartial($WindowTitlePartial)
    }
    if ($targetHwnd -ne [IntPtr]::Zero -and [Win32Helper]::IsWindowVisible($targetHwnd)) {
        Write-ProvisionLog "[FOUND] 창 핸들 감지: 0x$($targetHwnd.ToString('X8'))"
        break
    }
    Start-Sleep -Milliseconds 500
}

$clickSuccess = $false
if ($targetHwnd -ne [IntPtr]::Zero) {
    [Win32Helper]::ShowWindow($targetHwnd, [Win32Helper]::SW_RESTORE) | Out-Null
    [Win32Helper]::SetForegroundWindow($targetHwnd) | Out-Null
    Start-Sleep -Milliseconds 400

    $btnDeadline = (Get-Date).AddSeconds(5)
    $btnHwnd = [IntPtr]::Zero

    while ((Get-Date) -lt $btnDeadline) {
        $btnHwnd = [Win32Helper]::FindButton($targetHwnd, $ButtonText)
        if ($btnHwnd -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 300
    }

    if ($btnHwnd -ne [IntPtr]::Zero) {
        Write-ProvisionLog "[BUTTON FOUND] 버튼 컨트롤 감지: 0x$($btnHwnd.ToString('X8'))"
        [Win32Helper]::SendMessage($btnHwnd, [Win32Helper]::BM_CLICK, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        Start-Sleep -Milliseconds 100
        [Win32Helper]::SendMessage($btnHwnd, [Win32Helper]::WM_LBUTTONDOWN, [IntPtr]1, [IntPtr]::Zero) | Out-Null
        [Win32Helper]::SendMessage($btnHwnd, [Win32Helper]::WM_LBUTTONUP, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        $clickSuccess = $true
        Write-ProvisionLog "[SUCCESS] [설치 시작] 버튼 클릭 신호 전송 완료!"
    } else {
        Write-ProvisionLog "[WARNING] 버튼 컨트롤을 찾지 못했습니다."
    }
} else {
    Write-ProvisionLog "[TIMEOUT] $TimeoutSeconds 초 내에 창을 감지하지 못했습니다."
}

# ------------------------------------------------------------------------------
# 5. Step 5: 정상 종료 및 자폭(Self-Delete)
# ------------------------------------------------------------------------------
Write-ProvisionLog "[Step 5] 프로비저닝 종료. 2초 후 자동 정리(Self-Delete)를 수행합니다."
if ($foundJson -and (Test-Path $foundJson)) {
    Remove-Item -Path $foundJson -Force -ErrorAction SilentlyContinue
}

# ps1 파일 자체 삭제 백그라운드 예약
$ps1Path = $MyInvocation.MyCommand.Path
if ($ps1Path -and (Test-Path $ps1Path)) {
    Start-Process -FilePath "cmd.exe" -ArgumentList "/c timeout /t 2 >nul & del /f /q `"$ps1Path`"" -WindowStyle Hidden
}

exit 0

