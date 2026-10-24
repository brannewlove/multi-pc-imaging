// ==========================================================================
// 다중 PC 동시 부팅 및 이미지 더빙 시뮬레이터 (Multi-PC Simulator)
// 다양한 기종(삼성, 레노버, 델, LG)의 PC가 동시에 PXE 부팅되어 마스터에 접속하고,
// 하드웨어 스펙 보고 -> 파티션 매칭 -> 실시간 WIM 이미지 더빙 진행률 -> 성공/실패 보고
// ==========================================================================

const net = require('net');

const MASTER_HOST = '127.0.0.1';
const MASTER_PORT = 39201;

// 다양한 테스트 PC 프로필 목록
const VIRTUAL_PCS = [
    {
        modelName: "Galaxy Book3 Pro 360 (NT960QFG)",
        manufacturer: "SAMSUNG ELECTRONICS CO., LTD.",
        serialNumber: "SN-SAM-960Q-9912",
        macAddress: "00:E0:4C:81:22:1A",
        ipAddress: "192.168.1.111",
        disks: [
            {
                diskIndex: 0,
                friendlyName: "Samsung SSD 990 PRO 1TB",
                busType: "NVMe",
                mediaType: "SSD",
                sizeGb: 953.8,
                sizeBytes: 1024209584128,
                partitionStyle: "GPT",
                partitions: [{ partitionNumber: 1, type: "GPT", sizeBytes: 1024209584128 }]
            }
        ],
        speedMb: 880,
        failRate: 0.0 // 항상 성공
    },
    {
        modelName: "ThinkPad T14 Gen 3 (21AH005EKR)",
        manufacturer: "LENOVO",
        serialNumber: "SN-LNV-T14G3-4410",
        macAddress: "E8:6A:64:1B:3C:90",
        ipAddress: "192.168.1.112",
        disks: [
            {
                diskIndex: 0,
                friendlyName: "KIOXIA 512GB PCIe NVMe SSD",
                busType: "NVMe",
                mediaType: "SSD",
                sizeGb: 476.9,
                sizeBytes: 512110190592,
                partitionStyle: "GPT",
                partitions: []
            }
        ],
        speedMb: 750,
        failRate: 0.0
    },
    {
        modelName: "Latitude 5430",
        manufacturer: "DELL Inc.",
        serialNumber: "SN-DLL-5430-8821",
        macAddress: "34:E6:D7:99:A1:02",
        ipAddress: "192.168.1.113",
        disks: [
            {
                diskIndex: 0,
                friendlyName: "Micron 256GB SATA SSD",
                busType: "SATA",
                mediaType: "SSD",
                sizeGb: 238.4,
                sizeBytes: 256060514304,
                partitionStyle: "MBR",
                partitions: []
            }
        ],
        speedMb: 480,
        failRate: 0.0
    },
    {
        modelName: "LG gram 16 (16Z90Q-GA76K)",
        manufacturer: "LG Electronics",
        serialNumber: "SN-LGE-16Z9-1144",
        macAddress: "A0:C5:89:4F:2E:8D",
        ipAddress: "192.168.1.114",
        disks: [
            {
                diskIndex: 0,
                friendlyName: "SK Hynix Platinum P41 1TB",
                busType: "NVMe",
                mediaType: "SSD",
                sizeGb: 953.8,
                sizeBytes: 1024209584128,
                partitionStyle: "GPT",
                partitions: []
            }
        ],
        speedMb: 920,
        failRate: 0.0
    }
];

