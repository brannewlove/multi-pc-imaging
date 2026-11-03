# CJ 보안 소프트웨어 무인 프로비저너 (Post-OOBE Auto-Provisioner)

Windows OS 배포(WIM 이미징) 완료 후, 시스템 첫 부팅 시 사내 보안 프로그램을 자동으로 설정하고 [설치 시작] 버튼을 자동 클릭하여 완주시키는 무인 프로비저닝 도구입니다.

---

## 1. 주요 기능

1. **ID 유무에 따른 INI 설정 파일 자동 분기 (`setup_config.ini`)**:
   - `assigned_info.json`의 `assignedId` 유무를 자동 감지
   - **[CASE A: 정상 발급 모드]**: `user_id = {CJ_ID}` (실제 파일 양식에 맞춰 따옴표 없이 저장)
   - **[CASE B: 사전 세팅 모드]**: `user_id = ` (공란 유지), `sindoh_enabled = False`
   - 기존 파일 주석, 빈 줄, 인코딩을 100% 보존하는 정규식 라인 치환
   - 수정 전 원본 백업(`setup_config.ini.bak`) 자동 생성

2. **무인 프로비저너 활성화/비활성화 스위치 지원**:
   - `assigned_info.json`에 `"enabled": false` 설정 시 작업을 건너뛰고 자폭 종료
   - 기본값: 활성 (`enabled: true`)

3. **보안 프로그램 자동 실행 및 버튼 자동 클릭**:
   - 실행 대상: `C:\Temp\CJ_Work\AS_2.0\AutoSetup2.0.exe`
   - 타이틀바 `"CJ 보안 소프트웨어 설치 자동화 v2.0"` 창을 최대 15초간 감지
   - 창이 열리면 화면 전면 활성화 후 `"설치 시작"` 버튼에 Win32 `BM_CLICK` 및 마우스 클릭 이벤트 전송

4. **실행 완료 후 자폭(Self-Delete) 및 안전 로깅**:
   - 로그 파일: `C:\Temp\CJ_Work\MultiDeploy\provision.log` 에 실시간 타임스탬프 기록
   - 클릭 완료 후 백그라운드 프로세스를 통해 `CJAutoProvisioner.exe` 및 `assigned_info.json` 자동 삭제

---

## 2. 파일 구성

| 파일명 | 설명 |
| :--- | :--- |
| **`Program.cs`** | C# 전체 소스 코드 (외부 종속성 0%, .NET 8 및 .NET Framework 4.8 동시 호환) |
| **`CJAutoProvisioner.csproj`** | .NET 8 프로젝트 파일 |
| **`app.manifest`** | UAC 관리자 권한 승격 매니페스트 |
| **`Build-Provisioner.bat`** | 클릭 한 번으로 실행 파일을 빌드하는 배치 스크립트 |
| **`CJAutoProvisioner.ps1`** | 동일 로직을 수행하는 독립형 PowerShell 버전 |
| **`SetupComplete.cmd`** | Windows OOBE 완료 직후 자동 실행 등록 템플릿 |
| **`Register-RunOnce.cmd`** | 첫 사용자 로그인 시점 대화형 UI 실행 등록 스크립트 |

---

## 3. 빌드 방법

### 방법 A: 배치 파일 실행 (가장 간편)
`tools\CJAutoProvisioner\Build-Provisioner.bat` 을 더블 클릭하여 실행합니다.
- Windows 기본 `csc.exe` 컴파일러를 통해 **외부 런타임이 필요 없는 초경량 18KB 단일 파일 (`dist\CJAutoProvisioner.exe`)**이 즉시 생성됩니다.
- .NET 8 SDK가 설치된 경우 `dist\CJAutoProvisioner_Net8.exe`도 함께 빌드됩니다.

### 방법 B: CLI 명령어로 직접 빌드
```cmd
# 1. Windows 내장 컴파일러 (외부 런타임 0%, 초경량 18KB)
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /platform:anycpu /optimize+ /win32manifest:app.manifest /out:dist\CJAutoProvisioner.exe Program.cs

# 2. .NET 8 SDK 단일 파일 빌드
dotnet publish CJAutoProvisioner.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist
```

---

## 4. Windows 첫 부팅 시 무인 실행 연동 방법

### 방식 1: `SetupComplete.cmd` 연동 (권장)
WIM 이미징 배포 단계에서 타깃 OS 드라이브의 스크립트 폴더에 배치합니다:
1. `tools\CJAutoProvisioner\dist\CJAutoProvisioner.exe` 파일을 `C:\Temp\CJ_Work\MultiDeploy\CJAutoProvisioner.exe` 위치에 복사합니다.
2. `tools\CJAutoProvisioner\SetupComplete.cmd` 파일을 `C:\Windows\Setup\Scripts\SetupComplete.cmd` 에 배치합니다.
3. Windows 설치 완료(OOBE 종료) 직후 시스템 계정으로 자동 실행됩니다.

### 방식 2: `RunOnce` 레지스트리 연동 (바탕화면 대화형 실행 권장)
사용자가 윈도우에 첫 로그인하는 시점에 화면 창이 뜨고 버튼이 클릭되는 과정을 사용자가 육안으로 확인해야 하는 경우:
```cmd
reg add "HKLM\Software\Microsoft\Windows\CurrentVersion\RunOnce" /v "CJAutoProvisioner" /t REG_SZ /d "C:\Temp\CJ_Work\MultiDeploy\CJAutoProvisioner.exe" /f
```
WinPE 단계에서는 오프라인 레지스트리 하이브 로드 후 등록 가능:
```cmd
reg load HKLM\OFFLINE_SOFTWARE W:\Windows\System32\config\SOFTWARE
reg add "HKLM\OFFLINE_SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" /v "CJAutoProvisioner" /t REG_SZ /d "C:\Temp\CJ_Work\MultiDeploy\CJAutoProvisioner.exe" /f
reg unload HKLM\OFFLINE_SOFTWARE
```

---

## 5. `assigned_info.json` 포맷 예시

### [CASE A: 정상 발급 모드]
```json
{
  "enabled": true,
  "assignedId": "cjencmaster"
}
```
결과: `setup_config.ini`의 `user_id = cjencmaster` 로 치환

### [CASE B: 사전 세팅 모드]
```json
{
  "enabled": true,
  "assignedId": ""
}
```
(또는 `assigned_info.json` 파일이 없는 경우)
결과: `setup_config.ini`의 `user_id = `, `sindoh_enabled = False` 로 치환

### [프로비저너 비활성화 모드]
```json
{
  "enabled": false
}
```
결과: 아무 작업도 하지 않고 프로비저너가 즉시 자폭 삭제 후 종료
