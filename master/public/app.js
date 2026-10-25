// ==========================================================================
// Multi-Deploy OS Master Dashboard - Frontend Application Logic
// ==========================================================================

let clientsMap = new Map();
let partitionPolicies = [];
let imageCatalog = [];
let deployHistory = [];
let globalAutoApprove = true;
let serverConfig = {
    masterImageDir: "C:\\images",
    networkSharePath: "\\\\127.0.0.1\\images",
    clientAccessMethod: "LOCAL_OR_SHARE"
};
let scannedImages = { totalFiles: 0, totalSizeGb: "0.00", files: [] };

let ws = null;
let currentTab = 'LIVE'; // 'LIVE' or 'HISTORY'
let searchFilter = '';
let statusFilter = 'ALL';

// DOM 요소 캐시
const elClientsGrid = document.getElementById('clientsGrid');
const elHistoryBody = document.getElementById('historyTableBody');
const elEmptyState = document.getElementById('emptyStatePlaceholder');

const elValTotal = document.getElementById('valTotalConnected');
const elValPending = document.getElementById('valPending');
const elValActive = document.getElementById('valActiveImaging');
const elValSuccess = document.getElementById('valSuccess');
const elValFailed = document.getElementById('valFailed');

const elBadgeClient = document.getElementById('badgeClientCount');
const elBadgeHistory = document.getElementById('badgeHistoryCount');

const elSwitchAutoApprove = document.getElementById('switchAutoApprove');
const elLblAutoApprove = document.getElementById('lblAutoApproveStatus');
const elBtnToggleAutoApprove = document.getElementById('btnToggleAutoApprove');
const elBtnBatchApprove = document.getElementById('btnBatchApprove');

const elTabLive = document.getElementById('tabLiveClients');
const elTabHistory = document.getElementById('tabHistory');
const elViewClients = document.getElementById('viewClientsArea');
const elViewHistory = document.getElementById('viewHistoryArea');

const elInputSearch = document.getElementById('inputSearch');
const elSelectStatus = document.getElementById('selectStatusFilter');

const elModalPolicies = document.getElementById('modalPolicies');
const elBtnOpenPolicies = document.getElementById('btnOpenPolicies');
const elBtnClosePolicies = document.getElementById('btnClosePolicies');
const elBtnClosePoliciesBottom = document.getElementById('btnClosePoliciesBottom');
const elPolicyListContainer = document.getElementById('policyListContainer');

// 마스터 이미지 및 선택 규칙 모달 DOM
const elModalImageRules = document.getElementById('modalImageRules');
const elBtnOpenImageRules = document.getElementById('btnOpenImageRules');
const elBtnCloseImageRules = document.getElementById('btnCloseImageRules');
const elBtnCloseImageRulesBottom = document.getElementById('btnCloseImageRulesBottom');

const elInputMasterImageDir = document.getElementById('inputMasterImageDir');
const elInputNetworkSharePath = document.getElementById('inputNetworkSharePath');
const elSelectAccessMethod = document.getElementById('selectAccessMethod');
const elBtnSaveAndScanFolder = document.getElementById('btnSaveAndScanFolder');
const elBadgeScannedCount = document.getElementById('badgeScannedCount');
const elScannedFilesContainer = document.getElementById('scannedFilesContainer');

const elImageRulesListContainer = document.getElementById('imageRulesListContainer');
const elBtnAddNewImageRule = document.getElementById('btnAddNewImageRule');
const elBtnSaveAllImageRules = document.getElementById('btnSaveAllImageRules');

const elSimTestMfg = document.getElementById('simTestMfg');
const elSimTestModel = document.getElementById('simTestModel');
const elSimTestDisk = document.getElementById('simTestDisk');
const elBtnRunMatchTest = document.getElementById('btnRunMatchTest');
const elSimTestResultBox = document.getElementById('simTestResultBox');

const elModalLog = document.getElementById('modalLogViewer');
const elBtnCloseLog = document.getElementById('btnCloseLogViewer');
const elBtnCloseLogBottom = document.getElementById('btnCloseLogViewerBottom');
const elLogContent = document.getElementById('logTerminalContent');
const elLogTitle = document.getElementById('modalLogTitle');

// 1. WebSocket 연결 수립 및 이벤트 수신
function initWebSocket() {
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    const wsUrl = `${protocol}//${window.location.host}/ws`;

    ws = new WebSocket(wsUrl);

    ws.onopen = () => {
        console.log('[+] 마스터 서버 WebSocket 연결 성공');
    };

    ws.onmessage = (event) => {
        try {
            const msg = JSON.parse(event.data);
            handleWsMessage(msg.event, msg.data);
        } catch (e) {
            console.error('[-] 메시지 파싱 오류:', e);
        }
    };

    ws.onclose = () => {
        console.warn('[-] WebSocket 연결 끊김. 2초 후 재연결...');
        setTimeout(initWebSocket, 2000);
    };

    ws.onerror = (err) => {
        console.error('[-] WebSocket 에러:', err);
    };
}

