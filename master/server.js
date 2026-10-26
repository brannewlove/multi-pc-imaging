const http = require('http');
const net = require('net');
const dgram = require('dgram');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

// 설정 상수
const PORT_UDP_DISCOVERY = 39200;
const PORT_TCP_CLIENT = 39201;
const PORT_HTTP_WEB = 39202;

const PATH_POLICIES = path.join(__dirname, 'config', 'partition_policies.json');
const PATH_IMAGES = path.join(__dirname, 'config', 'image_catalog.json');
const PATH_HISTORY = path.join(__dirname, 'config', 'history.json');
const PATH_SERVER_CONFIG = path.join(__dirname, 'config', 'server_config.json');
const PUBLIC_DIR = path.join(__dirname, 'public');

// 메모리 상태 관리
let partitionPolicies = [];
let imageCatalog = [];
let deployHistory = [];
let globalAutoApprove = true; // 전역 자동 승인 모드 (기본 ON)

let serverConfig = {
    masterImageDir: "C:\\images",
    networkSharePath: "\\\\127.0.0.1\\images",
    clientAccessMethod: "LOCAL_OR_SHARE",
    lastScannedAt: new Date().toISOString()
};

// 연결된 WinPE 클라이언트 세션 목록: Map<sessionId, ClientSession>
const connectedClients = new Map();

// 웹소켓 대시보드 클라이언트 목록
const webSocketClients = new Set();

// 1. 설정 로드 및 저장
function loadConfigs() {
    try {
        if (fs.existsSync(PATH_SERVER_CONFIG)) {
            serverConfig = { ...serverConfig, ...JSON.parse(fs.readFileSync(PATH_SERVER_CONFIG, 'utf-8')) };
        }
    } catch (e) {
        console.error('[-] 서버 설정 로드 오류:', e.message);
    }

    try {
        if (fs.existsSync(PATH_POLICIES)) {
            partitionPolicies = JSON.parse(fs.readFileSync(PATH_POLICIES, 'utf-8'));
        }
    } catch (e) {
        console.error('[-] 파티션 정책 로드 오류:', e.message);
    }

    try {
        if (fs.existsSync(PATH_IMAGES)) {
            imageCatalog = JSON.parse(fs.readFileSync(PATH_IMAGES, 'utf-8'));
        }
    } catch (e) {
        console.error('[-] 이미지 카탈로그 로드 오류:', e.message);
    }

    try {
        if (fs.existsSync(PATH_HISTORY)) {
            deployHistory = JSON.parse(fs.readFileSync(PATH_HISTORY, 'utf-8'));
        }
    } catch (e) {
        deployHistory = [];
    }
}
loadConfigs();

function saveServerConfig() {
    try {
        fs.writeFileSync(PATH_SERVER_CONFIG, JSON.stringify(serverConfig, null, 2), 'utf-8');
    } catch (e) {
        console.error('[-] 서버 설정 저장 오류:', e.message);
    }
}

function saveImageCatalog() {
    try {
        fs.writeFileSync(PATH_IMAGES, JSON.stringify(imageCatalog, null, 2), 'utf-8');
    } catch (e) {
        console.error('[-] 이미지 카탈로그 저장 오류:', e.message);
    }
}

function saveHistory() {
    try {
        fs.writeFileSync(PATH_HISTORY, JSON.stringify(deployHistory, null, 2), 'utf-8');
    } catch (e) {
        console.error('[-] 이력 저장 오류:', e.message);
    }
}

// 2. 마스터 이미지 폴더 파일 탐색 및 스캔
function scanMasterImageFolder(dirPath) {
    const targetDir = dirPath || serverConfig.masterImageDir;
    const result = {
        folderPath: targetDir,
        exists: false,
        accessible: false,
        error: null,
        totalFiles: 0,
        totalSizeGb: "0.00",
        files: []
    };

    if (!targetDir) {
        result.error = '이미지 폴더 경로가 지정되지 않았습니다.';
        return result;
    }

    if (!fs.existsSync(targetDir)) {
        result.error = `지정된 경로(${targetDir})가 존재하지 않습니다.`;
        return result;
    }

    try {
        result.exists = true;
        const entries = fs.readdirSync(targetDir, { withFileTypes: true });
        result.accessible = true;

        let totalBytes = 0;
        for (const entry of entries) {
            if (entry.isFile()) {
                const ext = path.extname(entry.name).toLowerCase();
                if (ext === '.wim' || ext === '.esd' || ext === '.swm') {
                    const fullPath = path.join(targetDir, entry.name);
                    try {
                        const stat = fs.statSync(fullPath);
                        totalBytes += stat.size;
                        result.files.push({
                            fileName: entry.name,
                            fullPath: fullPath,
                            sizeBytes: stat.size,
                            sizeGb: (stat.size / (1024 * 1024 * 1024)).toFixed(2),
                            modifiedAt: stat.mtime.toISOString(),
                            extension: ext.substring(1).toUpperCase()
                        });
                    } catch (statErr) {}
                }
            }
        }

        result.totalFiles = result.files.length;
        result.totalSizeGb = (totalBytes / (1024 * 1024 * 1024)).toFixed(2);
        return result;
    } catch (e) {
        result.error = `폴더 읽기 권한 오류: ${e.message}`;
        return result;
    }
}

