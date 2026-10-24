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
const PUBLIC_DIR = path.join(__dirname, 'public');

// 메모리 상태 관리
let partitionPolicies = [];
let imageCatalog = [];
let deployHistory = [];
let globalAutoApprove = true; // 전역 자동 승인 모드 (기본 ON)

// 연결된 WinPE 클라이언트 세션 목록: Map<sessionId, ClientSession>
const connectedClients = new Map();

// 웹소켓 대시보드 클라이언트 목록
const webSocketClients = new Set();

// 1. 설정 로드
function loadConfigs() {
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

function saveHistory() {
    try {
        fs.writeFileSync(PATH_HISTORY, JSON.stringify(deployHistory, null, 2), 'utf-8');
    } catch (e) {
        console.error('[-] 이력 저장 오류:', e.message);
    }
}

// 2. 규칙 매칭 엔진
function matchPartitionPolicy(client) {
    if (!client.disks || client.disks.length === 0) return null;

    const diskCount = client.disks.length;
    const targetDisk = client.disks[0]; // 기본 타깃 디스크 0
    const targetCapacityGb = targetDisk.sizeGb || (targetDisk.sizeBytes / (1024 * 1024 * 1024));

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

    for (const img of imageCatalog) {
        if (img.modelPattern && img.modelPattern !== '*') {
            const regex = new RegExp(img.modelPattern.replace(/\*/g, '.*'), 'i');
            if (regex.test(model)) return img;
        }
        if (img.manufacturer && img.manufacturer !== '*') {
            const regex = new RegExp(img.manufacturer.replace(/\*/g, '.*'), 'i');
            if (regex.test(mfg)) return img;
        }
    }

    // 기본 이미지 반환
    return imageCatalog[imageCatalog.length - 1] || null;
}

function generateDiskpartCommands(policy, targetDiskIndex = 0) {
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

// 3. 브라우저 대시보드 브로드캐스트
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
        clients.push({
            sessionId: id,
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
        });
    }
    return {
        clients,
        partitionPolicies,
        imageCatalog,
        deployHistory: deployHistory.slice(-50).reverse(), // 최근 50건
        globalAutoApprove
    };
}

// 4. 배포 승인 처리
function approveClientDeploy(sessionId) {
    const client = connectedClients.get(sessionId);
    if (!client || !client.socket) return false;
    if (client.status === 'IMAGING' || client.status === 'SUCCESS') return false;

    const policy = client.matchedPolicy || partitionPolicies[0];
    const image = client.matchedImage || imageCatalog[0];
    const cmds = generateDiskpartCommands(policy, 0);

    const deployPayload = {
        type: 'MASTER_APPROVE_DEPLOY',
        payload: {
            sessionId,
            targetDiskIndex: 0,
            partitionPolicyId: policy.id,
            partitionCommands: cmds,
            imageInfo: {
                imageName: image ? image.imageFileName : 'default.wim',
                imageIndex: image ? image.imageIndex : 1,
                sourceType: 'HTTP_STREAM',
                sourceUrl: `http://127.0.0.1:${PORT_HTTP_WEB}/images/${image ? image.imageFileName : 'default.wim'}`,
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

// 5. WinPE Agent TCP 서버 (Port 39201)
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
        console.log(`    -> 매칭 이미지: ${session.matchedImage?.name || '없음'}`);

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
        const historyItem = {
            id: 'HIST-' + Date.now(),
            sessionId: session.sessionId,
            modelName: session.system.modelName,
            serialNumber: session.system.serialNumber,
            macAddress: session.network.primaryMac,
            policyName: session.matchedPolicy?.name,
            imageName: session.matchedImage?.imageFileName,
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

// 6. UDP 자동 탐색 서버 (Port 39200)
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

// 7. 웹 대시보드 HTTP 서버 & 경량 WebSocket 핸들러
const httpServer = http.createServer((req, res) => {
    const url = new URL(req.url, `http://${req.headers.host}`);

    // CORS 및 보안 헤더
    res.setHeader('Access-Control-Allow-Origin', '*');
    res.setHeader('Access-Control-Allow-Methods', 'GET, POST, OPTIONS');
    res.setHeader('Access-Control-Allow-Headers', 'Content-Type');

    if (req.method === 'OPTIONS') {
        res.writeHead(204);
        res.end();
        return;
    }

    // API 라우팅
    if (url.pathname === '/api/state') {
        res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
        res.end(JSON.stringify(getDashboardState()));
        return;
    }

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

    if (url.pathname === '/api/toggle-auto-approve' && req.method === 'POST') {
        globalAutoApprove = !globalAutoApprove;
        broadcastToDashboard('GLOBAL_CONFIG_CHANGE', { globalAutoApprove });
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ success: true, globalAutoApprove }));
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
                broadcastToDashboard('POLICIES_UPDATED', { partitionPolicies });
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify({ success: true, policies: partitionPolicies }));
            } catch (e) {
                res.writeHead(400);
                res.end(JSON.stringify({ error: e.message }));
            }
        });
        return;
    }

    // 정적 파일 제공 (public/)
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

// WebSocket 업그레이드 핸들러 (RFC 6455 표준 직접 구현 - 외부 패키지 무설치 상태에서도 100% 작동)
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
        // Ping-Pong 및 클라이언트 메시지 수신 프레임 언마스킹
        handleIncomingWsFrame(socket, buffer);
    });

    socket.on('close', () => webSocketClients.delete(socket));
    socket.on('error', () => webSocketClients.delete(socket));
});

function sendWsFrame(socket, payloadStr) {
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

    socket.write(Buffer.concat([header, payload]));
}

function handleIncomingWsFrame(socket, buf) {
    if (buf.length < 2) return;
    const opcode = buf[0] & 0x0f;
    if (opcode === 0x8) { // Connection Close
        webSocketClients.delete(socket);
        socket.end();
        return;
    }
    if (opcode === 0x9) { // Ping -> Pong
        socket.write(Buffer.from([0x8A, 0x00]));
        return;
    }
}

httpServer.listen(PORT_HTTP_WEB, '0.0.0.0', () => {
    console.log(`================================================================`);
    console.log(`[🚀] 다중 PC 이미지 더빙 마스터 서버 가동 완료!`);
    console.log(`  - 웹 관리 대시보드: http://localhost:${PORT_HTTP_WEB}`);
    console.log(`  - WinPE Agent 소켓: 0.0.0.0:${PORT_TCP_CLIENT}`);
    console.log(`  - UDP 탐색 리스너:  0.0.0.0:${PORT_UDP_DISCOVERY}`);
    console.log(`================================================================\n`);
});
