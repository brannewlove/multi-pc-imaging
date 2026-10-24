# WinPE 부트 이미지 제작 및 클라이언트 자동 실행 구성 매뉴얼

본 문서는 PXE 부팅을 통해 타깃 PC가 부팅되었을 때, 작업자의 개입 없이 `PeImagingAgent.exe`가 자동 실행되도록 WinPE(`boot.wim`)를 제작하고 패키징하는 상세 가이드입니다.

---

## 1. 자동 실행 원리 및 핵심 파일

WinPE 환경에서 부팅 시 자동 실행은 **2개의 파일**에 의해 제어됩니다.

| 파일명 | WinPE 내 경로 | 역할 |
|---|---|---|
| **`startnet.cmd`** | `X:\Windows\System32\startnet.cmd` | WinPE 부팅 시 Windows 커널이 1순위로 실행하는 공식 시작 스크립트 |
| **`PeImagingAgent.exe`** | `X:\Windows\System32\PeImagingAgent.exe` | C++ 네이티브로 빌드된 하드웨어 수집 및 WIM 더빙 자동화 에이전트 |

### 부팅 실행 순서 (Boot Sequence)
1. **타깃 PC 전원 ON** ➔ LAN PXE 부팅 선택
2. **PXE 서버로부터 `boot.wim` 다운로드** ➔ 메모리(RAMDISK `X:\`)에 로드
3. **`startnet.cmd` 자동 호출**:
   - `wpeinit` 명령으로 네트워크 카드 드라이버 초기화 및 DHCP로부터 IP 자동 할당
   - 네트워크 연결 안정화 대기 (2~3초)
4. **`PeImagingAgent.exe` 자동 실행**:
   - SMBIOS, MAC, 디스크(SSD/NVMe) 스펙 스캔
   - UDP 브로드캐스트로 마스터 PC 자동 탐색 및 소켓 연결
   - 마스터 승인 수신 후 디스크 파티션 + WIM 더빙 + 부트로더 생성
5. 작업 완료 후 `wpeutil reboot` 명령으로 **PC 자동 재부팅**

---

## 2. 기존 WinPE (`boot.wim`)에 적용하는 방법 (DISM)

이미 가지고 계신 WinPE 부트 이미지(`boot.wim`)가 있는 경우, DISM 명령어를 사용하여 몇 초 만에 적용할 수 있습니다.

> [!NOTE]
> 아래 작업은 관리자 권한으로 실행된 명령 프롬프트(CMD) 또는 PowerShell에서 진행해야 합니다.

### 단계 1: 마운트 디렉터리 준비 및 마운트
```cmd
:: 임시 마운트 디렉터리 생성
mkdir C:\winpe_mount

:: boot.wim 마운트 (경로는 실제 파일 경로로 수정)
dism /Mount-Wim /WimFile:"D:\PXE\boot.wim" /Index:1 /MountDir:"C:\winpe_mount"
```

### 단계 2: 에이전트 및 시동 스크립트 복사
프로젝트의 빌드된 실행 파일과 스크립트를 마운트된 디렉터리의 `System32`에 복사합니다.

```cmd
:: 1. C++ 네이티브 에이전트 복사
copy "C:\Users\LeeJH\.gemini\antigravity-ide\scratch\multi-pc-imaging\client\build\Release\PeImagingAgent.exe" "C:\winpe_mount\Windows\System32\"

:: 2. 자동 실행 startnet.cmd 복사 (기존 파일 덮어쓰기)
copy "C:\Users\LeeJH\.gemini\antigravity-ide\scratch\multi-pc-imaging\client\scripts\startnet.cmd" "C:\winpe_mount\Windows\System32\"
```

### 단계 3 (옵션): 최신 유선 네트워크 드라이버 주입
다양한 기종(인텔 12~14세대, Realtek 2.5GbE 등)의 PC에서 네트워크가 정상 작동하도록 LAN 드라이버(.inf)를 주입할 수 있습니다.
```cmd
:: 드라이버 폴더 일괄 주입
dism /Image:"C:\winpe_mount" /Add-Driver /Driver:"D:\Drivers\LAN" /Recurse
```

### 단계 4: 변경 사항 저장 및 언마운트
```cmd
dism /Unmount-Wim /MountDir:"C:\winpe_mount" /Commit
```
이제 완성된 `boot.wim`을 PXE 서버(TFTP 폴더)에 교체하여 배치하시면 됩니다.

---

## 3. Windows ADK를 이용한 순정 WinPE 신규 제작 방법

WinPE 이미지가 없는 경우, Microsoft 공식 Windows ADK를 사용하여 백지 상태에서 새로 제작할 수 있습니다.

### 준비물
1. **Windows ADK (Assessment and Deployment Kit)** 설치
2. **Windows PE 추가 기능(WinPE Add-on)** 설치

### 순정 WinPE 빌드 단계
1. **"배포 및 이미징 도구 환경"**을 관리자 권한으로 실행합니다.
2. WinPE 작업 공간 복사:
   ```cmd
   copype amd64 C:\WinPE_amd64
   ```
3. WinPE 이미지 마운트:
   ```cmd
   dism /Mount-Image /ImageFile:"C:\WinPE_amd64\media\sources\boot.wim" /index:1 /MountDir:"C:\WinPE_amd64\mount"
   ```
4. 필수 패키지 추가 (WMI, 스크립팅, DISM API 지원):
   ```cmd
   dism /Add-Package /Image:"C:\WinPE_amd64\mount" /PackagePath:"C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit\Windows Preinstallation Environment\amd64\WinPE_OCs\WinPE-WMI.cab"
   dism /Add-Package /Image:"C:\WinPE_amd64\mount" /PackagePath:"C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit\Windows Preinstallation Environment\amd64\WinPE_OCs\WinPE-Scripting.cab"
   dism /Add-Package /Image:"C:\WinPE_amd64\mount" /PackagePath:"C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit\Windows Preinstallation Environment\amd64\WinPE_OCs\WinPE-StorageWMI.cab"
   ```
5. 에이전트 및 `startnet.cmd` 복사:
   ```cmd
   copy "PeImagingAgent.exe" "C:\WinPE_amd64\mount\Windows\System32\"
   copy "startnet.cmd"       "C:\WinPE_amd64\mount\Windows\System32\"
   ```
6. 저장 및 언마운트:
   ```cmd
   dism /Unmount-Image /MountDir:"C:\WinPE_amd64\mount" /Commit
   ```
7. PXE 배포용 ISO 또는 `boot.wim` 추출:
   - **PXE 부팅용**: `C:\WinPE_amd64\media\sources\boot.wim` 파일 사용
   - **USB/CD 테스트용 ISO 생성**:
     ```cmd
     MakeWinPEMedia /ISO C:\WinPE_amd64 C:\WinPE_MultiDeploy.iso
     ```

---

## 4. `startnet.cmd` 스크립트 상세 설명

현재 적용되어 있는 [`client/scripts/startnet.cmd`](file:///C:/Users/LeeJH/.gemini/antigravity-ide/scratch/multi-pc-imaging/client/scripts/startnet.cmd)의 내용과 동작 원리입니다.

```cmd
@echo off
title WinPE Multi-PC Imaging Client Initializer
echo ========================================================
echo   Initializing WinPE Network and Environment...
echo ========================================================

:: 1. WinPE 기본 네트워크 스택 및 DHCP 초기화 (필수)
wpeinit

:: 2. 네트워크 스위치 및 DHCP로부터 IP를 할당받을 수 있도록 3초 대기
ping 127.0.0.1 -n 3 > nul

:: 3. 네트워크 IP 상태 확인 및 콘솔 출력
ipconfig | findstr /i "IPv4"

:: 4. 다중 PC 이미지 더빙 에이전트 자동 실행
echo Starting PeImagingAgent.exe...
X:\Windows\System32\PeImagingAgent.exe

:: 5. 에이전트 종료 시(더빙 완료) 시스템 자동 재부팅
echo Imaging process ended. Rebooting system in 5 seconds...
wpeutil reboot
```

---

## 5. 트러블슈팅

### Q1. PE 부팅 후 마스터 서버를 찾지 못합니다.
- **원인**: 네트워크 카드(NIC) 드라이버가 인식되지 않았거나, 공유기/스위치에서 DHCP IP 할당이 지연되는 경우입니다.
- **해결책**:
  - `startnet.cmd`에서 `ping 127.0.0.1 -n 5 > nul`로 대기 시간을 5초로 늘려줍니다.
  - 최신 칩셋의 경우 DISM으로 해당 기종의 유선 LAN 드라이버(`.inf`)를 `boot.wim`에 주입합니다.

### Q2. 수동으로 마스터 IP를 지정하고 싶습니다.
- `PeImagingAgent.exe`는 뒤에 인자로 마스터 IP를 넘길 수 있습니다.
- `startnet.cmd`에서 아래와 같이 지정 가능합니다:
  ```cmd
  X:\Windows\System32\PeImagingAgent.exe 192.168.1.10
  ```
