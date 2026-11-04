#include <iostream>
#include <fstream>
#include <string>
#include <vector>
#include <thread>
#include <chrono>
#include <windows.h>
#include "hw_scanner.h"
#include "net_client.h"
#include "partition_engine.h"
#include "deploy_engine.h"
#include "simple_json.h"

// 간결한 JSON 필드 추출 유틸리티 (외부 라이브러리 종속성 없음)
static std::string ExtractJsonString(const std::string& json, const std::string& key) {
    std::string searchKey = "\"" + key + "\"";
    size_t pos = json.find(searchKey);
    if (pos == std::string::npos) return "";
    pos += searchKey.length();
    while (pos < json.length() && (json[pos] == ' ' || json[pos] == ':' || json[pos] == '\t' || json[pos] == '\r' || json[pos] == '\n')) pos++;
    if (pos < json.length() && json[pos] == '"') {
        pos++;
        size_t endPos = pos;
        while (endPos < json.length()) {
            if (json[endPos] == '"') {
                // 이스케이프된 따옴표(\")인지 확인하기 위해 선행 역슬래시 개수 계산
                size_t bsCount = 0;
                size_t k = endPos;
                while (k > pos && json[k - 1] == '\\') {
                    bsCount++;
                    k--;
                }
                if (bsCount % 2 == 0) {
                    // 짝수 개 역슬래시 (0, 2, 4...) -> 따옴표는 이스케이프되지 않은 닫는 따옴표
                    break;
                }
            }
            endPos++;
        }
        if (endPos < json.length()) {
            return SimpleJson::UnescapeString(json.substr(pos, endPos - pos));
        }
    }
    return "";
}