// 3. 파티션 및 이미지 선택 규칙 매칭 엔진
function matchPartitionPolicy(client) {
    if (!client.disks || client.disks.length === 0) {
        return partitionPolicies[0] || null;
    }

    const diskCount = client.disks.length;
    const targetDisk = client.disks[0]; // 기본 타깃 디스크 0
    const targetCapacityGb = targetDisk.sizeGb || (targetDisk.sizeBytes ? (targetDisk.sizeBytes / (1024 * 1024 * 1024)) : 0);

    for (const policy of partitionPolicies) {
        const cond = policy.conditions;
        if (!cond) continue;

        // 디스크 수량 조건 체크
        if (cond.diskCountMin && diskCount < cond.diskCountMin) continue;
        if (cond.diskCountMax && diskCount > cond.diskCountMax) continue;

        // 용량 범위 조건 체크
        if (cond.minCapacityGb && targetCapacityGb < cond.minCapacityGb) continue;
        if (cond.maxCapacityGb && targetCapacityGb > cond.maxCapacityGb) continue;

        // 모델명 정규식 체크 (예: * or NT950*)
        if (cond.modelPattern && cond.modelPattern !== '*') {
            const pattern = new RegExp(cond.modelPattern.replace(/\*/g, '.*'), 'i');
            if (!pattern.test(client.system.modelName || '')) continue;
        }

        return policy;
    }

    // 일치하는 규칙이 없으면 첫 번째 기본 정책 매칭
    return partitionPolicies[0] || null;
}

function matchImageCatalog(client) {
    const model = client.system?.modelName || '';
    const mfg = client.system?.manufacturer || '';
    const targetDisk = (client.disks && client.disks.length > 0) ? client.disks[0] : null;
    const diskGb = targetDisk ? (targetDisk.sizeGb || (targetDisk.sizeBytes / (1024 * 1024 * 1024))) : 0;
    const diskBus = targetDisk ? (targetDisk.busType || '') : '';

    // 활성화된 규칙을 우선순위(priority 오름차순) 정렬
    const sortedRules = [...imageCatalog]
        .filter(r => r.enabled !== false)
        .sort((a, b) => (a.priority || 999) - (b.priority || 999));

    // 1차 패스: 일반 조건 규칙 평가 (fallback 제외)
    for (const rule of sortedRules) {
        if (rule.isFallback) continue;

        const cond = rule.conditions || {};

        // 1. 제조사 매칭
        if (cond.manufacturer && cond.manufacturer !== '*') {
            const parts = cond.manufacturer.split('|').map(s => s.trim());
            const matched = parts.some(p => {
                const reg = new RegExp('^' + p.replace(/\*/g, '.*') + '$', 'i');
                return reg.test(mfg);
            });
            if (!matched) continue;
        }

        // 2. 모델명 매칭
        if (cond.modelPattern && cond.modelPattern !== '*') {
            const parts = cond.modelPattern.split('|').map(s => s.trim());
            const matched = parts.some(p => {
                const reg = new RegExp(p.replace(/\*/g, '.*'), 'i');
                return reg.test(model);
            });
            if (!matched) continue;
        }

        // 3. 디스크 용량 범위 매칭
        if (cond.minDiskGb && diskGb > 0 && diskGb < cond.minDiskGb) continue;
        if (cond.maxDiskGb && diskGb > 0 && diskGb > cond.maxDiskGb) continue;

        // 4. 버스 타입 매칭
        if (cond.targetDiskType && cond.targetDiskType !== 'ANY') {
            if (diskBus && !diskBus.toLowerCase().includes(cond.targetDiskType.toLowerCase())) {
                continue;
            }
        }

        return rule;
    }

    // 2차 패스: 일치하는 규칙이 없을 경우 Fallback 기본 규칙 사용
    const fallbackRule = sortedRules.find(r => r.isFallback) || sortedRules[sortedRules.length - 1];
    return fallbackRule || null;
}

// 연결된 모든 READY 상태 클라이언트의 매칭 규칙 재계산
function reMatchConnectedClients() {
    for (const [id, session] of connectedClients.entries()) {
        if (session.status === 'READY' || session.status === 'CONNECTED') {
            session.matchedPolicy = matchPartitionPolicy(session);
            session.matchedImage = matchImageCatalog(session);
            broadcastToDashboard('CLIENT_STATUS_CHANGE', { sessionId: id, client: getClientSummary(session) });
        }
    }
}

