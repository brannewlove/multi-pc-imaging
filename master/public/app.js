// ==========================================================================
// Multi-Deploy OS Master Dashboard - Frontend Application Logic
// ==========================================================================

let clientsMap = new Map();
let partitionPolicies = [];
let imageCatalog = [];
let deployHistory = [];
let globalAutoApprove = true;

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

    // 배포 이력 카운트도 반영
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
        // 검색 필터
        const q = searchFilter.toLowerCase();
        const m = (c.system?.modelName || '').toLowerCase();
        const s = (c.system?.serialNumber || '').toLowerCase();
        const mac = (c.network?.primaryMac || '').toLowerCase();
        const matchSearch = !q || m.includes(q) || s.includes(q) || mac.includes(q);

        // 상태 필터
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
    const imageName = c.matchedImage?.imageFileName || '표준 WIM 이미지';

    // 상태 배지 텍스트
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
            <div class="plan-row" style="margin-top:4px;">
                <span class="plan-label">할당 WIM 이미지:</span>
                <span class="plan-value" style="font-size:0.78rem;">${escapeHtml(imageName)}</span>
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

// 7. 로그 뷰어 모달
function openClientLog(sessionId) {
    const client = clientsMap.get(sessionId);
    elLogTitle.textContent = `[${sessionId}] 상세 작업 로그`;
    
    let sampleLogs = [
        `[INFO] [WinPE] Hardware scanner initialized.`,
        `[INFO] System: ${client?.system?.modelName || 'Unknown'} (S/N: ${client?.system?.serialNumber || 'N/A'})`,
        `[INFO] Primary MAC: ${client?.network?.primaryMac} | Assigned IP: ${client?.network?.ipAddress}`,
        `[INFO] Disk 0: ${client?.disks?.[0]?.friendlyName} [Capacity: ${client?.disks?.[0]?.sizeGb?.toFixed(1)} GB]`,
        `[INFO] Matched Policy: ${client?.matchedPolicy?.name}`,
        `[INFO] Assigned Image: ${client?.matchedImage?.imageFileName}`,
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

// 8. 이벤트 바인딩
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

elBtnOpenPolicies.addEventListener('click', () => {
    renderPoliciesList();
    elModalPolicies.classList.remove('hidden');
});

elBtnClosePolicies.addEventListener('click', () => elModalPolicies.classList.add('hidden'));
elBtnClosePoliciesBottom.addEventListener('click', () => elModalPolicies.classList.add('hidden'));
elBtnCloseLog.addEventListener('click', () => elModalLog.classList.add('hidden'));
elBtnCloseLogBottom.addEventListener('click', () => elModalLog.classList.add('hidden'));

// 초기 구동
initWebSocket();