// WinPE 환경에서 USB나 로컬 디스크에 보관된 WIM 파일 자동 탐색 (와일드카드 및 최신 파일 자동 선택 지원)
static std::string FindLocalWimPath(const std::string& imageName) {
    if (imageName.empty()) return "";
    
    // WinPE에서 마운트 가능한 드라이브 문자(D~Z, C)
    const char driveLetters[] = "DEFGHIJKLMNOPQRSTUVWXYZC";
    const std::vector<std::string> subDirs = {
        "images\\",
        "Images\\",
        "sources\\",
        "Sources\\",
        ""
    };

    // 1단계: 와일드카드가 없는 경우 정확한 파일명이 존재하는지 1차 확인
    if (imageName.find('*') == std::string::npos && imageName.find('?') == std::string::npos) {
        for (int i = 0; driveLetters[i] != '\0'; ++i) {
            char drive = driveLetters[i];
            for (const auto& sub : subDirs) {
                std::string candidate = std::string(1, drive) + ":\\" + sub + imageName;
                DWORD attr = GetFileAttributesA(candidate.c_str());
                if (attr != INVALID_FILE_ATTRIBUTES && !(attr & FILE_ATTRIBUTE_DIRECTORY)) {
                    return candidate;
                }
            }
        }
    }

    // 2단계: 와일드카드 검색 또는 최신 파일 자동 선택 (FindFirstFileA)
    std::string pattern = imageName;
    if (pattern.find('*') == std::string::npos && pattern.find('?') == std::string::npos) {
        // 확장자가 .wim인 경우 모델명 기반 검색 패턴 생성 (예: NT950_202610.wim -> NT950*.wim)
        size_t underscore = pattern.find('_');
        if (underscore != std::string::npos) {
            pattern = pattern.substr(0, underscore + 1) + "*.wim";
        } else {
            pattern = "*.wim";
        }
    }

    FILETIME bestTime = { 0, 0 };
    std::string bestCandidate = "";

    for (int i = 0; driveLetters[i] != '\0'; ++i) {
        char drive = driveLetters[i];
        for (const auto& sub : subDirs) {
            std::string dirPath = std::string(1, drive) + ":\\" + sub;
            std::string query = dirPath + pattern;

            WIN32_FIND_DATAA ffd;
            HANDLE hFind = FindFirstFileA(query.c_str(), &ffd);
            if (hFind != INVALID_HANDLE_VALUE) {
                do {
                    if (!(ffd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) {
                        if (CompareFileTime(&ffd.ftLastWriteTime, &bestTime) > 0) {
                            bestTime = ffd.ftLastWriteTime;
                            bestCandidate = dirPath + ffd.cFileName;
                        }
                    }
                } while (FindNextFileA(hFind, &ffd));
                FindClose(hFind);
            }
        }
    }

    if (!bestCandidate.empty()) {
        std::cout << "[+] USB/로컬 스토리지에서 최신 WIM 파일 매칭: " << bestCandidate << "\n";
        return bestCandidate;
    }

    return "";
}

static int ExtractJsonInt(const std::string& json, const std::string& key, int defaultVal = 1) {
    std::string searchKey = "\"" + key + "\"";
    size_t pos = json.find(searchKey);
    if (pos == std::string::npos) return defaultVal;
    pos += searchKey.length();
    while (pos < json.length() && (json[pos] == ' ' || json[pos] == ':' || json[pos] == '\t' || json[pos] == '\r' || json[pos] == '\n')) pos++;
    size_t endPos = pos;
    while (endPos < json.length() && (isdigit(json[endPos]) || json[endPos] == '-')) endPos++;
    if (endPos > pos) {
        try {
            return std::stoi(json.substr(pos, endPos - pos));
        } catch (...) {}
    }
    return defaultVal;
}

static bool ExtractJsonBool(const std::string& json, const std::string& key, bool defaultVal = false) {
    std::string searchKey = "\"" + key + "\"";
    size_t pos = json.find(searchKey);
    if (pos == std::string::npos) return defaultVal;
    pos += searchKey.length();
    while (pos < json.length() && (json[pos] == ' ' || json[pos] == ':' || json[pos] == '\t')) pos++;
    if (json.compare(pos, 4, "true") == 0) return true;
    if (json.compare(pos, 5, "false") == 0) return false;
    return defaultVal;
}

static std::vector<std::string> ExtractPartitionCommands(const std::string& json) {
    std::vector<std::string> cmds;
    std::string key = "\"partitionCommands\"";
    size_t pos = json.find(key);
    if (pos == std::string::npos) return cmds;
    size_t startArr = json.find('[', pos);
    size_t endArr = json.find(']', startArr);
    if (startArr == std::string::npos || endArr == std::string::npos) return cmds;

    size_t curr = startArr + 1;
    while (curr < endArr) {
        size_t q1 = json.find('"', curr);
        if (q1 == std::string::npos || q1 >= endArr) break;
        size_t q2 = q1 + 1;
        while (q2 < endArr) {
            if (json[q2] == '"' && json[q2 - 1] != '\\') {
                break;
            }
            q2++;
        }
        if (q2 >= endArr) break;
        cmds.push_back(SimpleJson::UnescapeString(json.substr(q1 + 1, q2 - q1 - 1)));
        curr = q2 + 1;
    }
    return cmds;
}

// 방금 WIM 이미지가 더빙되어 윈도우가 설치된 실제 대상 드라이브를 100% 자동 감지
static std::string DetectInstalledWindowsDrive(const std::string& preferredDrive = "W:") {
    std::string candidate = preferredDrive;
    while (!candidate.empty() && (candidate.back() == '\\' || candidate.back() == '/')) {
        candidate.pop_back();
    }
    if (candidate.length() == 1) candidate += ":";

    // 1단계: 지정된 우선 대상 드라이브(W:)에 실제 윈도우 핵심 파일이 존재하는지 1차 확인
    if (!candidate.empty()) {
        std::string testKernel = candidate + "\\Windows\\System32\\ntoskrnl.exe";
        std::string testCmd = candidate + "\\Windows\\System32\\cmd.exe";
        if (GetFileAttributesA(testKernel.c_str()) != INVALID_FILE_ATTRIBUTES ||
            GetFileAttributesA(testCmd.c_str()) != INVALID_FILE_ATTRIBUTES) {
            std::cout << "[+] 지정된 타깃 드라이브(" << candidate << ")에서 정상 설치된 Windows 확인 완료\n";
            return candidate;
        }
    }

    // 2단계: 드라이브 문자가 W:가 아니거나 변경되었을 경우 전체 드라이브 자동 스캔 (WinPE X: 제외)
    std::cout << "[*] 윈도우 설치 드라이브 자동 스캔 중 (C~Z, WinPE X: 제외)...\n";
    const char drives[] = "WTCDEFGHIJKLMNOPQRSUVYZ";
    std::string bestDrive = "";
    FILETIME newestTime = { 0, 0 };

    for (int i = 0; drives[i] != '\0'; ++i) {
        char d = drives[i];
        if (d == 'X' || d == 'x') continue; // WinPE RAM 디스크 제외

        std::string driveRoot = std::string(1, d) + ":";
        std::string kernelPath = driveRoot + "\\Windows\\System32\\ntoskrnl.exe";
        std::string cmdPath = driveRoot + "\\Windows\\System32\\cmd.exe";

        DWORD attr = GetFileAttributesA(kernelPath.c_str());
        if (attr == INVALID_FILE_ATTRIBUTES) {
            attr = GetFileAttributesA(cmdPath.c_str());
        }

        if (attr != INVALID_FILE_ATTRIBUTES && !(attr & FILE_ATTRIBUTE_DIRECTORY)) {
            // 방금 WIM이 풀린 가장 최신 파티션을 우선 감지
            WIN32_FILE_ATTRIBUTE_DATA fileInfo;
            if (GetFileAttributesExA(kernelPath.c_str(), GetFileExInfoStandard, &fileInfo)) {
                if (bestDrive.empty() || CompareFileTime(&fileInfo.ftLastWriteTime, &newestTime) > 0) {
                    newestTime = fileInfo.ftLastWriteTime;
                    bestDrive = driveRoot;
                }
            } else if (bestDrive.empty()) {
                bestDrive = driveRoot;
            }
        }
    }

    if (!bestDrive.empty()) {
        std::cout << "[+] 실제로 Windows가 설치된 타깃 드라이브 자동 감지 성공: " << bestDrive << "\n";
        return bestDrive;
    }

    return candidate.empty() ? "W:" : candidate;
}

void PrintBanner() {
    std::cout << "=========================================================\n";
    std::cout << "    Multi-PC Automated Imaging Client Agent (WinPE)      \n";
    std::cout << "    Version: 1.0.0 (Native C++ / Zero-Dependency)        \n";
    std::cout << "=========================================================\n\n";
}

int main(int argc, char* argv[]) {
    // 1. 콘솔 코드페이지를 UTF-8(65001)로 설정하여 한글 및 유니코드 깨짐 방지
    SetConsoleOutputCP(CP_UTF8);
    SetConsoleCP(CP_UTF8);

    PrintBanner();

    // 1. 하드웨어 인벤토리 수집
    std::cout << "[*] 시스템 사양 스캔 중... (Scanning Hardware)...\n";
    HardwareScanner::FullInventory inv = HardwareScanner::ScanHardwareInventory();

    std::cout << "  - 제조사 (Manufacturer): " << inv.system.manufacturer << "\n";
    std::cout << "  - 모델명 (Model):        " << inv.system.modelName << "\n";
    std::cout << "  - 시리얼 (Serial No):     " << inv.system.serialNumber << "\n";
    std::cout << "  - MAC 주소 (MAC Addr):   " << inv.network.primaryMac << "\n";
    std::cout << "  - IP 주소 (IP Addr):     " << inv.network.ipAddress << "\n";
    std::cout << "  - 디스크 수량 (Disks):     " << inv.disks.size() << "개 (unit)\n";
    for (const auto& d : inv.disks) {
        std::cout << "    [Disk " << d.diskIndex << "] " << d.friendlyName 
                  << " (" << d.busType << ", " << (int)d.sizeGb << " GB, " 
                  << d.partitionStyle << ")\n";
    }
    std::cout << "\n";

    bool deployedSuccessfully = false;

    // 마스터 프로그램 종료 시에도 에이전트가 종료되지 않고 대기 모드로 재연결을 시도하는 외부 루프
    while (!deployedSuccessfully) {
        // 2. 마스터 서버 탐색 및 연결
        std::string masterIp = "127.0.0.1";
        int masterPort = 39201;

        if (argc >= 2) {
            masterIp = argv[1];
            std::cout << "[*] 커맨드라인 지정 마스터 IP 사용: " << masterIp << "\n";
        } else {
            std::cout << "[*] 마스터 서버 자동 탐색 중... (UDP 39200)\n";
            NetClient::MasterConnectionInfo connInfo;
            if (NetClient::DiscoverMasterServer(3, connInfo)) {
                masterIp = connInfo.ip;
                masterPort = connInfo.tcpPort;
                std::cout << "[+] 마스터 서버 발견! (" << masterIp << ":" << masterPort << ")\n";
            } else {
                std::cout << "[-] 자동 탐색 대기 중. 기본/로컬 IP 시도 (" << masterIp << ")\n";
            }
        }

        NetClient::AgentSocketClient client;
        int retryCount = 0;
        while (!client.Connect(masterIp, masterPort)) {
            retryCount++;
            std::cout << "[-] 마스터 연결 대기 중... (retry " << retryCount << ")\r" << std::flush;
            std::this_thread::sleep_for(std::chrono::seconds(2));
            if (retryCount % 5 == 0 && argc < 2) {
                NetClient::MasterConnectionInfo connInfo;
                if (NetClient::DiscoverMasterServer(2, connInfo)) {
                    masterIp = connInfo.ip;
                    masterPort = connInfo.tcpPort;
                    std::cout << "\n[+] 마스터 서버 발견! (" << masterIp << ":" << masterPort << ")\n";
                }
            }
        }

        std::cout << "\n[+] 마스터 서버 연결 성공! (" << masterIp << ":" << masterPort << ")\n";

        // 3. 인벤토리 등록 메시지 전송
        SimpleJson::JsonValue regMsg = SimpleJson::JsonValue::CreateObject();
        regMsg.Set("type", "CLIENT_REGISTER");
        regMsg.Set("payload", inv.ToJson());

        client.SendJsonMessage(regMsg.ToString());
        std::cout << "[*] 하드웨어 정보 전송 완료. 배포 승인 대기 중... (Info Sent. Waiting for Approval...)\n\n";

        // 4. 마스터로부터의 배포 승인 명령 대기 루프
        std::string incomingJson;
        while (client.ReadMessage(incomingJson)) {
            if (incomingJson.empty()) continue;

            std::cout << "[*] 마스터 명령 수신 (Command Received): " << incomingJson.substr(0, 80) << "...\n";

            // "MASTER_APPROVE_DEPLOY" 감지 시 배포 시퀀스 시작
            if (incomingJson.find("MASTER_APPROVE_DEPLOY") != std::string::npos) {
                std::cout << "\n=========================================================\n";
                std::cout << "[*] 마스터 승인 확인! 이미지 더빙 작업을 시작합니다. (Deploy Approved! Starting...)\n";
                std::cout << "=========================================================\n";

                // 마스터가 전달한 배포 메타데이터 동적 추출
                std::string ruleName = ExtractJsonString(incomingJson, "ruleName");
                std::string imageName = ExtractJsonString(incomingJson, "imageName");
                std::string imageLocalPath = ExtractJsonString(incomingJson, "imageLocalPath");
                std::string imageNetworkPath = ExtractJsonString(incomingJson, "imageNetworkPath");
                std::string sourceUrl = ExtractJsonString(incomingJson, "sourceUrl");
                int imageIndex = ExtractJsonInt(incomingJson, "imageIndex", 1);
                std::string targetDrive = ExtractJsonString(incomingJson, "targetDriveLetter");
                std::string bootDrive = ExtractJsonString(incomingJson, "bootDriveLetter");

                if (targetDrive.empty()) targetDrive = "W:";
                if (bootDrive.empty()) bootDrive = "S:";

                bool provisionerEnabled = false;
                std::string provisionerAssignedId = "";
                size_t provPos = incomingJson.find("\"provisioner\"");
                if (provPos != std::string::npos) {
                    size_t provBrace = incomingJson.find('{', provPos);
                    size_t provEnd = incomingJson.find('}', provBrace);
                    if (provBrace != std::string::npos && provEnd != std::string::npos) {
                        std::string provSub = incomingJson.substr(provBrace, provEnd - provBrace + 1);
                        provisionerEnabled = ExtractJsonBool(provSub, "enabled", false);
                        provisionerAssignedId = ExtractJsonString(provSub, "assignedId");
                    }
                }

                std::cout << "  - 적용 매칭 규칙 : " << (ruleName.empty() ? "기본 자동 선택 규칙" : ruleName) << "\n";
                std::cout << "  - 대상 WIM 파일  : " << (imageName.empty() ? "install.wim" : imageName) << " (Index: " << imageIndex << ")\n\n";

                // (1) 파티션 단계
                std::cout << "[단계 1/3] 타깃 디스크 파티셔닝 수행 중... (Step 1/3: Partitioning Disk...)\n";
                std::vector<std::string> diskpartCmds = ExtractPartitionCommands(incomingJson);
                if (diskpartCmds.empty()) {
                    diskpartCmds = {
                        "select disk 0",
                        "clean",
                        "convert gpt",
                        "create partition efi size=300",
                        "format quick fs=fat32 label=System",
                        "assign letter=S",
                        "create partition msr size=16",
                        "create partition primary",
                        "format quick fs=ntfs label=Windows",
                        "assign letter=W"
                    };
                }

                int targetDiskIdx = ExtractJsonInt(incomingJson, "targetDiskIndex", 0);
                auto partRes = PartitionEngine::ExecutePartitionPlan(targetDiskIdx, diskpartCmds);
                if (!partRes.success) {
                    std::cerr << "[-] 파티셔닝 실패 (Partition Failed): " << partRes.errorMessage << "\n";
                    // 실패 보고
                    SimpleJson::JsonValue failMsg = SimpleJson::JsonValue::CreateObject();
                    failMsg.Set("type", "CLIENT_RESULT");
                    failMsg.Set("status", "FAILED");
                    failMsg.Set("errorMessage", partRes.errorMessage);
                    client.SendJsonMessage(failMsg.ToString());
                    continue;
                }
                std::cout << "[+] 파티셔닝 완료! (Partitioning Completed!)\n";

                // 진행 보고 (10%)
                SimpleJson::JsonValue p1 = SimpleJson::JsonValue::CreateObject();
                p1.Set("type", "CLIENT_PROGRESS");
                p1.Set("status", "IMAGING");
                p1.Set("step", "파티션 구성 완료 (Partition Completed 1/3)");
                p1.Set("percent", 10.0);
                p1.Set("elapsedSeconds", 5);
                p1.Set("remainingSeconds", 120);
                client.SendJsonMessage(p1.ToString());

                // (2) 이미지 적용 (더빙)
                // 실제 WIM 파일 유효 경로 결정:
                // 1순위: 로컬 스토리지 / USB 드라이브 자동 검색 (가장 빠르고 100% 안정적)
                // 2순위: 마스터가 전달한 로컬 경로 (동일 PC 테스트용)
                // 3순위: 네트워크 공유 경로 (UNC 경로 및 Z: 드라이브 자동 마운트)
                // 4순위: 기본 C:\images
                std::string effectiveImagePath = "";

                // 1순위: USB / 로컬 드라이브 자동 스캔
                std::string localWim = FindLocalWimPath(imageName.empty() ? "Win11_Universal.wim" : imageName);
                if (!localWim.empty()) {
                    effectiveImagePath = localWim;
                    std::cout << "[+] 로컬 드라이브(USB 메모리 등)에서 WIM 파일을 발견했습니다: " << effectiveImagePath << "\n";
                }

                // 2순위: imageLocalPath 검증
                if (effectiveImagePath.empty() && !imageLocalPath.empty()) {
                    DWORD attr = GetFileAttributesA(imageLocalPath.c_str());
                    if (attr != INVALID_FILE_ATTRIBUTES && !(attr & FILE_ATTRIBUTE_DIRECTORY)) {
                        effectiveImagePath = imageLocalPath;
                    }
                }

                // 3순위: 네트워크 공유 경로 처리
                if (effectiveImagePath.empty() && !imageNetworkPath.empty()) {
                    std::string netPath = imageNetworkPath;
                    // 앞뒤 공백 및 따옴표 제거
                    while (!netPath.empty() && (netPath.front() == ' ' || netPath.front() == '"' || netPath.front() == '\'')) netPath.erase(netPath.begin());
                    while (!netPath.empty() && (netPath.back() == ' ' || netPath.back() == '"' || netPath.back() == '\'')) netPath.pop_back();

                    // 127.0.0.1 또는 localhost는 WinPE 클라이언트 입장에서 자기 자신이므로 실제 마스터 IP로 치환
                    size_t l1 = netPath.find("127.0.0.1");
                    if (l1 != std::string::npos) netPath.replace(l1, 9, masterIp);
                    size_t l2 = netPath.find("localhost");
                    if (l2 != std::string::npos) netPath.replace(l2, 9, masterIp);

                    // UNC 경로인 경우 (예: \\192.168.0.50\images\Win11_Universal.wim)
                    if (netPath.rfind("\\\\", 0) == 0) {
                        DWORD attr = GetFileAttributesA(netPath.c_str());
                        if (attr != INVALID_FILE_ATTRIBUTES && !(attr & FILE_ATTRIBUTE_DIRECTORY)) {
                            // 직접 UNC 접근 가능
                            effectiveImagePath = netPath;
                        } else {
                            // UNC 직접 접근 불가 시 WinPE SMB 세션 확보를 위해 Z: 드라이브 마운트 시도
                            size_t p1 = netPath.find('\\', 2);
                            if (p1 != std::string::npos) {
                                size_t p2 = netPath.find('\\', p1 + 1);
                                std::string uncShare = (p2 != std::string::npos) ? netPath.substr(0, p2) : netPath;
                                std::string subFile = (p2 != std::string::npos) ? netPath.substr(p2 + 1) : imageName;

                                std::cout << "[*] SMB 네트워크 공유 연결 시도: " << uncShare << " -> Z:\n";
                                // 최신 WinPE(Windows 11 22H2/24H2)의 게스트 로그온 차단 정책 자동 해제
                                system("reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\LanmanWorkstation\\Parameters\" /v AllowInsecureGuestAuth /t REG_DWORD /d 1 /f >nul 2>&1");
                                system("reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\LanmanWorkstation\\Parameters\" /v RequireSecuritySignature /t REG_DWORD /d 0 /f >nul 2>&1");

                                system("net use Z: /delete /y >nul 2>&1");

                                std::string netCmd = "net use Z: \"" + uncShare + "\" /persistent:no >nul 2>&1";
                                int r = system(netCmd.c_str());
                                if (r != 0) {
                                    // 게스트 및 익명 권한으로 순차 재시도
                                    std::string guestCmd = "net use Z: \"" + uncShare + "\" \"\" /user:guest /persistent:no >nul 2>&1";
                                    r = system(guestCmd.c_str());
                                    if (r != 0) {
                                        std::string anonCmd = "net use Z: \"" + uncShare + "\" \"\" /user:Everyone /persistent:no >nul 2>&1";
                                        system(anonCmd.c_str());
                                    }
                                }

                                std::string zCandidate = "Z:\\" + subFile;
                                DWORD zAttr = GetFileAttributesA(zCandidate.c_str());
                                if (zAttr != INVALID_FILE_ATTRIBUTES && !(zAttr & FILE_ATTRIBUTE_DIRECTORY)) {
                                    effectiveImagePath = zCandidate;
                                    std::cout << "[+] Z: 드라이브 마운트 성공 및 WIM 파일 확인: " << effectiveImagePath << "\n";
                                } else {
                                    effectiveImagePath = netPath;
                                }
                            } else {
                                effectiveImagePath = netPath;
                            }
                        }
                    } else {
                        effectiveImagePath = netPath;
                    }
                }

                if (effectiveImagePath.empty()) {
                    effectiveImagePath = "C:\\images\\" + (imageName.empty() ? "install.wim" : imageName);
                }

                std::cout << "[단계 2/3] OS WIM 이미지 적용(더빙) 시작... (Step 2/3: Applying WIM Image...)\n";
                std::cout << "  - WIM 경로: " << effectiveImagePath << "\n";

                DeployEngine::DeployParams dParams;
                dParams.imagePath = effectiveImagePath;
                dParams.imageIndex = imageIndex;
                dParams.targetDrive = targetDrive;
                dParams.bootDrive = bootDrive;
                dParams.runBcdBoot = true;

                DeployEngine::DeployResult deployRes = DeployEngine::ApplyWimImage(dParams, [&](const DeployEngine::DeployProgress& prog) {
                    std::cout << "\r[*] " << prog.step << ": " << (int)prog.percent << "% (ETA: " << prog.remainingSeconds << "s)   " << std::flush;

                    SimpleJson::JsonValue p = SimpleJson::JsonValue::CreateObject();
                    p.Set("type", "CLIENT_PROGRESS");
                    p.Set("status", "IMAGING");
                    p.Set("step", prog.step);
                    p.Set("percent", prog.percent);
                    p.Set("speedMbps", prog.speedMbps);
                    p.Set("elapsedSeconds", prog.elapsedSeconds);
                    p.Set("remainingSeconds", prog.remainingSeconds);
                    p.Set("detailMessage", prog.detail);
                    client.SendJsonMessage(p.ToString());
                });

                std::cout << "\n";

                // (3) 결과 보고
                SimpleJson::JsonValue finalResult = SimpleJson::JsonValue::CreateObject();
                finalResult.Set("type", "CLIENT_RESULT");
                if (deployRes.success) {
                    // (2.5) 사후 무인 프로비저닝 (Post-OOBE Auto-Provisioner) 주입
                    if (provisionerEnabled) {
                        std::cout << "\n---------------------------------------------------------\n";
                        std::cout << "[*] 사후 무인 프로비저너 자동 주입 시작 (Injecting Auto-Provisioner)...\n";
                        std::cout << "  - 대상 사번 (CJ_ID): " << (provisionerAssignedId.empty() ? "(공란 - 사전세팅 모드)" : provisionerAssignedId) << "\n";

                        // 윈도우가 실제로 설치된 타깃 드라이브 자동 감지
                        std::string targetDrivePrefix = DetectInstalledWindowsDrive(targetDrive);
                        while (!targetDrivePrefix.empty() && (targetDrivePrefix.back() == '\\' || targetDrivePrefix.back() == '/')) {
                            targetDrivePrefix.pop_back();
                        }
                        if (targetDrivePrefix.empty()) targetDrivePrefix = "W:";

                        std::string deployDir = targetDrivePrefix + "\\Temp\\CJ_Work\\MultiDeploy";
                        std::string scriptsDir = targetDrivePrefix + "\\Windows\\Setup\\Scripts";

                        system(("cmd.exe /c mkdir \"" + deployDir + "\" >nul 2>&1").c_str());
                        system(("cmd.exe /c mkdir \"" + scriptsDir + "\" >nul 2>&1").c_str());

                        // 1) assigned_info.json 생성
                        std::string jsonPath = deployDir + "\\assigned_info.json";
                        std::ofstream ofs(jsonPath);
                        if (ofs.is_open()) {
                            ofs << "{\n";
                            ofs << "  \"enabled\": true,\n";
                            ofs << "  \"assignedId\": \"" << SimpleJson::EscapeString(provisionerAssignedId) << "\"\n";
                            ofs << "}\n";
                            ofs.close();
                            std::cout << "  [+] assigned_info.json 주입 완료: " << jsonPath << "\n";
                        } else {
                            std::cerr << "  [-] assigned_info.json 생성 실패!\n";
                        }

                        // 2) CJAutoProvisioner.exe 복사 또는 다운로드
                        std::string targetExe = deployDir + "\\CJAutoProvisioner.exe";
                        bool exeCopied = false;

                        char modulePath[MAX_PATH] = { 0 };
                        GetModuleFileNameA(NULL, modulePath, MAX_PATH);
                        std::string agentDir = modulePath;
                        size_t lastSlash = agentDir.find_last_of("\\/");
                        if (lastSlash != std::string::npos) agentDir = agentDir.substr(0, lastSlash);

                        std::vector<std::string> localCandidates = {
                            agentDir + "\\CJAutoProvisioner.exe",
                            "X:\\Windows\\System32\\CJAutoProvisioner.exe",
                            "X:\\CJAutoProvisioner.exe",
                            ".\\CJAutoProvisioner.exe",
                            "Z:\\agent\\CJAutoProvisioner.exe",
                            "Z:\\CJAutoProvisioner.exe"
                        };

                        for (const auto& cand : localCandidates) {
                            if (GetFileAttributesA(cand.c_str()) != INVALID_FILE_ATTRIBUTES) {
                                if (CopyFileA(cand.c_str(), targetExe.c_str(), FALSE)) {
                                    std::cout << "  [+] 로컬/네트워크에서 CJAutoProvisioner.exe 복사 완료: " << cand << "\n";
                                    exeCopied = true;
                                    break;
                                }
                            }
                        }

                        if (!exeCopied) {
                            const char driveLetters[] = "DEFGHIJKLMNOPQRSTUVWXYZC";
                            for (int d = 0; driveLetters[d] != '\0'; ++d) {
                                std::string usbCand = std::string(1, driveLetters[d]) + ":\\agent\\CJAutoProvisioner.exe";
                                if (GetFileAttributesA(usbCand.c_str()) != INVALID_FILE_ATTRIBUTES) {
                                    if (CopyFileA(usbCand.c_str(), targetExe.c_str(), FALSE)) {
                                        std::cout << "  [+] USB/로컬 드라이브에서 복사 완료: " << usbCand << "\n";
                                        exeCopied = true;
                                        break;
                                    }
                                }
                            }
                        }

                        if (!exeCopied) {
                            std::string httpUrl = "http://" + masterIp + ":39202/agent/CJAutoProvisioner.exe";
                            std::cout << "  [*] 마스터 HTTP 서버에서 다운로드 시도: " << httpUrl << "\n";
                            std::string curlCmd = "curl.exe -s -f -o \"" + targetExe + "\" \"" + httpUrl + "\"";
                            if (system(curlCmd.c_str()) == 0 && GetFileAttributesA(targetExe.c_str()) != INVALID_FILE_ATTRIBUTES) {
                                std::cout << "  [+] HTTP 다운로드 성공!\n";
                                exeCopied = true;
                            }
                        }

                        if (!exeCopied) {
                            std::cerr << "  [-] CJAutoProvisioner.exe를 찾거나 복사하지 못했습니다.\n";
                        }

                        // 3-1) 오프라인 레지스트리(SOFTWARE)에 RunOnce 직접 등록 (기존 윈도우 일반 부팅 완벽 지원!)
                        std::string swHivePath = targetDrivePrefix + "\\Windows\\System32\\config\\SOFTWARE";
                        if (GetFileAttributesA(swHivePath.c_str()) != INVALID_FILE_ATTRIBUTES) {
                            std::string loadCmd = "reg.exe load HKLM\\OFFLINE_SOFTWARE \"" + swHivePath + "\" >nul 2>&1";
                            int rLoad = system(loadCmd.c_str());
                            if (rLoad == 0) {
                                std::string addCmd = "reg.exe add \"HKLM\\OFFLINE_SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\RunOnce\" /v \"CJAutoProvisioner\" /t REG_SZ /d \"\\\"C:\\Temp\\CJ_Work\\MultiDeploy\\CJAutoProvisioner.exe\\\"\" /f >nul 2>&1";
                                system(addCmd.c_str());
                                // UAC 보안창(사용자 계정 컨트롤) 무인 자동 승격을 위해 프롬프트 임시 비활성화 (0 = 확인 대화상자 없이 즉시 관리자 권한 승격 실행)
                                system("reg.exe add \"HKLM\\OFFLINE_SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System\" /v \"ConsentPromptBehaviorAdmin\" /t REG_DWORD /d 0 /f >nul 2>&1");
                                system("reg.exe unload HKLM\\OFFLINE_SOFTWARE >nul 2>&1");
                                std::cout << "  [+] 오프라인 레지스트리(RunOnce 및 UAC 무인 자동승격) 등록 완료!\n";
                            }
                        }

                        // 3-2) 공용 시작프로그램(StartUp) 1회성 런처 배치파일 주입 (로그인 즉시 실행 보장)
                        std::string startupDir = targetDrivePrefix + "\\ProgramData\\Microsoft\\Windows\\Start Menu\\Programs\\StartUp";
                        system(("cmd.exe /c mkdir \"" + startupDir + "\" >nul 2>&1").c_str());
                        std::string startupCmdPath = startupDir + "\\CJAutoProvisionerLauncher.cmd";
                        std::ofstream startupOfs(startupCmdPath);
                        if (startupOfs.is_open()) {
                            startupOfs << "@echo off\r\n";
                            startupOfs << "timeout /t 2 /nobreak >nul 2>&1\r\n";
                            startupOfs << "if exist \"C:\\Temp\\CJ_Work\\MultiDeploy\\CJAutoProvisioner.exe\" (\r\n";
                            startupOfs << "    start \"\" \"C:\\Temp\\CJ_Work\\MultiDeploy\\CJAutoProvisioner.exe\"\r\n";
                            startupOfs << ")\r\n";
                            startupOfs << "del \"%~f0\" >nul 2>&1\r\n";
                            startupOfs.close();
                            std::cout << "  [+] 공용 시작프로그램 1회용 자동실행 런처 등록 완료: " << startupCmdPath << "\n";
                        }

                        // 3-3) SetupComplete.cmd 등록 (Sysprep OOBE 환경 듀얼 안전망)
                        std::string setupCmdPath = scriptsDir + "\\SetupComplete.cmd";
                        std::ofstream scOfs(setupCmdPath, std::ios::app);
                        if (scOfs.is_open()) {
                            scOfs << "\r\n";
                            scOfs << "rem --- MultiDeploy Post-OOBE Auto-Provisioner Trigger ---\r\n";
                            scOfs << "if exist \"C:\\Temp\\CJ_Work\\MultiDeploy\\CJAutoProvisioner.exe\" (\r\n";
                            scOfs << "    reg.exe add \"HKLM\\Software\\Microsoft\\Windows\\CurrentVersion\\RunOnce\" /v \"CJAutoProvisioner\" /t REG_SZ /d \"\\\"C:\\Temp\\CJ_Work\\MultiDeploy\\CJAutoProvisioner.exe\\\"\" /f >nul 2>&1\r\n";
                            scOfs << "    start \"\" \"C:\\Temp\\CJ_Work\\MultiDeploy\\CJAutoProvisioner.exe\"\r\n";
                            scOfs << ")\r\n";
                            scOfs.close();
                            std::cout << "  [+] SetupComplete.cmd 등록 완료: " << setupCmdPath << "\n";
                        }

                        std::cout << "[+] 사후 무인 프로비저너 설정 완료! 윈도우 첫 부팅 시 전자동 설치가 수행됩니다.\n";
                        std::cout << "---------------------------------------------------------\n";
                    }
                    std::cout << "[+] [성공] OS 이미지 더빙 및 부트로더 구성 완료! (SUCCESS: Image Applied! Time: " << deployRes.durationSeconds << "s)\n";
                    finalResult.Set("status", "SUCCESS");
                    finalResult.Set("totalDurationSeconds", deployRes.durationSeconds);
                    finalResult.Set("errorMessage", "");
                    client.SendJsonMessage(finalResult.ToString());
                    std::cout << "[*] 배포 성공 보고 완료.\n";
                    deployedSuccessfully = true;
                    break;
                } else {
                    std::cerr << "[-] [실패] 이미지 배포 실패 (FAILED: Deploy Failed): " << deployRes.errorMessage << "\n";
                    finalResult.Set("status", "FAILED");
                    finalResult.Set("totalDurationSeconds", deployRes.durationSeconds);
                    finalResult.Set("errorMessage", deployRes.errorMessage);
                    client.SendJsonMessage(finalResult.ToString());
                    std::cout << "[*] 배포 실패 보고 완료. 마스터 서버의 재시도 명령 대기 중... (Failed. Waiting for retry command from Master...)\n";
                    continue;
                }
            }
        }

        // 성공한 경우 시스템 자동 재부팅 수행
        if (deployedSuccessfully) {
            std::cout << "\n=========================================================\n";
            std::cout << " [★ 배포 성공] 모든 이미지 더빙 및 부트로더 작업이 완료되었습니다!\n";
            std::cout << " [*] 5초 후 클라이언트 PC가 자동으로 재부팅됩니다...\n";
            std::cout << "=========================================================\n";
            for (int sec = 5; sec > 0; --sec) {
                std::cout << "[*] " << sec << "초 후 시스템이 재부팅됩니다... (취소: Ctrl+C)   \r" << std::flush;
                std::this_thread::sleep_for(std::chrono::seconds(1));
            }
            std::cout << "\n[*] 시스템 재부팅 명령을 실행합니다 (wpeutil reboot)...\n";
            client.Close();
            system("wpeutil reboot");
            system("shutdown /r /t 0 /f");
            ExitWindowsEx(EWX_REBOOT | EWX_FORCE, 0);
            return 0;
        }

        // 마스터 프로그램이 종료되거나 연결이 끊어졌을 때: 종료되지 않고 대기 후 재연결 시도
        std::cout << "\n[!] 마스터 프로그램과의 연결이 종료되었습니다. (Master disconnected)\n";
        std::cout << "[*] 대기 모드로 전환합니다. 마스터 프로그램 재실행 시 자동으로 다시 연결됩니다...\n\n";
        client.Close();
        std::this_thread::sleep_for(std::chrono::seconds(2));
    }

    std::cout << "[*] 클라이언트 세션 종료. (Session closed.)\n";
    return 0;
}