// 스마트 지능형 동적 파티션 스크립트 생성 엔진 (사용자 지정 조건부 분할)
function generateDynamicSmartPartitionCommands(clientDisks = []) {
    const cmds = [];

    // 디스크 목록이 없거나 비어있는 경우 Disk 0 단일 디스크로 대체
    if (!clientDisks || clientDisks.length === 0) {
        return [
            'select disk 0',
            'clean',
            'convert gpt',
            'create partition efi size=100',
            'format quick fs=fat32 label="System"',
            'assign letter="S"',
            'create partition msr size=16',
            'create partition primary',
            'format quick fs=ntfs label="Windows11"',
            'assign letter="W"'
        ];
    }

    if (clientDisks.length === 1) {
        // [조건 1] 디스크 1개 인식
        const disk = clientDisks[0];
        const diskIndex = disk.diskIndex ?? 0;
        const sizeGb = disk.sizeGb || (disk.sizeBytes ? (disk.sizeBytes / (1024 * 1024 * 1024)) : 0);
        const sizeMb = disk.sizeBytes ? Math.floor(disk.sizeBytes / (1024 * 1024)) : Math.floor(sizeGb * 1024);

        cmds.push(`select disk ${diskIndex}`);
        cmds.push('clean');
        cmds.push('convert gpt');
        cmds.push('create partition efi size=100');
        cmds.push('format quick fs=fat32 label="System"');
        cmds.push('assign letter="S"');
        cmds.push('create partition msr size=16');

        if (sizeGb > 512) {
            // (1-A) 크기 512GB 초과: 300GB "Windows11" 라벨 후 설치, 나머지 "Data" 라벨
            cmds.push('create partition primary size=307200'); // 300GB = 307,200 MB
            cmds.push('format quick fs=ntfs label="Windows11"');
            cmds.push('assign letter="W"');
            cmds.push('create partition primary');
            cmds.push('format quick fs=ntfs label="Data"');
            cmds.push('assign letter="D"');
        } else {
            // (1-B) 크기 512GB 이하: 50%로 나눈 뒤 "Windows11" 라벨 후 설치, 나머지 "Data" 라벨
            const usableMb = Math.max(1024, sizeMb - 120); // EFI(100MB)+MSR(16MB) 제외 잔여 공간
            const halfMb = Math.floor(usableMb / 2);
            cmds.push(`create partition primary size=${halfMb}`);
            cmds.push('format quick fs=ntfs label="Windows11"');
            cmds.push('assign letter="W"');
            cmds.push('create partition primary');
            cmds.push('format quick fs=ntfs label="Data"');
            cmds.push('assign letter="D"');
        }
    } else {
        // [조건 2] 디스크 2개 이상 인식
        // 작은 용량 디스크 우선 정렬, 용량이 동일한 경우 1번 디스크(diskIndex 낮은 순) 우선
        const sorted = [...clientDisks].sort((a, b) => {
            const sizeA = a.sizeBytes || ((a.sizeGb || 0) * 1024 * 1024 * 1024);
            const sizeB = b.sizeBytes || ((b.sizeGb || 0) * 1024 * 1024 * 1024);
            // 2GB 이상 차이나면 작은 용량 디스크 선정
            if (Math.abs(sizeA - sizeB) > 2 * 1024 * 1024 * 1024) {
                return sizeA - sizeB;
            }
            // 동일 크기 디스크인 경우 1번 디스크(낮은 diskIndex) 선정
            return (a.diskIndex ?? 0) - (b.diskIndex ?? 0);
        });

        const osDisk = sorted[0];
        const dataDisks = sorted.slice(1);

        // 1. OS 디스크: 전체 GPT 포맷, EFI, MSR 생성 후 전체를 "Windows11" 라벨로 포맷
        cmds.push(`select disk ${osDisk.diskIndex ?? 0}`);
        cmds.push('clean');
        cmds.push('convert gpt');
        cmds.push('create partition efi size=100');
        cmds.push('format quick fs=fat32 label="System"');
        cmds.push('assign letter="S"');
        cmds.push('create partition msr size=16');
        cmds.push('create partition primary');
        cmds.push('format quick fs=ntfs label="Windows11"');
        cmds.push('assign letter="W"');

        // 2. 나머지 디스크: 전부 GPT 포맷, 1개면 "Data", 2개 이상이면 "Data1", "Data2" 형식
        dataDisks.forEach((dDisk, idx) => {
            const label = dataDisks.length === 1 ? 'Data' : `Data${idx + 1}`;
            cmds.push(`select disk ${dDisk.diskIndex}`);
            cmds.push('clean');
            cmds.push('convert gpt');
            cmds.push('create partition primary');
            cmds.push(`format quick fs=ntfs label="${label}"`);
            if (idx === 0) {
                cmds.push('assign letter="D"');
            } else {
                cmds.push('assign');
            }
        });
    }

    return cmds;
}

function generateDiskpartCommands(policy, clientDisks = [], targetDiskIndex = 0) {
    // 1. 정책이 스마트 동적 정책이거나, 지능형 모드인 경우
    if (policy.isSmartDynamic || policy.id === 'policy-smart-auto-gpt' || !policy.layout && (!policy.diskpartCommands || policy.diskpartCommands.length === 0)) {
        return generateDynamicSmartPartitionCommands(clientDisks);
    }

    // 2. 관리자가 직접 지정한 Diskpart 명령이 있는 경우
    if (policy.diskpartCommands && Array.isArray(policy.diskpartCommands) && policy.diskpartCommands.length > 0) {
        return policy.diskpartCommands;
    }

    // 3. 기존 레이아웃 배열이 있는 경우
    if (policy.layout && Array.isArray(policy.layout)) {
        const cmds = [
            `select disk ${targetDiskIndex}`,
            'clean'
        ];

        if (policy.partitionScheme === 'GPT') {
            cmds.push('convert gpt');
        } else {
            cmds.push('convert mbr');
        }

        for (const item of policy.layout) {
            if (item.type === 'EFI') {
                cmds.push(`create partition efi size=${item.sizeMb || 300}`);
                cmds.push(`format quick fs=${item.fs.toLowerCase()} label="${item.label || 'System'}"`);
                if (item.letter) cmds.push(`assign letter=${item.letter}`);
            } else if (item.type === 'MSR') {
                cmds.push(`create partition msr size=${item.sizeMb || 16}`);
            } else if (item.type === 'OS') {
                if (item.sizeMb === 'REMAINDER') {
                    cmds.push('create partition primary');
                } else {
                    cmds.push(`create partition primary size=${item.sizeMb}`);
                }
                cmds.push(`format quick fs=${item.fs.toLowerCase()} label="${item.label || 'Windows'}"`);
                if (item.letter) cmds.push(`assign letter=${item.letter}`);
            } else if (item.type === 'DATA') {
                if (item.sizeMb === 'REMAINDER') {
                    cmds.push('create partition primary');
                } else {
                    cmds.push(`create partition primary size=${item.sizeMb}`);
                }
                cmds.push(`format quick fs=${item.fs.toLowerCase()} label="${item.label || 'Data'}"`);
                if (item.letter) cmds.push(`assign letter=${item.letter}`);
            }
        }
        return cmds;
    }

    return generateDynamicSmartPartitionCommands(clientDisks);
}

