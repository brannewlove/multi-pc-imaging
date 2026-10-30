// ==========================================================================
// UEFI 64-bit iPXE + HTTP 초고속 부팅 & 더빙 통합 시뮬레이터
// 사용법:
//   1. MultiDeployMaster.exe 실행 -> [🌐 PXE 서버] 클릭 -> 서버 시작 (예: 바인딩 IP 또는 127.0.0.1)
//   2. node simulate_pxe_boot.js [마스터IP]
// ==========================================================================

const dgram = require('dgram');
const http = require('http');
const net = require('net');

const MASTER_IP = process.argv[2] || '127.0.0.1';
const HTTP_PORT = 39202;
const AGENT_PORT = 39201;

const TARGET_PC = {
    manufacturer: 'SAMSUNG ELECTRONICS CO., LTD.',
    modelName: 'Galaxy Book4 Pro (NT960XGK)',
    serialNumber: 'SN-SAM-960XGK-2026',
    mac: '00:E0:4C:88:99:AA',
    macBytes: Buffer.from([0x00, 0xE0, 0x4C, 0x88, 0x99, 0xAA]),
    uuid: '4C4C4544-0056-4E10-8054-B1C04F343999'
};

function buildDhcpPacket(msgType, macBytes, vendorClass, userClass, arch) {
    const hdr = Buffer.alloc(240, 0);
    hdr[0] = 1; // BOOTREQUEST
    hdr[1] = 1; // Ethernet
    hdr[2] = 6; // MAC length
    hdr.writeUInt32BE(0x99887766, 4);
    hdr.writeUInt16BE(0x8000, 10);
    macBytes.copy(hdr, 28);
    hdr[236] = 99; hdr[237] = 130; hdr[238] = 83; hdr[239] = 99;

    const opts = [Buffer.from([53, 1, msgType])];
    if (vendorClass) {
        const vb = Buffer.from(vendorClass, 'ascii');
        opts.push(Buffer.from([60, vb.length]), vb);
    }
    if (userClass) {
        const ub = Buffer.from(userClass, 'ascii');
        opts.push(Buffer.from([77, ub.length]), ub);
    }
    if (typeof arch === 'number') {
        opts.push(Buffer.from([93, 2, (arch >> 8) & 0xff, arch & 0xff]));
    }
    opts.push(Buffer.from([0xff]));
    return Buffer.concat([hdr, ...opts]);
}

function httpGetText(url) {
    return new Promise((resolve, reject) => {
        http.get(url, (res) => {
            const chunks = [];
            res.on('data', (c) => chunks.push(c));
            res.on('end', () => resolve({ status: res.statusCode, body: Buffer.concat(chunks).toString('utf8') }));
        }).on('error', reject);
    });
}

function httpDownloadBytes(url) {
    return new Promise((resolve, reject) => {
        http.get(url, (res) => {
            let total = 0;
            res.on('data', (c) => { total += c.length; });
            res.on('end', () => resolve({ status: res.statusCode, length: total }));
        }).on('error', reject);
    });
}

