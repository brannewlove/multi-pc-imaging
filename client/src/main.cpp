#include <iostream>
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

// WinPE 환경에서 USB나 로컬 디스크에 보관된 WIM 파일 자동 탐색
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

                auto partRes = PartitionEngine::ExecutePartitionPlan(0, diskpartCmds);
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
                                system("net use Z: /delete /y >nul 2>&1");

                                std::string netCmd = "net use Z: \"" + uncShare + "\" /persistent:no >nul 2>&1";
                                int r = system(netCmd.c_str());
                                if (r != 0) {
                                    // 게스트 권한으로 재시도
                                    std::string guestCmd = "net use Z: \"" + uncShare + "\" \"\" /user:guest /persistent:no >nul 2>&1";
                                    system(guestCmd.c_str());
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
