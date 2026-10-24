# Multi-PC Image Deployment System (다중 PC 이미지 더빙 자동화 시스템)

## 프로젝트 개요
내부 네트워크 환경에서 PXE 부팅된 여러 대의 대상 PC(WinPE 환경)를 자동으로 인식하고,
하드웨어 스펙(모델명, 시리얼, MAC, 디스크 구성)을 수집하여 마스터 관리 프로그램으로 전송합니다.
마스터 프로그램에서는 사전 정의된 정책(HDD 용량/수량/모델)에 따라 적합한 파티션 및 OS 이미지를 자동/수동 승인하여 초고속으로 이미지를 더빙(배포)하고 진행률과 성공/실패 결과를 실시간 추적하는 자동화 솔루션입니다.

---

## 📚 상세 매뉴얼 및 가이드 문서 바로가기
- 📖 [**WinPE 부트 이미지 제작 및 자동 실행 구성 매뉴얼**](file:///C:/Users/LeeJH/.gemini/antigravity-ide/scratch/multi-pc-imaging/docs/winpe_packaging_guide.md): `boot.wim` 마운트/패키징, LAN 드라이버 주입, `startnet.cmd` 자동 시동 구성법
- 🏛️ [**시스템 아키텍처 및 통신 프로토콜 명세서**](file:///C:/Users/LeeJH/.gemini/antigravity-ide/scratch/multi-pc-imaging/docs/architecture.md): 시퀀스 다이어그램 및 패킷 명세
- ⚙️ [**파티션 권장양식 및 자동매칭 규칙 설정**](file:///C:/Users/LeeJH/.gemini/antigravity-ide/scratch/multi-pc-imaging/master/config/partition_policies.json): HDD 용량, 개수, 번호별 자동 매칭 룰

---

## ⚡ WinPE 부팅 후 자동 실행 구조

WinPE로 부팅 시 작업자의 개입 없이 아래 **2개 파일**이 연동되어 자동으로 더빙을 수행합니다.

| 파일명 | 저장 위치 | 역할 |
|---|---|---|
| **`startnet.cmd`** | `X:\Windows\System32\startnet.cmd` | WinPE 부팅 시 커널이 1순위로 실행하는 공식 시작 스크립트 (네트워크 초기화 + 에이전트 실행) |
| **`PeImagingAgent.exe`** | `X:\Windows\System32\PeImagingAgent.exe` | C++ 네이티브 에이전트 바이너리 (HW 스펙 수집 ➔ 마스터 자동 연결 ➔ WIM 더빙 ➔ 재부팅) |

### 🛠️ 기존 `boot.wim`에 적용하는 초간단 3단계
```cmd
:: 1. boot.wim 마운트
dism /Mount-Wim /WimFile:"D:\PXE\boot.wim" /Index:1 /MountDir:"C:\mount"

:: 2. 에이전트와 자동실행 startnet.cmd 복사
copy "client\build\Release\PeImagingAgent.exe" "C:\mount\Windows\System32\"
copy "client\scripts\startnet.cmd"             "C:\mount\Windows\System32\"

:: 3. 저장 후 언마운트
dism /Unmount-Wim /MountDir:"C:\mount" /Commit
```

---

## 시스템 구성요소

### 1. Client Agent (WinPE Native)
- **실행 환경**: WinPE 10 / 11 (64-bit / 32-bit 호환)
- **개발 언어**: C++ (MSVC `/MT` 정적 링크 - 외부 런타임 DLL 종속성 0%)
- **핵심 모듈**:
  - `HardwareScanner`: SMBIOS(모델명, 시리얼, UUID), NIC(MAC, IP), 디스크 상세(NVMe/SATA, 용량, 현재 파티션) 수집
  - `NetworkClient`: 마스터 서버 자동 탐색(UDP 브로드캐스트) 및 TCP/WebSocket 연결
  - `DiskPartitionEngine`: diskpart 스크립트 생성/실행을 통한 무인 파티셔닝 (GPT/MBR)
  - `ImageDeployEngine`: DISM /Apply-Image를 통한 초고속 WIM 복원 및 UEFI 부트로더(BCDBoot) 자동 구성
  - `ProgressReporter`: 작업 단계(파티션, WIM 적용) 및 실시간 속도/진행률/오류 마스터 전송

### 2. Master Server & Management Console
- **실행 환경**: Windows 10 / 11 / Server
- **아키텍처**: 마스터 엔진(`server.js`) + 반응형 글래스모피즘 웹 관제 대시보드 (`http://localhost:39202`)
- **핵심 모듈**:
  - `ClientSessionManager`: WinPE 클라이언트 실시간 연결/세션 상태 관리
  - `ProfileMatchingEngine`: 하드웨어 스펙 기반 파티션/이미지 자동 매칭 규칙 엔진
    - HDD 수량/용량(GB)/모델명 정규식 매칭
    - 조건 부합 시 자동 승인(Auto-Deploy) 또는 관리자 수동 승인
  - `ImageRepository`: 모델별 OS 이미지(WIM) 카탈로그
  - `MonitoringDashboard`: 전체 클라이언트 카드뷰, 실시간 진행률 게이지, 배포 이력(History) 자동 기록

---

## 🚀 빠른 시작 가이드

### 1. 마스터 관리 서버 실행
```powershell
cd master
node server.js
```
- 웹 대시보드 접속: **`http://localhost:39202`**

### 2. 다중 PC 시뮬레이션 테스트
```powershell
cd simulator
node simulate_clients.js
```

### 3. 클라이언트 C++ 에이전트 빌드 (Visual Studio)
```powershell
cd client
mkdir build && cd build
cmake .. -G "Visual Studio 17 2022" -A x64
cmake --build . --config Release
```