function handleWsMessage(event, data) {
    if (event === 'INIT_STATE') {
        partitionPolicies = data.partitionPolicies || [];
        imageCatalog = data.imageCatalog || [];
        deployHistory = data.deployHistory || [];
        globalAutoApprove = data.globalAutoApprove;
        if (data.serverConfig) serverConfig = data.serverConfig;
        if (data.scannedImages) scannedImages = data.scannedImages;

        updateAutoApproveUI();

        clientsMap.clear();
        (data.clients || []).forEach(c => clientsMap.set(c.sessionId, c));
        renderAll();
    } else if (event === 'CLIENT_REGISTERED' || event === 'CLIENT_STATUS_CHANGE') {
        clientsMap.set(data.sessionId, data.client);
        renderAll();
    } else if (event === 'CLIENT_PROGRESS') {
        const client = clientsMap.get(data.sessionId);
        if (client) {
            client.status = 'IMAGING';
            client.progress = data.progress;
            updateClientProgressUI(client);
            updateMetrics();
        }
    } else if (event === 'CLIENT_COMPLETED') {
        const client = clientsMap.get(data.sessionId);
        if (client) {
            client.status = data.result.status;
            client.result = data.result;
        }
        if (data.historyItem) {
            deployHistory.unshift(data.historyItem);
        }
        renderAll();
    } else if (event === 'CLIENT_DISCONNECTED') {
        clientsMap.delete(data.sessionId);
        renderAll();
    } else if (event === 'GLOBAL_CONFIG_CHANGE') {
        globalAutoApprove = data.globalAutoApprove;
        updateAutoApproveUI();
    } else if (event === 'POLICIES_UPDATED') {
        partitionPolicies = data.partitionPolicies || [];
        renderPoliciesList();
    } else if (event === 'SERVER_CONFIG_UPDATED') {
        if (data.serverConfig) serverConfig = data.serverConfig;
        if (data.scan) scannedImages = data.scan;
        renderImageRulesModalUI();
    } else if (event === 'IMAGE_RULES_UPDATED') {
        if (data.imageCatalog) imageCatalog = data.imageCatalog;
        renderImageRulesList();
    }
}

// 2. 렌더링 메인 루틴
function renderAll() {
    renderClientsGrid();
    renderHistoryTable();
    updateMetrics();
}

function updateMetrics() {
    const clients = Array.from(clientsMap.values());
    const total = clients.length;
    let pending = 0;
    let imaging = 0;
    let success = 0;
    let failed = 0;

    clients.forEach(c => {
        if (c.status === 'READY' || c.status === 'CONNECTED') pending++;
        else if (c.status === 'IMAGING') imaging++;
        else if (c.status === 'SUCCESS') success++;
        else if (c.status === 'FAILED') failed++;
    });

    const histSuccess = deployHistory.filter(h => h.status === 'SUCCESS').length;
    const histFailed = deployHistory.filter(h => h.status === 'FAILED').length;

    elValTotal.textContent = total;
    elValPending.textContent = pending;
    elValActive.textContent = imaging;
    elValSuccess.textContent = success + histSuccess;
    elValFailed.textContent = failed + histFailed;

    elBadgeClient.textContent = total;
    elBadgeHistory.textContent = deployHistory.length;
}

function updateAutoApproveUI() {
    if (globalAutoApprove) {
        elSwitchAutoApprove.classList.add('active');
        elLblAutoApprove.textContent = '활성화';
        elLblAutoApprove.style.color = 'var(--accent-emerald)';
    } else {
        elSwitchAutoApprove.classList.remove('active');
        elLblAutoApprove.textContent = '수동 승인 모드';
        elLblAutoApprove.style.color = 'var(--accent-amber)';
    }
}

// 3. 클라이언트 카드 렌더링
function renderClientsGrid() {
    const clients = Array.from(clientsMap.values()).filter(c => {
        const q = searchFilter.toLowerCase();
        const m = (c.system?.modelName || '').toLowerCase();
        const s = (c.system?.serialNumber || '').toLowerCase();
        const mac = (c.network?.primaryMac || '').toLowerCase();
        const matchSearch = !q || m.includes(q) || s.includes(q) || mac.includes(q);
        const matchStatus = (statusFilter === 'ALL') || (c.status === statusFilter);
        return matchSearch && matchStatus;
    });

    if (clients.length === 0) {
        elClientsGrid.innerHTML = '';
        elClientsGrid.appendChild(elEmptyState);
        elEmptyState.classList.remove('hidden');
        return;
    }

    elEmptyState.classList.add('hidden');
    elClientsGrid.innerHTML = '';

    clients.forEach(c => {
        const card = createClientCardElement(c);
        elClientsGrid.appendChild(card);
    });
}