async function runSimulation() {
    console.log('================================================================');
    console.log(` [PXE E2E 시뮬레이터] 마스터 서버(${MASTER_IP}) 대상으로 부팅 시뮬레이션 시작`);
    console.log('================================================================\n');

    // 1. HTTP /boot.ipxe 및 /start.ipxe 호출 (대시보드에 PXE 부팅 예비 세션 즉시 등록 확인)
    const startUrl = `http://${MASTER_IP}:${HTTP_PORT}/start.ipxe?mac=${encodeURIComponent(TARGET_PC.mac)}&ip=192.168.77.101&mfg=${encodeURIComponent(TARGET_PC.manufacturer)}&model=${encodeURIComponent(TARGET_PC.modelName)}&sn=${encodeURIComponent(TARGET_PC.serialNumber)}&uuid=${TARGET_PC.uuid}`;

    try {
        console.log('[1/4] UEFI iPXE 부트 스크립트 요청 (/start.ipxe)...');
        const startRes = await httpGetText(startUrl);
        console.log('      수신된 iPXE 스크립트 요약:\n' + startRes.body.split('\n').slice(0, 8).map(l => '        ' + l).join('\n'));

        console.log('\n[2/4] HTTP 초고속 부트로더 에셋 다운로드 테스트 (wimboot, BCD, boot.sdi, boot.wim)...');
        for (const f of ['wimboot', 'bootmgfw.efi', 'BCD', 'boot.sdi']) {
            const r = await httpDownloadBytes(`http://${MASTER_IP}:${HTTP_PORT}/pxe/${f}`);
            console.log(`      - /pxe/${f}: HTTP ${r.status} (${(r.length / 1024).toFixed(1)} KB)`);
        }
        const wimRes = await httpDownloadBytes(`http://${MASTER_IP}:${HTTP_PORT}/pxe/boot.wim?mac=${encodeURIComponent(TARGET_PC.mac)}`);
        console.log(`      - /pxe/boot.wim: HTTP ${wimRes.status} (${(wimRes.length / (1024 * 1024)).toFixed(2)} MB)`);
    } catch (e) {
        console.log(`[!] HTTP PXE 서버(${MASTER_IP}:${HTTP_PORT})가 아직 꺼져 있습니다. 마스터 UI에서 [🌐 PXE 서버]를 먼저 켜주세요! (${e.message})`);
    }

    // 3. 3초 대기 후(사용자가 대시보드에서 'PXE 부팅' 행을 볼 수 있도록) WinPE 에이전트 등록(세션 병합) 시뮬레이션
    console.log('\n[3/4] 3초 후 WinPE 부팅 완료 및 PeImagingAgent 접속(PXE 예비 세션 -> 승인 대기 자동 병합)...');
    await new Promise(r => setTimeout(r, 3000));

    const sock = new net.Socket();
    sock.connect(AGENT_PORT, MASTER_IP, () => {
        console.log('[+] WinPE 에이전트 TCP 연결 완료! 하드웨어 인벤토리 전송 (동일 MAC 세션 병합)...');
        const regMsg = {
            type: 'CLIENT_REGISTER',
            payload: {
                system: {
                    manufacturer: TARGET_PC.manufacturer,
                    modelName: TARGET_PC.modelName,
                    serialNumber: TARGET_PC.serialNumber,
                    uuid: TARGET_PC.uuid,
                    isUefi: true
                },
                network: {
                    primaryMac: TARGET_PC.mac,
                    ipAddress: '192.168.77.101'
                },
                disks: [
                    {
                        diskIndex: 0,
                        friendlyName: 'Samsung PM9A1 NVMe 1TB',
                        busType: 'NVMe',
                        sizeGb: 953.8,
                        sizeBytes: 1024209584128,
                        partitionStyle: 'GPT',
                        partitions: []
                    }
                ]
            }
        };
        sock.write(JSON.stringify(regMsg) + '\n');
        console.log('[4/4] 마스터 대시보드에서 [배포 승인]을 클릭하면 더빙 완료 및 재부팅 우회 보호까지 테스트됩니다.');
    });

    sock.on('data', (data) => {
        const lines = data.toString('utf8').trim().split('\n');
        for (const line of lines) {
            if (!line) continue;
            const msg = JSON.parse(line);
            if (msg.type === 'MASTER_APPROVE_DEPLOY') {
                console.log('[★] 배포 승인 수신! 고속 더빙 진행 시뮬레이션...');
                let pct = 20;
                const timer = setInterval(() => {
                    sock.write(JSON.stringify({
                        type: 'CLIENT_PROGRESS',
                        percent: pct,
                        step: `WIM 이미지 적용 중 (${pct}%)`,
                        speedMbps: 950,
                        elapsedSeconds: Math.round(pct / 20),
                        remainingSeconds: Math.round((100 - pct) / 20)
                    }) + '\n');
                    pct += 25;
                    if (pct > 100) {
                        clearInterval(timer);
                        sock.write(JSON.stringify({
                            type: 'CLIENT_RESULT',
                            status: 'SUCCESS',
                            totalDurationSeconds: 6,
                            errorMessage: ''
                        }) + '\n');
                        console.log('[✔] 배포 성공 보고 완료! 이제 재부팅 시 PXE Boot Loop Guard가 작동합니다.');
                        setTimeout(() => sock.end(), 1500);
                    }
                }, 800);
            }
        }
    });
}

runSimulation();

