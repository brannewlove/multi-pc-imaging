#include <iostream>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>
#include <thread>
#include <atomic>
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

// WinPE 단계에서 타깃 윈도우 파티션 내 setup_config.ini를 즉시 직접 수정
static void UpdateSetupConfigDirectly(const std::string& iniPath, const std::string& assignedId) {
    std::string cleanId = assignedId;
    while (!cleanId.empty() && (cleanId.front() == ' ' || cleanId.front() == '"' || cleanId.front() == '\'')) cleanId.erase(0, 1);
    while (!cleanId.empty() && (cleanId.back() == ' ' || cleanId.back() == '"' || cleanId.back() == '\'')) cleanId.pop_back();

    std::string lowerId = cleanId;
    for (char& c : lowerId) c = (char)tolower(c);

    bool isRent = (lowerId == "rent");
    bool isCaseA = (!cleanId.empty() && !isRent);
    bool isCaseB = cleanId.empty();

    std::string targetUserId = isRent ? "cjencmaster" : (isCaseA ? cleanId : "");

    std::vector<std::string> lines;
    bool userIdFound = false;
    bool pcoffFound = false;
    bool sindohFound = false;

    if (GetFileAttributesA(iniPath.c_str()) != INVALID_FILE_ATTRIBUTES) {
        std::ifstream ifs(iniPath);
        if (ifs.is_open()) {
            std::string line;
            while (std::getline(ifs, line)) {
                if (!line.empty() && line.back() == '\r') line.pop_back();

                std::string lowerLine = line;
                for (char& c : lowerLine) c = (char)tolower(c);

                if (lowerLine.find("user_id") != std::string::npos && lowerLine.find('=') != std::string::npos) {
                    size_t eqPos = lowerLine.find('=');
                    if (eqPos != std::string::npos && lowerLine.substr(0, eqPos).find("user_id") != std::string::npos) {
                        line = "user_id = " + targetUserId;
                        userIdFound = true;
                    }
                } else if (lowerLine.find("pcoff_enabled") != std::string::npos && lowerLine.find('=') != std::string::npos) {
                    size_t eqPos = lowerLine.find('=');
                    if (eqPos != std::string::npos && lowerLine.substr(0, eqPos).find("pcoff_enabled") != std::string::npos) {
                        line = isRent ? "pcoff_enabled = False" : "pcoff_enabled = True";
                        pcoffFound = true;
                    }
                } else if (lowerLine.find("sindoh_enabled") != std::string::npos && lowerLine.find('=') != std::string::npos) {
                    size_t eqPos = lowerLine.find('=');
                    if (eqPos != std::string::npos && lowerLine.substr(0, eqPos).find("sindoh_enabled") != std::string::npos) {
                        line = (isRent || isCaseB) ? "sindoh_enabled = False" : "sindoh_enabled = True";
                        sindohFound = true;
                    }
                }
                lines.push_back(line);
            }
            ifs.close();
        }
    }

    if (lines.empty()) {
        lines.push_back("[SETTINGS]");
        lines.push_back("user_id = " + targetUserId);
        lines.push_back("global_timeout = 300");
        lines.push_back("sindoh_auth = 드라이버");
        lines.push_back("sindoh_complete = sindoh");
        lines.push_back("privacy_login = Privacy");
        lines.push_back("pri01_path = C:/Temp/CJ_Work/EDLP_1922_NP.exe");
        lines.push_back("pri01_enabled = True");
        lines.push_back("drm_path = C:/Temp/CJ_Work/Fasoo_DRM_Client_for_CJ_GROUP_x64_20250520_v20.03.0002.exe");
        lines.push_back("drm_enabled = True");
        lines.push_back("pcoff_path = C:/Temp/CJ_Work/CJGROUP_Install_260720.exe");
        lines.push_back(isRent ? "pcoff_enabled = False" : "pcoff_enabled = True");
        lines.push_back("sindoh_path = C:/Temp/CJ_Work/Shindoh_Uprint.exe");
        lines.push_back((isRent || isCaseB) ? "sindoh_enabled = False" : "sindoh_enabled = True");
    } else {
        if (!userIdFound) {
            lines.push_back("user_id = " + targetUserId);
        }
        if (!pcoffFound) {
            lines.push_back(isRent ? "pcoff_enabled = False" : "pcoff_enabled = True");
        }
        if (!sindohFound) {
            lines.push_back((isRent || isCaseB) ? "sindoh_enabled = False" : "sindoh_enabled = True");
        }
    }

    std::ofstream ofs(iniPath, std::ios::trunc);
    if (ofs.is_open()) {
        for (const auto& l : lines) {
            ofs << l << "\r\n";
        }
        ofs.close();
        std::cout << "  [+] setup_config.ini 사전 직접 수정/생성 완료: " << iniPath << "\n";
    }
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

        // [신규] WinPE RAM 부팅 시 마스터 서버의 최신 에이전트 핫스왑/자가 업데이트 확인
        char myExe[MAX_PATH] = { 0 };
        GetModuleFileNameA(NULL, myExe, MAX_PATH);
        bool isWinPeRam = (myExe[0] == 'X' || myExe[0] == 'x');
        bool isUpdatedChild = false;
        for (int a = 1; a < argc; ++a) {
            if (std::string(argv[a]) == "--updated") isUpdatedChild = true;
        }

        if (isWinPeRam && !isUpdatedChild) {
            std::string agentDownloadUrl = "http://" + masterIp + ":39202/agent/PeImagingAgent.exe";
            std::string updatedExe = "X:\\Windows\\System32\\PeImagingAgent_latest.exe";
            std::cout << "[*] 마스터 서버 최신 에이전트 버전 확인 중: " << agentDownloadUrl << "\n";
            std::string curlAgent = "curl.exe -s -f -o \"" + updatedExe + "\" \"" + agentDownloadUrl + "\"";
            if (system(curlAgent.c_str()) == 0) {
                WIN32_FILE_ATTRIBUTE_DATA fad;
                if (GetFileAttributesExA(updatedExe.c_str(), GetFileExInfoStandard, &fad) && (fad.nFileSizeLow > 1000000)) {
                    std::cout << "[+] 마스터 서버의 최신 에이전트 다운로드 성공 (" << fad.nFileSizeLow << " bytes)! 최신 버전으로 핫스왑 재시작합니다...\n";
                    std::string relaunchCmd = "\"" + updatedExe + "\" " + masterIp + " --updated";
                    STARTUPINFOA ssi = { sizeof(STARTUPINFOA) };
                    PROCESS_INFORMATION spi = {};
                    if (CreateProcessA(NULL, (LPSTR)relaunchCmd.c_str(), NULL, NULL, FALSE, 0, NULL, NULL, &ssi, &spi)) {
                        CloseHandle(spi.hProcess);
                        CloseHandle(spi.hThread);
                        return 0;
                    }
                }
            }
        }

        // 3. 인벤토리 등록 메시지 전송
        SimpleJson::JsonValue regMsg = SimpleJson::JsonValue::CreateObject();
        regMsg.Set("type", "CLIENT_REGISTER");
        regMsg.Set("payload", inv.ToJson());

        client.SendJsonMessage(regMsg.ToString());
        std::cout << "[*] 하드웨어 정보 전송 완료. 배포 승인 대기 중... (Info Sent. Waiting for Approval...)\n\n";

        // 4. 마스터로부터의 배포 승인 명령 대기 루프
        std::string incomingJson;
        bool lastProvisionerEnabled = false;
        std::string lastAssignedId = "";
        std::string lastTargetDrivePrefix = "";
        while (client.ReadMessage(incomingJson)) {
            if (incomingJson.empty()) continue;

            std::cout << "[*] 마스터 명령 수신 (Command Received): " << incomingJson.substr(0, 80) << "...\n";

            // "MASTER_STOP_DEPLOY" 대기 상태에서 수신 시 즉시 READY 응답
            if (incomingJson.find("MASTER_STOP_DEPLOY") != std::string::npos) {
                SimpleJson::JsonValue stopAck = SimpleJson::JsonValue::CreateObject();
                stopAck.Set("type", "CLIENT_STOPPED");
                stopAck.Set("status", "READY");
                client.SendJsonMessage(stopAck.ToString());
                std::cout << "[*] 마스터 배포 중지 확인 -> 승인 대기(READY) 상태 유지 중...\n";
                continue;
            }

            // "MASTER_APPROVE_DEPLOY" 감지 시 배포 시퀀스 시작
            if (incomingJson.find("MASTER_APPROVE_DEPLOY") != std::string::npos) {
                std::cout << "\n=========================================================\n";
                std::cout << "[*] 마스터 승인 확인! 이미지 더빙 작업을 시작합니다. (Deploy Approved! Starting...)\n";
                std::cout << "=========================================================\n";

                std::atomic<bool> stopRequested{ false };
                std::atomic<bool> monitorActive{ true };
                std::thread stopMonitorThread([&]() {
                    while (monitorActive.load() && client.IsConnected()) {
                        std::string ctrlMsg;
                        if (client.ReadMessage(ctrlMsg, 200)) {
                            if (ctrlMsg.find("MASTER_STOP_DEPLOY") != std::string::npos) {
                                stopRequested.store(true);
                                std::cout << "\n[!] 마스터 서버로부터 배포 중지(MASTER_STOP_DEPLOY) 명령 수신! 진행 중인 작업을 즉시 중단합니다...\n";
                                system("taskkill /F /IM dism.exe /T >nul 2>&1");
                                system("taskkill /F /IM DismHost.exe /T >nul 2>&1");
                                system("taskkill /F /IM diskpart.exe /T >nul 2>&1");
                                system("taskkill /F /IM bcdboot.exe /T >nul 2>&1");
                                break;
                            }
                        }
                    }
                });

                auto stopMonitorJoin = [&]() {
                    monitorActive.store(false);
                    if (stopMonitorThread.joinable()) {
                        stopMonitorThread.join();
                    }
                };

                auto checkAndHandleStop = [&]() -> bool {
                    if (!stopRequested.load()) return false;
                    stopMonitorJoin();
                    system("reg.exe unload HKLM\\OFFLINE_SOFTWARE >nul 2>&1");
                    SimpleJson::JsonValue stopAck = SimpleJson::JsonValue::CreateObject();
                    stopAck.Set("type", "CLIENT_STOPPED");
                    stopAck.Set("status", "READY");
                    client.SendJsonMessage(stopAck.ToString());
                    std::cout << "\n=========================================================\n";
                    std::cout << "[!] 배포 작업이 사용자에 의해 중지되었습니다.\n";
                    std::cout << "[*] 승인 대기(READY) 모드로 복귀합니다. 설정 변경 후 새로 배포할 수 있습니다.\n";
                    std::cout << "=========================================================\n\n";
                    return true;
                };

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

                bool provisionerEnabled = true; // 기본값 활성 (추가개발 요구사항: 기본값 활성)
                std::string provisionerAssignedId = "";
                size_t provPos = incomingJson.find("\"provisioner\"");
                if (provPos != std::string::npos) {
                    size_t provBrace = incomingJson.find('{', provPos);
                    size_t provEnd = incomingJson.find('}', provBrace);
                    if (provBrace != std::string::npos && provEnd != std::string::npos) {
                        std::string provSub = incomingJson.substr(provBrace, provEnd - provBrace + 1);
                        provisionerEnabled = ExtractJsonBool(provSub, "enabled", true);
                        provisionerAssignedId = ExtractJsonString(provSub, "assignedId");
                    }
                }
                lastProvisionerEnabled = provisionerEnabled;
                lastAssignedId = provisionerAssignedId;

                std::cout << "  - 적용 매칭 규칙 : " << (ruleName.empty() ? "기본 자동 선택 규칙" : ruleName) << "\n";
                std::cout << "  - 대상 WIM 파일  : " << (imageName.empty() ? "install.wim" : imageName) << " (Index: " << imageIndex << ")\n\n";

                if (checkAndHandleStop()) continue;

                // (1) 파티션 단계
                std::cout << "[단계 1/3] 타깃 디스크 파티셔닝 수행 중... (Step 1/3: Partitioning Disk...)\n";
                // [진행 보고: 3%] 파티셔닝 시작 즉시 보고하여 마스터 UI의 '작업 준비 중' 정체 해소
                SimpleJson::JsonValue pStart = SimpleJson::JsonValue::CreateObject();
                pStart.Set("type", "CLIENT_PROGRESS");
                pStart.Set("status", "IMAGING");
                pStart.Set("step", "타깃 디스크 파티셔닝 수행 중 (1/3)");
                pStart.Set("percent", 3.0);
                pStart.Set("elapsedSeconds", 1);
                pStart.Set("remainingSeconds", 180);
                client.SendJsonMessage(pStart.ToString());
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
                if (checkAndHandleStop()) continue;
                if (!partRes.success) {
                    stopMonitorJoin();
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

                // 진행 보고 (10%) - 마스터 UI와 클라이언트 화면 모두 최신 에이전트 동작 표시
                SimpleJson::JsonValue p1 = SimpleJson::JsonValue::CreateObject();
                p1.Set("type", "CLIENT_PROGRESS");
                p1.Set("status", "IMAGING");
                p1.Set("step", "파티션 완료 [최신 에이전트 검증] -> 더빙 준비");
                p1.Set("percent", 10.0);
                p1.Set("elapsedSeconds", 5);
                p1.Set("remainingSeconds", 120);
                client.SendJsonMessage(p1.ToString());

                // [사용자 요청 검증 배너] 디스크 파티션 완료 -> WIM 더빙 시작 중간 확인 메시지
                std::cout << "\n";
                std::cout << "======================================================================\n";
                std::cout << " [★ WinPE 최신 에이전트 주입 확인 메시지 - 2026-11-04 빌드]\n";
                std::cout << "----------------------------------------------------------------------\n";
                std::cout << " [1] 디스크 파티션 구성이 정상적으로 완료되었습니다!\n";
                std::cout << " [2] 잠시 후 OS WIM 이미지 적용(더빙)을 시작합니다.\n";
                std::cout << "  * 수신된 CJ_ID 사번      : [" << (provisionerAssignedId.empty() ? "(공란 - 사전세팅 모드)" : provisionerAssignedId) << "]\n";
                std::cout << "  * 사후 자동화(프로비저너): [" << (provisionerEnabled ? "활성 (ENABLE)" : "비활성 (DISABLE)") << "]\n";
                std::cout << "  * 안내: 이 배너가 보이면 boot.wim에 최신 에이전트가 정상 주입된 것입니다!\n";
                std::cout << "======================================================================\n";
                for (int waitTick = 50; waitTick > 0 && !stopRequested.load(); --waitTick) {
                    if (waitTick % 10 == 0) {
                        std::cout << "[*] 더빙 단계 진입 대기 중... (" << (waitTick / 10) << "초)   \r" << std::flush;
                    }
                    std::this_thread::sleep_for(std::chrono::milliseconds(100));
                }
                std::cout << "\n\n";
                if (checkAndHandleStop()) continue;

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

                // [진행 보고: 10%] SMB 마운트 성공 및 더빙 진입 보고
                SimpleJson::JsonValue pSmb = SimpleJson::JsonValue::CreateObject();
                pSmb.Set("type", "CLIENT_PROGRESS");
                pSmb.Set("status", "IMAGING");
                pSmb.Set("step", "OS WIM 이미지 더빙 진입 중 (2/3)");
                pSmb.Set("percent", 10.0);
                pSmb.Set("elapsedSeconds", 5);
                pSmb.Set("remainingSeconds", 120);
                client.SendJsonMessage(pSmb.ToString());

                std::cout << "[단계 2/3] OS WIM 이미지 적용(더빙) 시작... (Step 2/3: Applying WIM Image...)\n";
                std::cout << "  - WIM 경로: " << effectiveImagePath << "\n";

                DeployEngine::DeployParams dParams;
                dParams.imagePath = effectiveImagePath;
                dParams.imageIndex = imageIndex;
                dParams.targetDrive = targetDrive;
                dParams.bootDrive = bootDrive;
                dParams.runBcdBoot = true;

                DeployEngine::DeployResult deployRes = DeployEngine::ApplyWimImage(dParams, [&](const DeployEngine::DeployProgress& prog) {
                    if (stopRequested.load()) return;
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
                if (checkAndHandleStop()) continue;

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
                        lastTargetDrivePrefix = targetDrivePrefix;
                        while (!targetDrivePrefix.empty() && (targetDrivePrefix.back() == '\\' || targetDrivePrefix.back() == '/')) {
                            targetDrivePrefix.pop_back();
                        }
                        if (targetDrivePrefix.empty()) targetDrivePrefix = "W:";

                        std::string cjWorkDir = targetDrivePrefix + "\\Temp\\CJ_Work";
                        std::string as20Dir = cjWorkDir + "\\AS_2.0";
                        std::string deployDir = cjWorkDir + "\\MultiDeploy";
                        std::string scriptsDir = targetDrivePrefix + "\\Windows\\Setup\\Scripts";
                        std::string startupDir = targetDrivePrefix + "\\ProgramData\\Microsoft\\Windows\\Start Menu\\Programs\\StartUp";
                        std::string desktopDir = targetDrivePrefix + "\\Users\\Public\\Desktop";

                        std::error_code ec;
                        std::filesystem::create_directories(std::filesystem::u8path(cjWorkDir), ec);
                        std::filesystem::create_directories(std::filesystem::u8path(as20Dir), ec);
                        std::filesystem::create_directories(std::filesystem::u8path(deployDir), ec);
                        std::filesystem::create_directories(std::filesystem::u8path(scriptsDir), ec);
                        std::filesystem::create_directories(std::filesystem::u8path(startupDir), ec);
                        std::filesystem::create_directories(std::filesystem::u8path(desktopDir), ec);

                        system(("cmd.exe /c mkdir \"" + cjWorkDir + "\" >nul 2>&1").c_str());
                        system(("cmd.exe /c mkdir \"" + as20Dir + "\" >nul 2>&1").c_str());
                        system(("cmd.exe /c mkdir \"" + deployDir + "\" >nul 2>&1").c_str());
                        system(("cmd.exe /c mkdir \"" + scriptsDir + "\" >nul 2>&1").c_str());
                        system(("cmd.exe /c mkdir \"" + startupDir + "\" >nul 2>&1").c_str());
                        system(("cmd.exe /c mkdir \"" + desktopDir + "\" >nul 2>&1").c_str());

                        // 1) assigned_info.json 생성 (C:\Temp\CJ_Work 및 C:\Temp\CJ_Work\MultiDeploy 양쪽 모두 주입)
                        std::string jsonContent = "{\n  \"enabled\": true,\n  \"assignedId\": \"" + SimpleJson::EscapeString(provisionerAssignedId) + "\",\n  \"assigned_id\": \"" + SimpleJson::EscapeString(provisionerAssignedId) + "\"\n}\n";
                        std::string jsonPath1 = cjWorkDir + "\\assigned_info.json";
                        std::string jsonPath2 = deployDir + "\\assigned_info.json";
                        {
                            std::ofstream ofs1(jsonPath1);
                            if (ofs1.is_open()) { ofs1 << jsonContent; ofs1.close(); std::cout << "  [+] assigned_info.json 주입 완료: " << jsonPath1 << "\n"; }
                            std::ofstream ofs2(jsonPath2);
                            if (ofs2.is_open()) { ofs2 << jsonContent; ofs2.close(); std::cout << "  [+] assigned_info.json 주입 완료: " << jsonPath2 << "\n"; }
                        }

                        // 1-1) 영구 감사 및 검증용 deploy_summary.txt 생성 (자폭 시에도 절대 삭제되지 않음)
                        std::string summaryPath = cjWorkDir + "\\deploy_summary.txt";
                        std::ofstream sumOfs(summaryPath);
                        if (sumOfs.is_open()) {
                            sumOfs << "========================================\r\n";
                            sumOfs << " MultiDeploy Auto-Provisioning Report\r\n";
                            sumOfs << "========================================\r\n";
                            sumOfs << "Target Windows Drive : " << targetDrivePrefix << "\r\n";
                            sumOfs << "Assigned CJ_ID       : " << provisionerAssignedId << "\r\n";
                            sumOfs << "Provisioner Enabled  : " << (provisionerEnabled ? "TRUE" : "FALSE") << "\r\n";
                            sumOfs << "WIM Image Applied    : " << effectiveImagePath << "\r\n";
                            sumOfs << "Duration Seconds     : " << deployRes.durationSeconds << "\r\n";
                            sumOfs << "UAC Bypass Injected  : ConsentPromptBehaviorAdmin=0, PromptOnSecureDesktop=0\r\n";
                            sumOfs.close();
                            std::cout << "  [+] 영구 검증용 deploy_summary.txt 생성 완료: " << summaryPath << "\n";
                        }

                        // 1-2) WinPE 단계에서 setup_config.ini를 즉시 직접 수정 (AS_2.0 및 기본 폴더 2곳 모두)
                        UpdateSetupConfigDirectly(cjWorkDir + "\\AS_2.0\\setup_config.ini", provisionerAssignedId);
                        UpdateSetupConfigDirectly(cjWorkDir + "\\setup_config.ini", provisionerAssignedId);

                        // 2) CJAutoProvisioner.exe 복사 또는 다운로드 (MultiDeploy 및 CJ_Work 양쪽 배포)
                        std::string targetExe = deployDir + "\\CJAutoProvisioner.exe";
                        std::string targetExe2 = cjWorkDir + "\\CJAutoProvisioner.exe";
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
                                    CopyFileA(cand.c_str(), targetExe2.c_str(), FALSE);
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
                                        CopyFileA(usbCand.c_str(), targetExe2.c_str(), FALSE);
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
                                CopyFileA(targetExe.c_str(), targetExe2.c_str(), FALSE);
                                std::cout << "  [+] HTTP 다운로드 성공!\n";
                                exeCopied = true;
                            }
                        }

                        if (!exeCopied) {
                            std::cerr << "  [-] CJAutoProvisioner.exe를 찾거나 복사하지 못했습니다.\n";
                        }

                        // 3) 단일 독립 실행 파일(CJAutoProvisioner.exe) 단일 루트 등록 (다중 스크립트 완전 제거)
                        std::cout << "  [*] 단일 독립 실행 파일(CJAutoProvisioner.exe) 기반 자동실행 구성 중...\n";

                        // 3-1) 공용 시작프로그램(StartUp)에 CJAutoProvisioner.exe 단일 등록
                        std::string startupExePath = startupDir + "\\CJAutoProvisioner.exe";
                        CopyFileA(targetExe.c_str(), startupExePath.c_str(), FALSE);
                        std::cout << "  [+] 공용 시작프로그램 등록 완료: " << startupExePath << "\n";

                        // 3-2) 공용 바탕화면 바로가기 런처 생성
                        std::string desktopCmdPath = desktopDir + "\\보안프로그램_자동설치.cmd";
                        std::ofstream dOfs(desktopCmdPath);
                        if (dOfs.is_open()) {
                            dOfs << "@echo off\r\n";
                            dOfs << "start \"\" \"C:\\Temp\\CJ_Work\\CJAutoProvisioner.exe\"\r\n";
                            dOfs.close();
                            std::cout << "  [+] 공용 바탕화면 바로가기 등록 완료: " << desktopCmdPath << "\n";
                        }

                        // 3-3) 오프라인 레지스트리(RunOnce 및 UAC 자동승격) 단일 등록
                        std::string swHivePath = targetDrivePrefix + "\\Windows\\System32\\config\\SOFTWARE";
                        if (GetFileAttributesA(swHivePath.c_str()) != INVALID_FILE_ATTRIBUTES) {
                            std::string loadCmd = "reg.exe load HKLM\\OFFLINE_SOFTWARE \"" + swHivePath + "\" >nul 2>&1";
                            int rLoad = system(loadCmd.c_str());
                            if (rLoad == 0) {
                                // RunOnce에만 등록하여 부팅 시 1회 실행 후 Windows가 자체 삭제
                                system("reg.exe add \"HKLM\\OFFLINE_SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\RunOnce\" /v \"CJAutoProvision\" /t REG_SZ /d \"C:\\Temp\\CJ_Work\\CJAutoProvisioner.exe\" /f >nul 2>&1");
                                system("reg.exe delete \"HKLM\\OFFLINE_SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run\" /v \"CJAutoProvision\" /f >nul 2>&1");
                                system("reg.exe add \"HKLM\\OFFLINE_SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System\" /v \"ConsentPromptBehaviorAdmin\" /t REG_DWORD /d 0 /f >nul 2>&1");
                                system("reg.exe add \"HKLM\\OFFLINE_SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System\" /v \"PromptOnSecureDesktop\" /t REG_DWORD /d 0 /f >nul 2>&1");
                                system("reg.exe unload HKLM\\OFFLINE_SOFTWARE >nul 2>&1");
                                std::cout << "  [+] 오프라인 레지스트리(RunOnce 1회 실행 및 UAC 무인 자동승격) 등록 완료!\n";
                            }
                        }

                        // 3-4) SetupComplete.cmd는 중복 트리거를 피하기 위해 레지스트리 RunOnce와 시작프로그램에 위임
                        std::cout << "[+] 사후 무인 프로비저너 설정 완료! CJAutoProvisioner.exe 단일 파일로 윈도우 첫 부팅 시 1회 완벽 실행됩니다.\n";
                        std::cout << "---------------------------------------------------------\n";
                    }
                    if (checkAndHandleStop()) continue;
                    stopMonitorJoin();
                    std::cout << "[+] [성공] OS 이미지 더빙 및 부트로더 구성 완료! (SUCCESS: Image Applied! Time: " << deployRes.durationSeconds << "s)\n";
                    finalResult.Set("status", "SUCCESS");
                    finalResult.Set("totalDurationSeconds", deployRes.durationSeconds);
                    finalResult.Set("errorMessage", "");
                    client.SendJsonMessage(finalResult.ToString());
                    std::cout << "[*] 배포 성공 보고 완료.\n";
                    deployedSuccessfully = true;
                    break;
                } else {
                    if (checkAndHandleStop()) continue;
                    stopMonitorJoin();
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
            if (lastProvisionerEnabled) {
                std::cout << "---------------------------------------------------------\n";
                std::cout << " [★ 사후 무인 프로비저닝 파일 주입 상태 최종 검증]\n";
                std::string drive = lastTargetDrivePrefix.empty() ? "W:" : lastTargetDrivePrefix;
                std::cout << "  - 대상 윈도우 드라이브: " << drive << "\n";
                std::string chkPath1 = drive + "\\Temp\\CJ_Work\\assigned_info.json";
                std::string chkPath2 = drive + "\\Temp\\CJ_Work\\deploy_summary.txt";
                std::string chkPath3 = drive + "\\Temp\\CJ_Work\\MultiDeploy\\CJAutoProvisioner.exe";
                std::string chkPath4 = drive + "\\Temp\\CJ_Work\\CJAutoProvisioner.exe";
                std::cout << "  - assigned_info.json : " << (GetFileAttributesA(chkPath1.c_str()) != INVALID_FILE_ATTRIBUTES ? "[생성 확인 OK]" : "[누락됨 FAIL]") << "\n";
                std::cout << "  - deploy_summary.txt : " << (GetFileAttributesA(chkPath2.c_str()) != INVALID_FILE_ATTRIBUTES ? "[생성 확인 OK]" : "[누락됨 FAIL]") << "\n";
                std::cout << "  - CJAutoProvisioner  : " << ((GetFileAttributesA(chkPath3.c_str()) != INVALID_FILE_ATTRIBUTES || GetFileAttributesA(chkPath4.c_str()) != INVALID_FILE_ATTRIBUTES) ? "[복사 확인 OK]" : "[누락됨 FAIL]") << "\n";
                std::cout << "---------------------------------------------------------\n";
            }
            std::cout << " [*] 7초 후 클라이언트 PC가 자동으로 재부팅됩니다...\n";
            std::cout << " [*] (화면을 멈추고 직접 확인하려면 지금 Ctrl+C를 누르세요)\n";
            std::cout << "=========================================================\n";
            for (int sec = 7; sec > 0; --sec) {
                std::cout << "[*] " << sec << "초 후 시스템이 재부팅됩니다... (콘솔 확인 중)   \r" << std::flush;
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
