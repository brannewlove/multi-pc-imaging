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
        size_t endPos = json.find('"', pos);
        if (endPos != std::string::npos) {
            return json.substr(pos, endPos - pos);
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
        size_t q2 = json.find('"', q1 + 1);
        if (q2 == std::string::npos || q2 > endArr) break;
        cmds.push_back(json.substr(q1 + 1, q2 - q1 - 1));
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

    // 2. 마스터 서버 탐색 및 연결
    std::string masterIp = "127.0.0.1";
    int masterPort = 39201;

    if (argc >= 2) {
        masterIp = argv[1];
        std::cout << "[*] 커맨드라인 지정 마스터 IP 사용 (Using Master IP): " << masterIp << "\n";
    } else {
        std::cout << "[*] 마스터 서버 자동 탐색 중... (Searching for Master UDP 39200)...\n";
        NetClient::MasterConnectionInfo connInfo;
        if (NetClient::DiscoverMasterServer(3, connInfo)) {
            masterIp = connInfo.ip;
            masterPort = connInfo.tcpPort;
            std::cout << "[+] 마스터 서버 발견! (Master Found): " << masterIp << ":" << masterPort << "\n";
        } else {
            std::cout << "[-] 자동 탐색 타임아웃. 기본/로컬 IP 시도 (Fallback to " << masterIp << ").\n";
        }
    }

    NetClient::AgentSocketClient client;
    int retryCount = 0;
    while (!client.Connect(masterIp, masterPort)) {
        retryCount++;
        std::cout << "[-] 마스터 연결 대기 중... (Waiting for Master Server... retry " << retryCount << ")\n";
        std::this_thread::sleep_for(std::chrono::seconds(2));
        if (retryCount >= 10 && argc < 2) {
            // 주기적으로 다시 브로드캐스트 탐색
            NetClient::MasterConnectionInfo connInfo;
            if (NetClient::DiscoverMasterServer(2, connInfo)) {
                masterIp = connInfo.ip;
                masterPort = connInfo.tcpPort;
            }
        }
    }

    std::cout << "[+] 마스터 서버 연결 성공! (Connected to Master: " << masterIp << ":" << masterPort << ")\n";

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
                    "format quick fs=fat32 label=\"System\"",
                    "assign letter=S",
                    "create partition msr size=16",
                    "create partition primary",
                    "format quick fs=ntfs label=\"Windows\"",
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
            // 실제 WIM 파일 유효 경로 결정 (로컬 경로 우선 -> 네트워크 공유 경로 -> 기본 C:\images)
            std::string effectiveImagePath = imageLocalPath;
            DWORD fileAttr = GetFileAttributesA(effectiveImagePath.c_str());
            if (fileAttr == INVALID_FILE_ATTRIBUTES && !imageNetworkPath.empty()) {
                effectiveImagePath = imageNetworkPath;
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
            } else {
                std::cerr << "[-] [실패] 이미지 배포 실패 (FAILED: Deploy Failed): " << deployRes.errorMessage << "\n";
                finalResult.Set("status", "FAILED");
                finalResult.Set("totalDurationSeconds", deployRes.durationSeconds);
                finalResult.Set("errorMessage", deployRes.errorMessage);
            }
            client.SendJsonMessage(finalResult.ToString());

            std::cout << "[*] 배포 결과 보고 완료. 시스템 종료/재부팅 대기... (Process completed. Ready for reboot.)\n";
            break;
        }
    }

    std::cout << "[*] 클라이언트 세션 종료. (Session closed.)\n";
    return 0;
}