// 4. 브라우저 대시보드 브로드캐스트
function broadcastToDashboard(eventType, data) {
    const message = JSON.stringify({ event: eventType, data });
    for (const ws of webSocketClients) {
        try {
            sendWsFrame(ws, message);
        } catch (e) {
            webSocketClients.delete(ws);
        }
    }
}

function getDashboardState() {
    const clients = [];
    for (const [id, c] of connectedClients.entries()) {
        clients.push(getClientSummary(c));
    }

    const scanResult = scanMasterImageFolder(serverConfig.masterImageDir);

    return {
        clients,
        partitionPolicies,
        imageCatalog,
        serverConfig,
        scannedImages: scanResult,
        deployHistory: deployHistory.slice(-50).reverse(), // 최근 50건
        globalAutoApprove
    };
}

// 5. 배포 승인 처리
function approveClientDeploy(sessionId, customImage = null) {
    const client = connectedClients.get(sessionId);
    if (!client || !client.socket) return false;
    if (client.status === 'IMAGING' || client.status === 'SUCCESS') return false;

    const policy = client.matchedPolicy || partitionPolicies[0];
    const imageRule = customImage || client.matchedImage || imageCatalog[0];
    const cmds = generateDiskpartCommands(policy, client.disks, 0);

    const targetFileName = imageRule?.targetImage?.imageFileName || imageRule?.imageFileName || 'Win11_Universal.wim';
    const targetIndex = imageRule?.targetImage?.imageIndex || imageRule?.imageIndex || 1;
    
    // 로컬 디렉터리 기반 경로 및 네트워크 공유/HTTP 경로 구성
    const localImagePath = path.join(serverConfig.masterImageDir, targetFileName);
    const networkImagePath = path.join(serverConfig.networkSharePath, targetFileName);

    const deployPayload = {
        type: 'MASTER_APPROVE_DEPLOY',
        payload: {
            sessionId,
            targetDiskIndex: 0,
            partitionPolicyId: policy.id,
            partitionCommands: cmds,
            imageInfo: {
                ruleId: imageRule.id,
                ruleName: imageRule.name,
                imageName: targetFileName,
                imageIndex: targetIndex,
                imageLocalPath: localImagePath,
                imageNetworkPath: networkImagePath,
                sourceType: serverConfig.clientAccessMethod || 'LOCAL_OR_SHARE',
                sourceUrl: `http://127.0.0.1:${PORT_HTTP_WEB}/images/${encodeURIComponent(targetFileName)}`,
                targetDriveLetter: 'W:',
                bootDriveLetter: 'S:'
            },
            postAction: 'REBOOT'
        }
    };

    client.status = 'IMAGING';
    client.progress = {
        percent: 0,
        step: '배포 명령 승인됨 (작업 준비 중)',
        speedMbps: 0,
        elapsedSeconds: 0,
        remainingSeconds: 0
    };

    client.socket.write(JSON.stringify(deployPayload) + '\n');
    broadcastToDashboard('CLIENT_STATUS_CHANGE', { sessionId, client: getClientSummary(client) });
    return true;
}

function getClientSummary(c) {
    return {
        sessionId: c.sessionId,
        connectedAt: c.connectedAt,
        ip: c.ip,
        status: c.status,
        system: c.system,
        network: c.network,
        disks: c.disks,
        matchedPolicy: c.matchedPolicy,
        matchedImage: c.matchedImage,
        progress: c.progress,
        result: c.result,
        logs: c.logs
    };
}

