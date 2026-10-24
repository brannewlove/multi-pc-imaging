#include <iostream>
#include <string>
#include <thread>
#include <chrono>
#include "hw_scanner.h"
#include "net_client.h"
#include "partition_engine.h"
#include "deploy_engine.h"
#include "simple_json.h"

void PrintBanner() {
    std::cout << "=========================================================\n";
    std::cout << "    Multi-PC Automated Imaging Client Agent (WinPE)      \n";
    std::cout << "    Version: 1.0.0 (Native C++ / Zero-Dependency)        \n";
    std::cout << "=========================================================\n\n";
}

int main(int argc, char* argv[]) {
    PrintBanner();

    // 1. 하드웨어 인벤토리 수집
    std::cout << "[*] 하드웨어 및 시스템 사양 스캔 중...\n";
    HardwareScanner::FullInventory inv = HardwareScanner::ScanHardwareInventory();

    std::cout << "  - 제조사: " << inv.system.manufacturer << "\n";
    std::cout << "  - 모델명: " << inv.system.modelName << "\n";
    std::cout << "  - 시리얼: " << inv.system.serialNumber << "\n";
    std::cout << "  - MAC 주소: " << inv.network.primaryMac << "\n";
    std::cout << "  - IP 주소: " << inv.network.ipAddress << "\n";
    std::cout << "  - 검색된 디스크 수량: " << inv.disks.size() << "개\n";
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
        std::cout << "[*] 커맨드라인 지정 마스터 IP 사용: " << masterIp << "\n";
    } else {
        std::cout << "[*] 마스터 서버 자동 탐색 중 (UDP Broadcast Port 39200)...\n";
        NetClient::MasterConnectionInfo connInfo;
        if (NetClient::DiscoverMasterServer(3, connInfo)) {
            masterIp = connInfo.ip;
            masterPort = connInfo.tcpPort;
            std::cout << "[+] 마스터 서버 발견: " << masterIp << ":" << masterPort << "\n";
        } else {
            std::cout << "[-] 자동 탐색 타임아웃. 기본 게이트웨이/로컬 연결을 시도합니다 (" << masterIp << ").\n";
        }
    }

    NetClient::AgentSocketClient client;
    int retryCount = 0;
    while (!client.Connect(masterIp, masterPort)) {
        retryCount++;
        std::cout << "[-] 마스터 연결 대기 중... (" << retryCount << "회 재시도)\n";
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

    std::cout << "[+] 마스터 서버와 연결 성공! (" << masterIp << ":" << masterPort << ")\n";

    // 3. 인벤토리 등록 메시지 전송
    SimpleJson::JsonValue regMsg = SimpleJson::JsonValue::CreateObject();
    regMsg.Set("type", "CLIENT_REGISTER");
    regMsg.Set("payload", inv.ToJson());

    client.SendMessage(regMsg.ToString());
    std::cout << "[*] 하드웨어 정보를 마스터로 전송 완료. 배포 승인 대기 중...\n\n";

    // 4. 마스터로부터의 배포 승인 명령 대기 루프
    std::string incomingJson;
    while (client.ReadMessage(incomingJson)) {
        if (incomingJson.empty()) continue;

        std::cout << "[*] 마스터 명령 수신됨: " << incomingJson.substr(0, 80) << "...\n";

        // "MASTER_APPROVE_DEPLOY" 감지 시 배포 시퀀스 시작
        if (incomingJson.find("MASTER_APPROVE_DEPLOY") != std::string::npos) {
            std::cout << "\n=========================================================\n";
            std::cout << "[*] 마스터 승인 확인! 이미지 더빙 작업을 시작합니다.\n";
            std::cout << "=========================================================\n";

            // (1) 파티션 단계
            std::cout << "[단계 1/3] 타깃 디스크 파티셔닝 및 포맷 수행 중...\n";
            std::vector<std::string> diskpartCmds = {
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

            auto partRes = PartitionEngine::ExecutePartitionPlan(0, diskpartCmds);
            if (!partRes.success) {
                std::cerr << "[-] 파티셔닝 실패: " << partRes.errorMessage << "\n";
                // 실패 보고
                SimpleJson::JsonValue failMsg = SimpleJson::JsonValue::CreateObject();
                failMsg.Set("type", "CLIENT_RESULT");
                failMsg.Set("status", "FAILED");
                failMsg.Set("errorMessage", partRes.errorMessage);
                client.SendMessage(failMsg.ToString());
                continue;
            }
            std::cout << "[+] 파티셔닝 완료!\n";

            // 진행 보고 (10%)
            SimpleJson::JsonValue p1 = SimpleJson::JsonValue::CreateObject();
            p1.Set("type", "CLIENT_PROGRESS");
            p1.Set("status", "IMAGING");
            p1.Set("step", "파티션 구성 완료 (1/3)");
            p1.Set("percent", 10.0);
            p1.Set("elapsedSeconds", 5);
            p1.Set("remainingSeconds", 120);
            client.SendMessage(p1.ToString());

            // (2) 이미지 적용 (더빙)
            std::cout << "[단계 2/3] OS WIM 이미지 적용(더빙) 시작...\n";
            DeployEngine::DeployParams dParams;
            dParams.imagePath = "C:\\images\\install.wim"; // 실제 환경에서는 마운트된 WIM 경로
            dParams.imageIndex = 1;
            dParams.targetDrive = "W:";
            dParams.bootDrive = "S:";
            dParams.runBcdBoot = true;

            DeployEngine::DeployResult deployRes = DeployEngine::ApplyWimImage(dParams, [&](const DeployEngine::DeployProgress& prog) {
                std::cout << "\r[*] " << prog.step << ": " << (int)prog.percent << "% (남은시간: " << prog.remainingSeconds << "s)   " << std::flush;

                SimpleJson::JsonValue p = SimpleJson::JsonValue::CreateObject();
                p.Set("type", "CLIENT_PROGRESS");
                p.Set("status", "IMAGING");
                p.Set("step", prog.step);
                p.Set("percent", prog.percent);
                p.Set("speedMbps", prog.speedMbps);
                p.Set("elapsedSeconds", prog.elapsedSeconds);
                p.Set("remainingSeconds", prog.remainingSeconds);
                p.Set("detailMessage", prog.detail);
                client.SendMessage(p.ToString());
            });

            std::cout << "\n";

            // (3) 결과 보고
            SimpleJson::JsonValue finalResult = SimpleJson::JsonValue::CreateObject();
            finalResult.Set("type", "CLIENT_RESULT");
            if (deployRes.success) {
                std::cout << "[+] [성공] OS 이미지 더빙 및 부트로더 구성 완료! (소요시간: " << deployRes.durationSeconds << "초)\n";
                finalResult.Set("status", "SUCCESS");
                finalResult.Set("totalDurationSeconds", deployRes.durationSeconds);
                finalResult.Set("errorMessage", "");
            } else {
                std::cerr << "[-] [실패] 이미지 배포 실패: " << deployRes.errorMessage << "\n";
                finalResult.Set("status", "FAILED");
                finalResult.Set("totalDurationSeconds", deployRes.durationSeconds);
                finalResult.Set("errorMessage", deployRes.errorMessage);
            }
            client.SendMessage(finalResult.ToString());

            std::cout << "[*] 배포 결과 보고 완료. 시스템 종료 또는 대기 상태로 전환합니다.\n";
            break;
        }
    }

    std::cout << "[*] 클라이언트 세션 종료.\n";
    return 0;
}