function createClientCardElement(c) {
    const card = document.createElement('div');
    card.className = `pc-card status-${(c.status || 'ready').toLowerCase()}`;
    card.id = `card-${c.sessionId}`;

    const modelName = c.system?.modelName || '미확인 모델 (Generic)';
    const manufacturer = c.system?.manufacturer || '알 수 없음';
    const serial = c.system?.serialNumber || 'N/A';
    const mac = c.network?.primaryMac || '00:00:00:00:00:00';
    const ip = c.network?.ipAddress || '127.0.0.1';

    // 타깃 디스크 정보
    const disk0 = (c.disks && c.disks.length > 0) ? c.disks[0] : null;
    const diskSummary = disk0 
        ? `[Disk 0] ${disk0.friendlyName || 'SSD'} (${disk0.busType || 'NVMe'}, ${(disk0.sizeGb || 0).toFixed(0)} GB)` 
        : '장착된 디스크 없음';

    const policyName = c.matchedPolicy?.name || '맞춤 권장양식 자동 매칭됨';
    const imageRuleName = c.matchedImage?.name || '자동 매칭 이미지';
    const imageName = c.matchedImage?.targetImage?.imageFileName || c.matchedImage?.imageFileName || 'Win11_Universal.wim';

    let statusText = '승인 대기';
    let badgeClass = 'badge-ready';
    if (c.status === 'IMAGING') {
        statusText = '더빙 진행 중';
        badgeClass = 'badge-imaging';
    } else if (c.status === 'SUCCESS') {
        statusText = '더빙 완료 (성공)';
        badgeClass = 'badge-success';
    } else if (c.status === 'FAILED') {
        statusText = '더빙 실패';
        badgeClass = 'badge-failed';
    }

    const pct = c.progress ? Math.round(c.progress.percent) : 0;
    const stepText = c.progress ? c.progress.step : '배포 대기 중';
    const remainingText = c.progress && c.progress.remainingSeconds > 0 ? `남은 시간: ${c.progress.remainingSeconds}초` : '';

    card.innerHTML = `
        <div class="card-header">
            <div class="model-title-box">
                <span class="manufacturer-tag">${escapeHtml(manufacturer)}</span>
                <h3 class="model-name">${escapeHtml(modelName)}</h3>
            </div>
            <span class="status-badge ${badgeClass}" id="badge-${c.sessionId}">
                <span class="live-dot" style="width:7px;height:7px;"></span>
                ${statusText}
            </span>
        </div>

        <div class="spec-info-grid">
            <div class="spec-item">
                <span class="spec-key">시리얼 번호 (S/N)</span>
                <span class="spec-val">${escapeHtml(serial)}</span>
            </div>
            <div class="spec-item">
                <span class="spec-key">MAC / IP 주소</span>
                <span class="spec-val">${escapeHtml(mac)} (${escapeHtml(ip)})</span>
            </div>
            <div class="spec-item" style="grid-column: span 2;">
                <span class="spec-key">타깃 스토리지 (HDD/SSD)</span>
                <span class="spec-val" style="color:#38bdf8;">${escapeHtml(diskSummary)}</span>
            </div>
        </div>

        <div class="deployment-plan-box">
            <div class="plan-row">
                <span class="plan-label">파티션 권장양식:</span>
                <span class="policy-badge">${escapeHtml(policyName)}</span>
            </div>
            <div class="partition-visual-bar">
                <div class="part-segment part-efi" title="EFI 시스템 파티션 (300MB)">EFI 300M</div>
                <div class="part-segment part-msr" title="MSR 예약 파티션 (16MB)">MSR</div>
                <div class="part-segment part-os" title="Windows OS 파티션 (주 파티션)">Windows C: (주 파티션)</div>
            </div>
            <div class="plan-row" style="margin-top:6px; align-items:center;">
                <span class="plan-label">매칭 WIM 이미지:</span>
                <span class="target-wim-badge" title="${escapeHtml(imageRuleName)}">
                    📀 ${escapeHtml(imageName)}
                </span>
            </div>
        </div>

        <div class="progress-panel" id="progress-${c.sessionId}">
            <div class="progress-header">
                <span class="progress-step" id="pstep-${c.sessionId}">${stepText}</span>
                <span class="progress-percent" id="ppct-${c.sessionId}">${pct}%</span>
            </div>
            <div class="progress-bar-track">
                <div class="progress-bar-fill" id="pbar-${c.sessionId}" style="width: ${pct}%;"></div>
            </div>
            <div class="progress-meta">
                <span id="pspeed-${c.sessionId}">${c.progress ? (c.progress.speedMbps || 850) + ' MB/s' : ''}</span>
                <span id="premain-${c.sessionId}">${remainingText}</span>
            </div>
        </div>

        <div class="card-actions">
            <button class="btn btn-secondary btn-sm" onclick="openClientLog('${c.sessionId}')">
                로그 보기
            </button>
            ${(c.status === 'READY' || c.status === 'CONNECTED') ? `
                <button class="btn btn-primary btn-approve btn-sm" onclick="approveDeploy('${c.sessionId}')">
                    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" style="width:14px;height:14px;"><polygon points="5 3 19 12 5 21 5 3"></polygon></svg>
                    더빙 승인 시작
                </button>
            ` : `
                <span style="font-size:0.8rem;color:var(--text-dim);">
                    ${c.status === 'IMAGING' ? '자동 배포 작업 진행 중...' : (c.status === 'SUCCESS' ? '완료됨' : '오류 발생')}
                </span>
            `}
        </div>
    `;

    return card;
}