// 6. WinPE Agent TCP 서버 (Port 39201)
const tcpServer = net.createServer((socket) => {
    const sessionId = 'PC-' + crypto.randomBytes(4).toString('hex').toUpperCase();
    const clientIp = socket.remoteAddress.replace(/^.*:/, '');

    console.log(`[+] 클라이언트 접속: [${sessionId}] IP=${clientIp}`);

    const clientSession = {
        sessionId,
        socket,
        ip: clientIp,
        connectedAt: new Date().toISOString(),
        status: 'CONNECTED', // CONNECTED, READY, IMAGING, SUCCESS, FAILED
        system: {},
        network: {},
        disks: [],
        matchedPolicy: null,
        matchedImage: null,
        progress: null,
        result: null,
        logs: []
    };
    connectedClients.set(sessionId, clientSession);

    let buffer = '';
    socket.on('data', (chunk) => {
        buffer += chunk.toString('utf-8');
        let boundary = buffer.indexOf('\n');
        while (boundary !== -1) {
            const rawMsg = buffer.substring(0, boundary).trim();
            buffer = buffer.substring(boundary + 1);
            boundary = buffer.indexOf('\n');

            if (rawMsg) {
                try {
                    const msg = JSON.parse(rawMsg);
                    handleClientMessage(clientSession, msg);
                } catch (e) {
                    console.error('[-] 클라이언트 메시지 파싱 에러:', e.message);
                }
            }
        }
    });

    socket.on('close', () => {
        console.log(`[-] 클라이언트 연결 종료: [${sessionId}]`);
        connectedClients.delete(sessionId);
        broadcastToDashboard('CLIENT_DISCONNECTED', { sessionId });
    });

    socket.on('error', (err) => {
        console.error(`[-] 소켓 에러 [${sessionId}]:`, err.message);
    });
});

function handleClientMessage(session, msg) {
    if (msg.type === 'CLIENT_REGISTER') {
        const payload = msg.payload || {};
        session.system = payload.system || {};
        session.network = payload.network || {};
        session.disks = payload.disks || [];
        session.status = 'READY';

        // 정책 및 이미지 자동 매칭
        session.matchedPolicy = matchPartitionPolicy(session);
        session.matchedImage = matchImageCatalog(session);

        console.log(`[*] 클라이언트 등록 완료: [${session.sessionId}] 모델=${session.system.modelName}, 시리얼=${session.system.serialNumber}`);
        console.log(`    -> 매칭 정책: ${session.matchedPolicy?.name || '없음'}, 자동승인: ${session.matchedPolicy?.autoApprove}`);
        console.log(`    -> 매칭 이미지: ${session.matchedImage?.name || '없음'} (${session.matchedImage?.targetImage?.imageFileName || session.matchedImage?.imageFileName})`);

        broadcastToDashboard('CLIENT_REGISTERED', { sessionId: session.sessionId, client: getClientSummary(session) });

        // 자동 승인 조건 검사 (전역 설정이 켜져 있고 해당 정책도 autoApprove일 때)
        if (globalAutoApprove && session.matchedPolicy && session.matchedPolicy.autoApprove) {
            console.log(`[★] 자동 승인 조건 충족: [${session.sessionId}] 1초 후 자동 배포를 시작합니다.`);
            setTimeout(() => {
                approveClientDeploy(session.sessionId);
            }, 1000);
        }
    } else if (msg.type === 'CLIENT_PROGRESS') {
        session.status = 'IMAGING';
        session.progress = {
            percent: msg.percent || 0,
            step: msg.step || '배포 진행 중',
            speedMbps: msg.speedMbps || 0,
            elapsedSeconds: msg.elapsedSeconds || 0,
            remainingSeconds: msg.remainingSeconds || 0,
            detail: msg.detailMessage || ''
        };
        broadcastToDashboard('CLIENT_PROGRESS', { sessionId: session.sessionId, progress: session.progress });
    } else if (msg.type === 'CLIENT_RESULT') {
        const isSuccess = (msg.status === 'SUCCESS');
        session.status = isSuccess ? 'SUCCESS' : 'FAILED';
        session.result = {
            status: session.status,
            totalDurationSeconds: msg.totalDurationSeconds || 0,
            errorMessage: msg.errorMessage || '',
            completedAt: new Date().toISOString()
        };

        // 배포 이력에 추가
        const imgName = session.matchedImage?.targetImage?.imageFileName || session.matchedImage?.imageFileName || 'Unknown';
        const historyItem = {
            id: 'HIST-' + Date.now(),
            sessionId: session.sessionId,
            modelName: session.system.modelName,
            serialNumber: session.system.serialNumber,
            macAddress: session.network.primaryMac,
            policyName: session.matchedPolicy?.name,
            imageName: imgName,
            status: session.status,
            durationSeconds: session.result.totalDurationSeconds,
            errorMessage: session.result.errorMessage,
            completedAt: session.result.completedAt
        };
        deployHistory.push(historyItem);
        saveHistory();

        console.log(`[✔] 배포 완료 [${session.sessionId}]: 상태=${session.status}, 소요시간=${session.result.totalDurationSeconds}초`);
        broadcastToDashboard('CLIENT_COMPLETED', { sessionId: session.sessionId, result: session.result, historyItem });
    }
}

tcpServer.listen(PORT_TCP_CLIENT, '0.0.0.0', () => {
    console.log(`[+] WinPE Agent TCP 서버 구동: 0.0.0.0:${PORT_TCP_CLIENT}`);
});

// 7. UDP 자동 탐색 서버 (Port 39200)
const udpServer = dgram.createSocket('udp4');
udpServer.on('message', (msg, rinfo) => {
    try {
        const parsed = JSON.parse(msg.toString('utf-8'));
        if (parsed.type === 'CLIENT_DISCOVERY') {
            console.log(`[*] UDP 클라이언트 탐색 수신: ${rinfo.address}:${rinfo.port}`);
            const ackMsg = JSON.stringify({
                type: 'MASTER_DISCOVERY_ACK',
                masterIp: rinfo.address, // 응답 주소
                tcpPort: PORT_TCP_CLIENT,
                httpPort: PORT_HTTP_WEB
            }) + '\n';
            udpServer.send(ackMsg, rinfo.port, rinfo.address);
        }
    } catch (e) {}
});
udpServer.bind(PORT_UDP_DISCOVERY, '0.0.0.0', () => {
    console.log(`[+] UDP 브로드캐스트 탐색 서버 구동: 0.0.0.0:${PORT_UDP_DISCOVERY}`);
});