function simulateClient(pcData, delayMs) {
    setTimeout(() => {
        console.log(`[*] [시뮬레이터] PC 부팅 시작: ${pcData.modelName} (MAC: ${pcData.macAddress})`);

        const socket = new net.Socket();
        socket.connect(MASTER_PORT, MASTER_HOST, () => {
            console.log(`[+] [시뮬레이터] 마스터 연결 성공: ${pcData.modelName}`);

            // 1. 하드웨어 인벤토리 등록
            const regMsg = {
                type: "CLIENT_REGISTER",
                payload: {
                    system: {
                        manufacturer: pcData.manufacturer,
                        modelName: pcData.modelName,
                        serialNumber: pcData.serialNumber,
                        uuid: "4C4C4544-0046-4E10-8054-C3C04F343232",
                        isUefi: true
                    },
                    network: {
                        primaryMac: pcData.macAddress,
                        ipAddress: pcData.ipAddress
                    },
                    disks: pcData.disks
                }
            };
            socket.write(JSON.stringify(regMsg) + '\n');
        });

        let currentProgress = 0;
        let progressInterval = null;

        socket.on('data', (data) => {
            const lines = data.toString('utf-8').trim().split('\n');
            for (const line of lines) {
                if (!line) continue;
                try {
                    const msg = JSON.parse(line);
                    if (msg.type === 'MASTER_APPROVE_DEPLOY') {
                        console.log(`[★] [시뮬레이터] 배포 승인 명령 수신됨: ${pcData.modelName} (타깃 디스크 0)`);
                        
                        // 파티셔닝 단계 (3초)
                        setTimeout(() => {
                            const p1 = {
                                type: "CLIENT_PROGRESS",
                                percent: 10,
                                step: "디스크 파티셔닝 및 포맷 완료 (1/3)",
                                speedMbps: 0,
                                elapsedSeconds: 3,
                                remainingSeconds: 25
                            };
                            socket.write(JSON.stringify(p1) + '\n');

                            // WIM 이미지 적용 진행률 루프
                            currentProgress = 10;
                            const totalSteps = 15;
                            let stepCount = 0;

                            progressInterval = setInterval(() => {
                                stepCount++;
                                currentProgress += (90 / totalSteps);
                                if (currentProgress >= 100) currentProgress = 99;

                                const progMsg = {
                                    type: "CLIENT_PROGRESS",
                                    percent: Math.min(99, Math.round(currentProgress)),
                                    step: `WIM 이미지 적용 중 (DISM Apply-Image ${Math.round(currentProgress)}%)`,
                                    speedMbps: pcData.speedMb,
                                    elapsedSeconds: 3 + stepCount,
                                    remainingSeconds: Math.max(1, totalSteps - stepCount)
                                };
                                socket.write(JSON.stringify(progMsg) + '\n');

                                if (stepCount >= totalSteps) {
                                    clearInterval(progressInterval);

                                    // 최종 성공 보고
                                    setTimeout(() => {
                                        const resMsg = {
                                            type: "CLIENT_RESULT",
                                            status: "SUCCESS",
                                            totalDurationSeconds: 3 + totalSteps + 2,
                                            errorMessage: ""
                                        };
                                        socket.write(JSON.stringify(resMsg) + '\n');
                                        console.log(`[✔] [시뮬레이터] 배포 성공 완료: ${pcData.modelName}`);
                                        
                                        setTimeout(() => socket.end(), 2000);
                                    }, 1500);
                                }
                            }, 1000);
                        }, 2000);
                    }
                } catch (e) {
                    console.error('[-] 수신 메시지 파싱 오류:', e.message);
                }
            }
        });

        socket.on('error', (err) => {
            console.error(`[-] [시뮬레이터 에러] ${pcData.modelName}:`, err.message);
        });

    }, delayMs);
}

// 4대의 PC를 시차를 두고 순차 부팅 시뮬레이션
console.log("=========================================================");
console.log("   다중 PC PXE 부팅 & 이미지 더빙 시뮬레이터 시작        ");
console.log("   (삼성, 레노버, 델, LG PC 4대가 순차 부팅됩니다)       ");
console.log("=========================================================\n");

VIRTUAL_PCS.forEach((pc, idx) => {
    simulateClient(pc, (idx + 1) * 2000); // 2초, 4초, 6초, 8초 간격으로 순차 접속
});