function updateClientProgressUI(client) {
    const pbar = document.getElementById(`pbar-${client.sessionId}`);
    const ppct = document.getElementById(`ppct-${client.sessionId}`);
    const pstep = document.getElementById(`pstep-${client.sessionId}`);
    const pspeed = document.getElementById(`pspeed-${client.sessionId}`);
    const premain = document.getElementById(`premain-${client.sessionId}`);

    if (pbar && client.progress) {
        const pct = Math.round(client.progress.percent);
        pbar.style.width = `${pct}%`;
        if (ppct) ppct.textContent = `${pct}%`;
        if (pstep) pstep.textContent = client.progress.step;
        if (pspeed && client.progress.speedMbps) pspeed.textContent = `${client.progress.speedMbps} MB/s`;
        if (premain && client.progress.remainingSeconds > 0) premain.textContent = `남은 시간: ${client.progress.remainingSeconds}초`;
    }
}

// 4. 배포 승인 API 호출
async function approveDeploy(sessionId) {
    try {
        const res = await fetch('/api/approve', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ sessionId })
        });
        const data = await res.json();
        if (data.success) {
            console.log(`[+] [${sessionId}] 배포 승인 완료`);
        }
    } catch (e) {
        alert('승인 요청 실패: ' + e.message);
    }
}

async function batchApproveAll() {
    try {
        const res = await fetch('/api/batch-approve', { method: 'POST' });
        const data = await res.json();
        alert(`${data.count || 0}대의 PC에 일괄 배포 승인을 내렸습니다.`);
    } catch (e) {
        alert('일괄 승인 요청 실패: ' + e.message);
    }
}

async function toggleAutoApprove() {
    try {
        const res = await fetch('/api/toggle-auto-approve', { method: 'POST' });
        const data = await res.json();
        globalAutoApprove = data.globalAutoApprove;
        updateAutoApproveUI();
    } catch (e) {
        console.error('[-] 자동 승인 토글 실패:', e);
    }
}

// 5. 이력 테이블 렌더링
function renderHistoryTable() {
    elHistoryBody.innerHTML = '';
    if (deployHistory.length === 0) {
        elHistoryBody.innerHTML = `
            <tr>
                <td colspan="9" style="text-align:center;padding:40px;color:var(--text-dim);">
                    아직 완료된 배포 작업 이력이 없습니다.
                </td>
            </tr>
        `;
        return;
    }

    deployHistory.forEach(h => {
        const tr = document.createElement('tr');
        const dt = new Date(h.completedAt).toLocaleString();
        const isSuccess = h.status === 'SUCCESS';
        const badge = isSuccess 
            ? '<span class="status-badge badge-success">성공</span>' 
            : '<span class="status-badge badge-failed">실패</span>';

        tr.innerHTML = `
            <td style="font-size:0.8rem;color:var(--text-muted);">${dt}</td>
            <td>${badge}</td>
            <td style="font-weight:600;">${escapeHtml(h.modelName || '-')}</td>
            <td style="font-family:var(--font-mono);">${escapeHtml(h.serialNumber || '-')}</td>
            <td style="font-family:var(--font-mono);font-size:0.8rem;">${escapeHtml(h.macAddress || '-')}</td>
            <td>${escapeHtml(h.policyName || '-')}</td>
            <td style="font-size:0.8rem;">${escapeHtml(h.imageName || '-')}</td>
            <td style="font-weight:600;">${h.durationSeconds || 0}초</td>
            <td style="color:${isSuccess ? 'var(--text-dim)' : 'var(--accent-rose)'};">${escapeHtml(h.errorMessage || '정상 완료')}</td>
        `;
        elHistoryBody.appendChild(tr);
    });
}