// 8. 웹 대시보드 HTTP 서버 & API & WIM 스트리밍 서빙
const httpServer = http.createServer((req, res) => {
    const url = new URL(req.url, `http://${req.headers.host}`);

    // CORS 및 공통 헤더
    res.setHeader('Access-Control-Allow-Origin', '*');
    res.setHeader('Access-Control-Allow-Methods', 'GET, POST, OPTIONS');
    res.setHeader('Access-Control-Allow-Headers', 'Content-Type, Range');

    if (req.method === 'OPTIONS') {
        res.writeHead(204);
        res.end();
        return;
    }

    // -------------------------------------------------------------
    // REST API 라우팅
    // -------------------------------------------------------------
    
    // (1) 전체 상태 조회
    if (url.pathname === '/api/state') {
        res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
        res.end(JSON.stringify(getDashboardState()));
        return;
    }

    // (2) 마스터 이미지 폴더 설정 및 스캔 API
    if (url.pathname === '/api/images/folder' && req.method === 'GET') {
        const scanRes = scanMasterImageFolder(serverConfig.masterImageDir);
        res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
        res.end(JSON.stringify({ success: true, serverConfig, scan: scanRes }));
        return;
    }

    if (url.pathname === '/api/images/folder' && req.method === 'POST') {
        let body = '';
        req.on('data', chunk => body += chunk);
        req.on('end', () => {
            try {
                const data = JSON.parse(body);
                if (data.masterImageDir) {
                    serverConfig.masterImageDir = data.masterImageDir.trim();
                }
                if (data.networkSharePath) {
                    serverConfig.networkSharePath = data.networkSharePath.trim();
                }
                if (data.clientAccessMethod) {
                    serverConfig.clientAccessMethod = data.clientAccessMethod;
                }
                serverConfig.lastScannedAt = new Date().toISOString();
                saveServerConfig();

                const scanRes = scanMasterImageFolder(serverConfig.masterImageDir);
                broadcastToDashboard('SERVER_CONFIG_UPDATED', { serverConfig, scan: scanRes });

                res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
                res.end(JSON.stringify({ success: true, serverConfig, scan: scanRes }));
            } catch (e) {
                res.writeHead(400, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify({ error: e.message }));
            }
        });
        return;
    }

    // (3) 이미지 선택 규칙 (Catalog/Rules) 조회 및 저장
    if (url.pathname === '/api/images/rules' && req.method === 'GET') {
        res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
        res.end(JSON.stringify({ success: true, rules: imageCatalog }));
        return;
    }

    if (url.pathname === '/api/images/rules' && req.method === 'POST') {
        let body = '';
        req.on('data', chunk => body += chunk);
        req.on('end', () => {
            try {
                const payload = JSON.parse(body);
                if (Array.isArray(payload)) {
                    imageCatalog = payload;
                } else if (payload.rules && Array.isArray(payload.rules)) {
                    imageCatalog = payload.rules;
                }
                saveImageCatalog();
                reMatchConnectedClients();
                broadcastToDashboard('IMAGE_RULES_UPDATED', { imageCatalog });

                res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
                res.end(JSON.stringify({ success: true, rules: imageCatalog }));
            } catch (e) {
                res.writeHead(400, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify({ error: e.message }));
            }
        });
        return;
    }

    // (4) 선택 규칙 실시간 매칭 테스트 (시뮬레이션)
    if (url.pathname === '/api/images/test-match' && req.method === 'POST') {
        let body = '';
        req.on('data', chunk => body += chunk);
        req.on('end', () => {
            try {
                const testSpec = JSON.parse(body);
                const matched = matchImageCatalog({
                    system: {
                        modelName: testSpec.modelName || '',
                        manufacturer: testSpec.manufacturer || ''
                    },
                    disks: [{
                        sizeGb: Number(testSpec.diskGb) || 512,
                        busType: testSpec.busType || 'NVMe'
                    }]
                });
                res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
                res.end(JSON.stringify({ success: true, matchedRule: matched }));
            } catch (e) {
                res.writeHead(400, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify({ error: e.message }));
            }
        });
        return;
    }

    // (5) 특정 클라이언트의 배포 이미지 수동 변경
    if (url.pathname === '/api/client/set-image' && req.method === 'POST') {
        let body = '';
        req.on('data', chunk => body += chunk);
        req.on('end', () => {
            try {
                const { sessionId, ruleId } = JSON.parse(body);
                const client = connectedClients.get(sessionId);
                if (!client) {
                    res.writeHead(404, { 'Content-Type': 'application/json' });
                    res.end(JSON.stringify({ error: '클라이언트를 찾을 수 없습니다.' }));
                    return;
                }
                const rule = imageCatalog.find(r => r.id === ruleId);
                if (rule) {
                    client.matchedImage = rule;
                    broadcastToDashboard('CLIENT_STATUS_CHANGE', { sessionId, client: getClientSummary(client) });
                    res.writeHead(200, { 'Content-Type': 'application/json' });
                    res.end(JSON.stringify({ success: true, matchedImage: rule }));
                } else {
                    res.writeHead(400, { 'Content-Type': 'application/json' });
                    res.end(JSON.stringify({ error: '해당 규칙을 찾을 수 없습니다.' }));
                }
            } catch (e) {
                res.writeHead(400, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify({ error: e.message }));
            }
        });
        return;
    }

    // (6) 클라이언트 개별 배포 승인
    if (url.pathname === '/api/approve' && req.method === 'POST') {
        let body = '';
        req.on('data', chunk => body += chunk);
        req.on('end', () => {
            try {
                const data = JSON.parse(body);
                const ok = approveClientDeploy(data.sessionId);
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify({ success: ok }));
            } catch (e) {
                res.writeHead(400);
                res.end(JSON.stringify({ error: e.message }));
            }
        });
        return;
    }

    // (7) 대기 중 PC 일괄 승인
    if (url.pathname === '/api/batch-approve' && req.method === 'POST') {
        let count = 0;
        for (const [id, c] of connectedClients.entries()) {
            if (c.status === 'READY') {
                if (approveClientDeploy(id)) count++;
            }
        }
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ success: true, count }));
        return;
    }

    // (8) 전역 자동 승인 모드 토글
    if (url.pathname === '/api/toggle-auto-approve' && req.method === 'POST') {
        globalAutoApprove = !globalAutoApprove;
        broadcastToDashboard('GLOBAL_CONFIG_CHANGE', { globalAutoApprove });
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ success: true, globalAutoApprove }));
        return;
    }

    // (9) 파티션 정책 저장 (단일 또는 전체)
    if (url.pathname === '/api/policies' && req.method === 'POST') {
        let body = '';
        req.on('data', chunk => body += chunk);
        req.on('end', () => {
            try {
                const payload = JSON.parse(body);
                if (Array.isArray(payload)) {
                    partitionPolicies = payload;
                } else if (payload.policies && Array.isArray(payload.policies)) {
                    partitionPolicies = payload.policies;
                }
                fs.writeFileSync(PATH_POLICIES, JSON.stringify(partitionPolicies, null, 2), 'utf-8');
                reMatchConnectedClients();
                broadcastToDashboard('POLICIES_UPDATED', { partitionPolicies });
                res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
                res.end(JSON.stringify({ success: true, policies: partitionPolicies }));
            } catch (e) {
                res.writeHead(400, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify({ error: e.message }));
            }
        });
        return;
    }

    if (url.pathname === '/api/policy/save' && req.method === 'POST') {
        let body = '';
        req.on('data', chunk => body += chunk);
        req.on('end', () => {
            try {
                const newPolicy = JSON.parse(body);
                const existingIdx = partitionPolicies.findIndex(p => p.id === newPolicy.id);
                if (existingIdx >= 0) {
                    partitionPolicies[existingIdx] = newPolicy;
                } else {
                    partitionPolicies.push(newPolicy);
                }
                fs.writeFileSync(PATH_POLICIES, JSON.stringify(partitionPolicies, null, 2), 'utf-8');
                reMatchConnectedClients();
                broadcastToDashboard('POLICIES_UPDATED', { partitionPolicies });
                res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
                res.end(JSON.stringify({ success: true, policies: partitionPolicies }));
            } catch (e) {
                res.writeHead(400);
                res.end(JSON.stringify({ error: e.message }));
            }
        });
        return;
    }

    // (9-1) 파티션 정책 삭제
    if (url.pathname === '/api/policy/delete' && req.method === 'POST') {
        let body = '';
        req.on('data', chunk => body += chunk);
        req.on('end', () => {
            try {
                const payload = JSON.parse(body);
                const targetId = payload.policyId || payload.id;
                if (!targetId) {
                    res.writeHead(400, { 'Content-Type': 'application/json' });
                    res.end(JSON.stringify({ error: '삭제할 정책 ID(id/policyId)가 전달되지 않았습니다.' }));
                    return;
                }
                partitionPolicies = partitionPolicies.filter(p => p.id !== targetId);
                fs.writeFileSync(PATH_POLICIES, JSON.stringify(partitionPolicies, null, 2), 'utf-8');
                reMatchConnectedClients();
                broadcastToDashboard('POLICIES_UPDATED', { partitionPolicies });
                res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
                res.end(JSON.stringify({ success: true, policies: partitionPolicies }));
            } catch (e) {
                res.writeHead(400, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify({ error: e.message }));
            }
        });
        return;
    }

    // -------------------------------------------------------------
    // 마스터 이미지 파일 HTTP 다운로드/스트리밍 제공 (/images/:filename)
    // -------------------------------------------------------------
    if (url.pathname.startsWith('/images/')) {
        const reqFileName = decodeURIComponent(url.pathname.replace(/^\/images\//, ''));
        const safeFileName = path.basename(reqFileName);
        const imageFullPath = path.join(serverConfig.masterImageDir, safeFileName);

        if (fs.existsSync(imageFullPath) && fs.statSync(imageFullPath).isFile()) {
            const stat = fs.statSync(imageFullPath);
            const range = req.headers.range;

            if (range) {
                // HTTP Range 청크 스트리밍 지원 (대용량 WIM 최적화)
                const parts = range.replace(/bytes=/, "").split("-");
                const start = parseInt(parts[0], 10);
                const end = parts[1] ? parseInt(parts[1], 10) : stat.size - 1;
                const chunksize = (end - start) + 1;
                const file = fs.createReadStream(imageFullPath, { start, end });
                res.writeHead(206, {
                    'Content-Range': `bytes ${start}-${end}/${stat.size}`,
                    'Accept-Ranges': 'bytes',
                    'Content-Length': chunksize,
                    'Content-Type': 'application/octet-stream'
                });
                file.pipe(res);
            } else {
                res.writeHead(200, {
                    'Content-Length': stat.size,
                    'Content-Type': 'application/octet-stream',
                    'Content-Disposition': `attachment; filename="${safeFileName}"`
                });
                fs.createReadStream(imageFullPath).pipe(res);
            }
            return;
        } else {
            res.writeHead(404, { 'Content-Type': 'application/json; charset=utf-8' });
            res.end(JSON.stringify({ error: `지정된 마스터 이미지 파일(${safeFileName})을 찾을 수 없습니다.` }));
            return;
        }
    }

    // -------------------------------------------------------------
    // 정적 프론트엔드 파일 제공 (public/)
    // -------------------------------------------------------------
    let reqPath = url.pathname === '/' ? '/index.html' : url.pathname;
    const filePath = path.join(PUBLIC_DIR, reqPath);

    if (fs.existsSync(filePath) && fs.statSync(filePath).isFile()) {
        const ext = path.extname(filePath).toLowerCase();
        const mimeTypes = {
            '.html': 'text/html; charset=utf-8',
            '.css': 'text/css; charset=utf-8',
            '.js': 'application/javascript; charset=utf-8',
            '.json': 'application/json; charset=utf-8',
            '.png': 'image/png',
            '.svg': 'image/svg+xml'
        };
        res.writeHead(200, { 'Content-Type': mimeTypes[ext] || 'application/octet-stream' });
        fs.createReadStream(filePath).pipe(res);
    } else {
        res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
        res.end('404 Not Found');
    }
});

