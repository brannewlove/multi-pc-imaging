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

### 2. Master Management Console (Windows Native Standalone)
- **실행 환경**: Windows 10 / 11 / Server 64-bit (순수 윈도우 네이티브 독립 실행형 앱)
- **기술 스택**: C# (.NET 8.0 Windows Desktop App, WinForms) - **Node.js, Electron, 웹 브라우저 엔진(Chromium), HTTP 웹서버 종속성 0%**
- **실행 바이너리**: [**`bin\MultiDeployMaster.exe`**](file:///d:/Code/multi-pc-imaging/bin/MultiDeployMaster.exe) (외부 종속성 없는 단일 실행파일)
- **핵심 모듈**:
  - `PxeServerCoordinator` (`DhcpPxeService` + `TftpServerService` + `HttpBootServerService` + `PxeBootAssetManager`):
    - **독립 작업대 Standalone DHCP 서버 내장**: 별도 공유기 없이 스위칭 허브만으로 IP 자동 임대(Sticky IP) 및 ProxyDHCP 병행 모드 지원
    - **UEFI 64-bit 전용 iPXE + HTTP(`wimboot`) 초고속 부팅**: TFTP 윈도우 전송으로 `snponly.efi` 로드 후 TCP 39202 HTTP 인메모리(RAM) 캐시 스트리밍으로 다중 PC 동시 초고속 WinPE 부팅 지원
    - **전원 ON 즉시 하드웨어 관제 & 무한 재부팅 방지(Boot Loop Guard)**: iPXE 단계에서 SMBIOS(제조사/모델명/시리얼)를 선제 수집해 그리드에 실시간 부팅 진행률을 표시하고, 배포 완료(`SUCCESS`) PC 재부팅 시 자동으로 내장 SSD 부팅 유도
  - `UdpDiscoveryService`: UDP 39200 브로드캐스트 자동 감지 및 WinPE 에이전트에 마스터 IP 전송
  - `TcpServerService`: TCP 39201 포트 기반 WinPE 에이전트 다중 비동기 세션 제어
  - `SmartPartitionEngine`: 스마트 지능형 동적 파티션 자동 생성 엔진 (GPT 포맷, 512GB 초과/이하 조건 분기, 2개 이상 다중 디스크 최소 용량 OS 배치 및 Data1/Data2 자동 라벨링)
  - `ImageMatcher`: 기종/제조사/모델명/SSD 용량별 WIM 이미지 우선순위 매칭 엔진
  - `Native Interactive DataGridView`: 고성능 네이티브 테이블 관제 그리드
    - **순서 변경 (Drag & Drop Reordering)**: 마우스로 컬럼 헤더를 끌어서 원하는 순서로 즉시 재배치
    - **열 숨김/복원 (Column Visibility)**: 컬럼 헤더 우클릭 메뉴("이 열 숨기기", "모든 열 다시 표시") 및 "⚙ 테이블 열 설정" 체크리스트 모달로 개별 토글
    - **너비 조절 (Column Resizing)**: 헤더 경계선을 드래그하여 자유자재로 너비 조정
    - **헤더 정렬 (Column Sorting)**: 컬럼 헤더를 클릭하여 오름차순(▲)/내림차순(▼) 정렬
    - **설정 영구 보존**: 변경된 순서, 너비, 숨김 상태가 `bin\config\ui_settings.json`에 영구 자동 저장
  - `ConfigManager`: JSON 설정 파일 자동 관리 및 1클릭 전체 설정 백업(Export) / 복원(Import)

---

## 🚀 빠른 시작 가이드

### 1. 마스터 네이티브 앱 원클릭 실행 (권장)
- 루트 폴더의 [**`Run-Native-Master.bat`**](file:///d:/Code/multi-pc-imaging/Run-Native-Master.bat) 또는 [**`Run-Master-App.bat`**](file:///d:/Code/multi-pc-imaging/Run-Master-App.bat) 더블 클릭!
- 실행 바이너리 직접 실행: [**`bin\MultiDeployMaster.exe`**](file:///d:/Code/multi-pc-imaging/bin/MultiDeployMaster.exe)
- 웹페이지나 Node.js 설치 없이 즉시 네이티브 윈도우 창으로 실행됩니다.

### 2. 마스터 네이티브 앱 소스 빌드 (선택 사항)
- 루트 폴더의 [**`Build-Native-Master.bat`**](file:///d:/Code/multi-pc-imaging/Build-Native-Master.bat) 더블 클릭!
- 또는 CLI 명령어로 빌드:
```powershell
dotnet publish MasterApp\MasterApp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o bin
```
- 빌드 결과물: `bin\MultiDeployMaster.exe` (.NET 런타임이 자체 내장된 초고속 단일 포터블 실행파일)

### 3. 내장 독립 작업대 DHCP & UEFI 64-bit 초고속 PXE 부팅 서버 설정
1. 마스터 앱 상단 헤더의 **[🌐 PXE 서버: OFF]** 버튼을 클릭합니다.
2. **바인딩 랜카드(NIC)**에서 작업대 스위칭 허브와 연결된 유선 랜카드(예: 고정 IP `192.168.77.1`)를 선택하고 **[⚡ 대역 자동맞춤]**을 확인합니다.
3. **WinPE `boot.wim` 경로**를 선택합니다. (순정 `boot.wim`인 경우 우측 **[🔧 boot.wim 에이전트 자동 주입]** 버튼으로 1클릭 자동 패치 가능)
4. **[▶ PXE / DHCP 서버 시작]**을 누르면 외부 공유기나 서드파티 툴 없이 즉시 다중 PC 초고속 네트워크 부팅이 시작됩니다.
   - *타깃 PC BIOS 설정*: **UEFI 부팅 활성화** 및 **Secure Boot(보안 부팅) Disabled** 필요

---

### 2. 마스터 이미지 폴더 지정 및 선택 규칙 설정 방법

#### ① 마스터 이미지 저장 폴더 지정
1. 상단 툴바의 **[📀 이미지 선택 규칙]** 버튼 클릭
2. **마스터 로컬 이미지 폴더 경로**: `.wim` / `.esd` 파일들이 위치한 디렉터리 경로 입력 (예: `D:\Images` 또는 `C:\images`)
3. **WinPE 클라이언트 접근 공유 경로**: WinPE에서 접근할 SMB 네트워크 경로 입력 (예: `\\192.168.1.50\images`)
4. **[폴더 저장 & WIM 스캔]** 버튼을 누르면 해당 폴더 내의 WIM 파일 목록과 용량이 실시간 칩 배지로 표시됩니다.

#### ② 기종별 자동 이미지 선택 규칙 (Matching Rules) 구성
- 타깃 PC가 WinPE로 부팅되면 등록된 규칙 목록을 **우선순위(Priority #1, #2, ...) 순서대로 대조**하여 적합한 WIM 이미지를 자동 할당합니다.
- **판별 조건**:
  - **제조사 (Manufacturer)**: 와일드카드(`*`) 및 다중 조건(`|`) 지원 (예: `SAMSUNG*`, `LENOVO*`, `Dell*|Alienware*`)
  - **모델명 패턴 (Model Pattern)**: 정규식/와일드카드 지원 (예: `NT950*|Galaxy*|Book*`, `ThinkPad*|T14*`)
  - **스토리지 용량 범위 (GB)**: 최소/최대 디스크 용량 (예: `200` ~ `2000` GB)
  - **스토리지 인터페이스**: `NVMe`, `SATA`, `ANY`
- **조작 기능**:
  - `▲` / `▼`: 규칙 평가 우선순위 변경
  - `수정` / `삭제`: 매칭 조건 및 배포 WIM 이미지 파일명 수정
  - `+ 신규 규칙`: 새로운 모델에 대한 WIM 규칙 추가
- **기본 대체 규칙 (Fallback Rule)**:
  - 어떤 전용 규칙에도 부합하지 않는 기종이 부팅되었을 때 공용으로 배포할 범용 순정 WIM 이미지(`Win11_23H2_Universal_Clean.wim`)가 최하단 우선순위로 자동 작동합니다.

---

### 3. 다중 PC 시뮬레이션 테스트
```powershell
cd simulator
node simulate_clients.js
```
- 다양한 제조사(삼성 갤럭시북, 레노버 씽크패드, 델 래티튜드, LG 그램 등)가 동시 접속하며 지정된 규칙에 따라 알맞은 WIM 이미지가 자동 배포되는 전 과정을 마스터 네이티브 앱에서 실시간 모니터링할 수 있습니다.

---

### 4. 클라이언트 C++ 에이전트 빌드 (WinPE 용)

#### Visual Studio MSVC 빌드:
```powershell
cd client
mkdir build && cd build
cmake .. -G "Visual Studio 17 2022" -A x64
cmake --build . --config Release
```

#### LLVM-MinGW (GCC/Clang) 정적 링크 빌드:
```powershell
cd client
mkdir build && cd build
cmake .. -G "MinGW Makefiles" -DCMAKE_BUILD_TYPE=Release
cmake --build .
```
- 빌드 결과물: **`client/build/PeImagingAgent.exe`** (외부 런타임 DLL 없이 순수 Windows 기본 API만으로 정적 링크 구동)