// 6. 정책 모달 렌더링
function renderPoliciesList() {
    elPolicyListContainer.innerHTML = '';
    partitionPolicies.forEach(p => {
        const item = document.createElement('div');
        item.className = 'policy-card-item';

        const cond = p.conditions || {};
        item.innerHTML = `
            <div class="policy-header">
                <div>
                    <span class="policy-name">${escapeHtml(p.name)}</span>
                    <p style="font-size:0.78rem;color:var(--text-muted);margin-top:2px;">${escapeHtml(p.description || '')}</p>
                </div>
                <span class="policy-badge" style="${p.autoApprove ? 'background:rgba(16,185,129,0.15);color:#34d399;' : 'background:rgba(245,158,11,0.15);color:#fbbf24;'}">
                    ${p.autoApprove ? '⚡ 자동 승인 ON' : '수동 승인 권장'}
                </span>
            </div>
            <div class="policy-cond-list">
                <span class="cond-chip">모델 매칭: ${escapeHtml(cond.modelPattern || '*')}</span>
                <span class="cond-chip">HDD 용량 범위: ${cond.minCapacityGb || 0}GB ~ ${cond.maxCapacityGb || 9999}GB</span>
                <span class="cond-chip">타깃 디스크: ${cond.targetDiskCriteria || 'INDEX_0'}</span>
                <span class="cond-chip">파티션 형식: ${p.partitionScheme}</span>
            </div>
        `;
        elPolicyListContainer.appendChild(item);
    });
}

// --------------------------------------------------------------------------
// 7. 마스터 이미지 폴더 및 선택 규칙 모달 관리 로직
// --------------------------------------------------------------------------
function renderImageRulesModalUI() {
    elInputMasterImageDir.value = serverConfig.masterImageDir || 'C:\\images';
    elInputNetworkSharePath.value = serverConfig.networkSharePath || '\\\\127.0.0.1\\images';
    elSelectAccessMethod.value = serverConfig.clientAccessMethod || 'LOCAL_OR_SHARE';

    renderScannedFilesPills();
    renderImageRulesList();
}

function renderScannedFilesPills() {
    const files = scannedImages.files || [];
    elBadgeScannedCount.textContent = `${files.length}개 WIM 감지됨 (${scannedImages.totalSizeGb || '0.00'} GB)`;

    if (files.length === 0) {
        elScannedFilesContainer.innerHTML = `
            <div class="scanned-files-empty">
                ${scannedImages.error ? `<span style="color:#f87171;">⚠️ ${escapeHtml(scannedImages.error)}</span>` : '지정된 폴더에 .wim 또는 .esd 파일이 없습니다. 경로를 확인하고 스캔을 실행하세요.'}
            </div>
        `;
        return;
    }

    elScannedFilesContainer.innerHTML = files.map(f => `
        <div class="scanned-file-pill" title="경로: ${escapeHtml(f.fullPath)}">
            <span>📀 ${escapeHtml(f.fileName)}</span>
            <span class="file-size">${f.sizeGb} GB</span>
        </div>
    `).join('');
}