// 9. WebSocket 업그레이드 핸들러 (외부 패키지 0% 독립 구현)
httpServer.on('upgrade', (req, socket, head) => {
    const key = req.headers['sec-websocket-key'];
    if (!key) {
        socket.destroy();
        return;
    }

    const acceptKey = crypto.createHash('sha1')
        .update(key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11')
        .digest('base64');

    const headers = [
        'HTTP/1.1 101 Switching Protocols',
        'Upgrade: websocket',
        'Connection: Upgrade',
        `Sec-WebSocket-Accept: ${acceptKey}`
    ];

    socket.write(headers.join('\r\n') + '\r\n\r\n');
    webSocketClients.add(socket);

    // 초기 상태 즉시 전송
    const initialState = JSON.stringify({ event: 'INIT_STATE', data: getDashboardState() });
    sendWsFrame(socket, initialState);

    socket.on('data', (buffer) => {
        handleIncomingWsFrame(socket, buffer);
    });

    socket.on('close', () => webSocketClients.delete(socket));
    socket.on('error', () => webSocketClients.delete(socket));
});

function sendWsFrame(socket, payloadStr) {
    if (!socket || socket.destroyed || socket.writableEnded) return;
    try {
        const payload = Buffer.from(payloadStr, 'utf-8');
        const length = payload.length;
        let header;

        if (length <= 125) {
            header = Buffer.from([0x81, length]);
        } else if (length <= 65535) {
            header = Buffer.alloc(4);
            header[0] = 0x81;
            header[1] = 126;
            header.writeUInt16BE(length, 2);
        } else {
            header = Buffer.alloc(10);
            header[0] = 0x81;
            header[1] = 127;
            header.writeBigUInt64BE(BigInt(length), 2);
        }

        socket.write(Buffer.concat([header, payload]), (err) => {
            if (err) webSocketClients.delete(socket);
        });
    } catch (e) {
        webSocketClients.delete(socket);
    }
}

function handleIncomingWsFrame(socket, buf) {
    if (buf.length < 2) return;
    const opcode = buf[0] & 0x0f;
    if (opcode === 0x8) { // Connection Close
        webSocketClients.delete(socket);
        try { socket.end(); } catch (e) {}
        return;
    }
    if (opcode === 0x9) { // Ping -> Pong
        try { socket.write(Buffer.from([0x8A, 0x00])); } catch (e) {}
        return;
    }
}

process.on('uncaughtException', (err) => {
    console.error('[-] 서버 미처리 예외 (복구됨):', err.message);
});

process.on('unhandledRejection', (reason) => {
    console.error('[-] 서버 미처리 거부 (복구됨):', reason);
});

httpServer.listen(PORT_HTTP_WEB, '0.0.0.0', () => {
    console.log(`================================================================`);
    console.log(`[🚀] 다중 PC 이미지 더빙 마스터 서버 가동 완료!`);
    console.log(`  - 웹 관리 대시보드: http://localhost:${PORT_HTTP_WEB}`);
    console.log(`  - 마스터 이미지 디렉터리: ${serverConfig.masterImageDir}`);
    console.log(`  - WinPE Agent 소켓: 0.0.0.0:${PORT_TCP_CLIENT}`);
    console.log(`  - UDP 탐색 리스너:  0.0.0.0:${PORT_UDP_DISCOVERY}`);
    console.log(`================================================================\n`);
});