function renderImageRulesList() {
    elImageRulesListContainer.innerHTML = '';

    if (!imageCatalog || imageCatalog.length === 0) {
        elImageRulesListContainer.innerHTML = '<div style="padding:20px;text-align:center;color:var(--text-muted);">등록된 선택 규칙이 없습니다.</div>';
        return;
    }

    // 우선순위 정렬
    const sorted = [...imageCatalog].sort((a, b) => (a.priority || 999) - (b.priority || 999));

    sorted.forEach((rule, idx) => {
        const card = document.createElement('div');
        card.className = `image-rule-card ${rule.enabled === false ? 'disabled-rule' : ''}`;

        const cond = rule.conditions || {};
        const target = rule.targetImage || {};
        const isFallback = rule.isFallback === true;

        card.innerHTML = `
            <div class="rule-top-bar">
                <div class="rule-title-box">
                    <span class="rule-priority-badge ${isFallback ? 'rule-priority-fallback' : ''}">
                        ${isFallback ? '기본 대체 (Fallback)' : `우선순위 #${rule.priority || (idx + 1)}`}
                    </span>
                    <span class="rule-name-text">${escapeHtml(rule.name)}</span>
                </div>
                <div class="rule-action-buttons">
                    ${!isFallback && idx > 0 ? `
                        <button class="btn btn-secondary btn-xs" onclick="moveRulePriority('${rule.id}', -1)" title="우선순위 올림">▲</button>
                    ` : ''}
                    ${!isFallback && idx < sorted.length - 2 ? `
                        <button class="btn btn-secondary btn-xs" onclick="moveRulePriority('${rule.id}', 1)" title="우선순위 내림">▼</button>
                    ` : ''}
                    <button class="btn btn-secondary btn-xs" onclick="toggleRuleEnabled('${rule.id}')">
                        ${rule.enabled !== false ? '✅ 활성' : '⚪ 비활성'}
                    </button>
                    <button class="btn btn-secondary btn-xs" onclick="editImageRule('${rule.id}')">수정</button>
                    ${!isFallback ? `
                        <button class="btn btn-danger btn-xs" onclick="deleteImageRule('${rule.id}')">삭제</button>
                    ` : ''}
                </div>
            </div>

            <div class="rule-details-grid">
                <div class="rule-condition-list">
                    <span class="detail-label">매칭 판별 조건 (Hardware Match)</span>
                    <div style="font-size:0.82rem;line-height:1.6;color:#cbd5e1;">
                        • 제조사: <strong>${escapeHtml(cond.manufacturer || '*')}</strong><br>
                        • 모델명 패턴: <strong>${escapeHtml(cond.modelPattern || '*')}</strong><br>
                        • 스토리지 용량: <strong>${cond.minDiskGb || 0}GB ~ ${cond.maxDiskGb || 9999}GB</strong> (${cond.targetDiskType || '모든 타입'})
                    </div>
                </div>
                <div class="rule-target-image">
                    <span class="detail-label">적용 마스터 이미지 (Assigned WIM)</span>
                    <div style="margin-top:2px;">
                        <span class="target-wim-badge">📀 ${escapeHtml(target.imageFileName || rule.imageFileName || '미지정.wim')}</span>
                        <span style="font-size:0.75rem;color:var(--text-muted);margin-left:4px;">(Index: ${target.imageIndex || 1})</span>
                    </div>
                    <p style="font-size:0.75rem;color:var(--text-muted);margin-top:4px;">${escapeHtml(target.description || rule.description || '')}</p>
                </div>
            </div>
        `;
        elImageRulesListContainer.appendChild(card);
    });
}

// 폴더 저장 및 스캔 API 호출
async function saveAndScanMasterFolder() {
    const dir = elInputMasterImageDir.value.trim();
    const share = elInputNetworkSharePath.value.trim();
    const method = elSelectAccessMethod.value;

    if (!dir) {
        alert('마스터 이미지 폴더 경로를 입력하세요.');
        return;
    }

    try {
        elBtnSaveAndScanFolder.disabled = true;
        elBtnSaveAndScanFolder.innerHTML = '스캔 중...';

        const res = await fetch('/api/images/folder', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                masterImageDir: dir,
                networkSharePath: share,
                clientAccessMethod: method
            })
        });

        const data = await res.json();
        if (data.success) {
            serverConfig = data.serverConfig;
            scannedImages = data.scan;
            renderScannedFilesPills();
            if (scannedImages.files.length > 0) {
                alert(`폴더가 저장되었습니다. 총 ${scannedImages.files.length}개의 WIM 파일(${scannedImages.totalSizeGb} GB)을 발견했습니다.`);
            } else if (scannedImages.error) {
                alert(`경고: ${scannedImages.error}`);
            } else {
                alert('폴더 설정이 저장되었으나, .wim 파일이 발견되지 않았습니다.');
            }
        } else {
            alert('폴더 저장 실패: ' + (data.error || '알 수 없는 오류'));
        }
    } catch (e) {
        alert('요청 오류: ' + e.message);
    } finally {
        elBtnSaveAndScanFolder.disabled = false;
        elBtnSaveAndScanFolder.innerHTML = `
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M21.5 2v6h-6M21.34 15.57a10 10 0 1 1-.57-8.38l5.67-5.67"/></svg>
            폴더 저장 & WIM 스캔
        `;
    }
}

// 규칙 우선순위 변경
function moveRulePriority(ruleId, direction) {
    const nonFallback = imageCatalog.filter(r => !r.isFallback).sort((a,b) => (a.priority||999) - (b.priority||999));
    const idx = nonFallback.findIndex(r => r.id === ruleId);
    if (idx < 0) return;

    const targetIdx = idx + direction;
    if (targetIdx < 0 || targetIdx >= nonFallback.length) return;

    // 스왑
    const temp = nonFallback[idx];
    nonFallback[idx] = nonFallback[targetIdx];
    nonFallback[targetIdx] = temp;

    // 우선순위 재정렬 부여 (1, 2, 3...)
    nonFallback.forEach((r, i) => r.priority = i + 1);

    renderImageRulesList();
}

function toggleRuleEnabled(ruleId) {
    const r = imageCatalog.find(rule => rule.id === ruleId);
    if (r) {
        r.enabled = !(r.enabled !== false);
        renderImageRulesList();
    }
}

function deleteImageRule(ruleId) {
    if (!confirm('이 선택 규칙을 삭제하시겠습니까?')) return;
    imageCatalog = imageCatalog.filter(r => r.id !== ruleId);
    renderImageRulesList();
}

function editImageRule(ruleId) {
    const r = imageCatalog.find(rule => rule.id === ruleId);
    if (!r) return;

    const newName = prompt('규칙 이름을 입력하세요:', r.name);
    if (newName === null) return;
    r.name = newName;

    const newMfg = prompt('매칭할 제조사 패턴을 입력하세요 (와일드카드 * 지원, 예: SAMSUNG*):', r.conditions?.manufacturer || '*');
    if (newMfg !== null) {
        if (!r.conditions) r.conditions = {};
        r.conditions.manufacturer = newMfg;
    }

    const newModel = prompt('매칭할 모델명 패턴을 입력하세요 (예: NT950*|Galaxy*):', r.conditions?.modelPattern || '*');
    if (newModel !== null) {
        r.conditions.modelPattern = newModel;
    }

    const newWim = prompt('적용할 WIM 파일명을 입력하세요 (예: Win11_23H2_Pro.wim):', r.targetImage?.imageFileName || r.imageFileName || 'install.wim');
    if (newWim !== null) {
        if (!r.targetImage) r.targetImage = {};
        r.targetImage.imageFileName = newWim;
    }

    renderImageRulesList();
}

function addNewImageRule() {
    const name = prompt('새 선택 규칙 이름을 입력하세요:', '신규 기종 이미지 규칙');
    if (!name) return;

    const mfg = prompt('제조사 패턴 (예: SAMSUNG*, LENOVO*, DELL*, *):', '*') || '*';
    const model = prompt('모델명 패턴 (예: NT950*, ThinkPad*, Latitude*, *):', '*') || '*';
    
    // 스캔된 파일이 있으면 첫 번째 파일을 기본값으로 제안
    const defaultWim = (scannedImages.files && scannedImages.files.length > 0) ? scannedImages.files[0].fileName : 'Win11_Custom.wim';
    const wim = prompt('적용할 WIM 파일명을 입력하세요:', defaultWim) || defaultWim;

    const newRule = {
        id: 'rule-' + Date.now(),
        name: name,
        enabled: true,
        priority: imageCatalog.filter(r => !r.isFallback).length + 1,
        conditions: {
            manufacturer: mfg,
            modelPattern: model,
            minDiskGb: 100,
            maxDiskGb: 4000,
            targetDiskType: 'ANY'
        },
        targetImage: {
            imageFileName: wim,
            imageIndex: 1,
            osVersion: 'Windows 11 x64',
            description: '관리자 신규 등록 이미지'
        },
        isFallback: false
    };

    // fallback 바로 앞에 삽입
    const fallbackIdx = imageCatalog.findIndex(r => r.isFallback);
    if (fallbackIdx >= 0) {
        imageCatalog.splice(fallbackIdx, 0, newRule);
    } else {
        imageCatalog.push(newRule);
    }

    renderImageRulesList();
}

async function saveAllImageRulesToServer() {
    try {
        elBtnSaveAllImageRules.disabled = true;
        elBtnSaveAllImageRules.textContent = '저장 중...';

        const res = await fetch('/api/images/rules', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ rules: imageCatalog })
        });
        const data = await res.json();
        if (data.success) {
            imageCatalog = data.rules;
            alert('이미지 선택 규칙이 성공적으로 저장되었습니다. 대기 중인 PC들에 새 규칙이 즉시 적용되었습니다.');
            elModalImageRules.classList.add('hidden');
        } else {
            alert('저장 실패: ' + (data.error || '오류 발생'));
        }
    } catch (e) {
        alert('서버 저장 요청 오류: ' + e.message);
    } finally {
        elBtnSaveAllImageRules.disabled = false;
        elBtnSaveAllImageRules.textContent = '선택 규칙 전체 저장';
    }
}

// 실시간 규칙 매칭 시뮬레이터 테스트
async function runMatchSimulationTest() {
    const mfg = elSimTestMfg.value.trim();
    const model = elSimTestModel.value.trim();
    const disk = elSimTestDisk.value.trim();

    try {
        elBtnRunMatchTest.disabled = true;
        elBtnRunMatchTest.textContent = '테스트 중...';

        const res = await fetch('/api/images/test-match', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                manufacturer: mfg,
                modelName: model,
                diskGb: disk
            })
        });

        const data = await res.json();
        if (data.success && data.matchedRule) {
            const rule = data.matchedRule;
            const wimName = rule.targetImage?.imageFileName || rule.imageFileName || 'N/A';
            elSimTestResultBox.innerHTML = `
                <div style="color:#34d399;font-weight:700;margin-bottom:4px;">
                    ✅ 매칭 성공: [${escapeHtml(rule.name)}]
                </div>
                <div style="font-size:0.84rem;color:#f1f5f9;">
                    • 최종 적용 WIM 이미지: <span class="target-wim-badge" style="display:inline-block;">📀 ${escapeHtml(wimName)}</span> (인덱스: ${rule.targetImage?.imageIndex || 1})<br>
                    • 규칙 우선순위: <strong>#${rule.priority || 'Fallback'}</strong> (${rule.isFallback ? '기본 대체 규칙' : '조건 정밀 일치'})<br>
                    • 이미지 설명: ${escapeHtml(rule.targetImage?.description || rule.description || '없음')}
                </div>
            `;
        } else {
            elSimTestResultBox.innerHTML = '<span style="color:#f87171;">일치하는 규칙이 없습니다.</span>';
        }
    } catch (e) {
        elSimTestResultBox.innerHTML = `<span style="color:#f87171;">테스트 오류: ${escapeHtml(e.message)}</span>`;
    } finally {
        elBtnRunMatchTest.disabled = false;
        elBtnRunMatchTest.textContent = '매칭 테스트';
    }
}

// 8. 로그 뷰어 모달
function openClientLog(sessionId) {
    const client = clientsMap.get(sessionId);
    elLogTitle.textContent = `[${sessionId}] 상세 작업 로그`;
    
    let sampleLogs = [
        `[INFO] [WinPE] Hardware scanner initialized.`,
        `[INFO] System: ${client?.system?.modelName || 'Unknown'} (S/N: ${client?.system?.serialNumber || 'N/A'})`,
        `[INFO] Primary MAC: ${client?.network?.primaryMac} | Assigned IP: ${client?.network?.ipAddress}`,
        `[INFO] Disk 0: ${client?.disks?.[0]?.friendlyName} [Capacity: ${client?.disks?.[0]?.sizeGb?.toFixed(1)} GB]`,
        `[INFO] Matched Policy: ${client?.matchedPolicy?.name}`,
        `[INFO] Matched Image Rule: ${client?.matchedImage?.name}`,
        `[INFO] Assigned Image WIM: ${client?.matchedImage?.targetImage?.imageFileName || client?.matchedImage?.imageFileName}`,
        `[STATUS] Current Stage: ${client?.progress?.step || client?.status}`,
        `[PERCENT] Progress: ${client?.progress?.percent || 0}%`,
        `[LOG] BCDBoot configuration target: S:\\EFI, W:\\Windows`
    ];

    if (client?.result) {
        sampleLogs.push(`[RESULT] Finished with status: ${client.result.status} (${client.result.totalDurationSeconds}s)`);
        if (client.result.errorMessage) {
            sampleLogs.push(`[ERROR] ${client.result.errorMessage}`);
        }
    }

    elLogContent.textContent = sampleLogs.join('\n');
    elModalLog.classList.remove('hidden');
}

function escapeHtml(str) {
    if (!str) return '';
    return String(str)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;');
}

// 9. 이벤트 바인딩
elBtnToggleAutoApprove.addEventListener('click', toggleAutoApprove);
elBtnBatchApprove.addEventListener('click', batchApproveAll);

elTabLive.addEventListener('click', () => {
    currentTab = 'LIVE';
    elTabLive.classList.add('active');
    elTabHistory.classList.remove('active');
    elViewClients.classList.remove('hidden');
    elViewHistory.classList.add('hidden');
});

elTabHistory.addEventListener('click', () => {
    currentTab = 'HISTORY';
    elTabHistory.classList.add('active');
    elTabLive.classList.remove('active');
    elViewHistory.classList.remove('hidden');
    elViewClients.classList.add('hidden');
    renderHistoryTable();
});

elInputSearch.addEventListener('input', (e) => {
    searchFilter = e.target.value;
    renderClientsGrid();
});

elSelectStatus.addEventListener('change', (e) => {
    statusFilter = e.target.value;
    renderClientsGrid();
});

// 파티션 모달
elBtnOpenPolicies.addEventListener('click', () => {
    renderPoliciesList();
    elModalPolicies.classList.remove('hidden');
});
elBtnClosePolicies.addEventListener('click', () => elModalPolicies.classList.add('hidden'));
elBtnClosePoliciesBottom.addEventListener('click', () => elModalPolicies.classList.add('hidden'));

// 마스터 이미지 및 선택 규칙 모달
elBtnOpenImageRules.addEventListener('click', () => {
    renderImageRulesModalUI();
    elModalImageRules.classList.remove('hidden');
});
elBtnCloseImageRules.addEventListener('click', () => elModalImageRules.classList.add('hidden'));
elBtnCloseImageRulesBottom.addEventListener('click', () => elModalImageRules.classList.add('hidden'));
elBtnSaveAndScanFolder.addEventListener('click', saveAndScanMasterFolder);
elBtnAddNewImageRule.addEventListener('click', addNewImageRule);
elBtnSaveAllImageRules.addEventListener('click', saveAllImageRulesToServer);
elBtnRunMatchTest.addEventListener('click', runMatchSimulationTest);

// 로그 모달
elBtnCloseLog.addEventListener('click', () => elModalLog.classList.add('hidden'));
elBtnCloseLogBottom.addEventListener('click', () => elModalLog.classList.add('hidden'));

// 초기 구동
initWebSocket();
