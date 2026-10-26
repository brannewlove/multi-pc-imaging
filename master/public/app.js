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
const elClientsTableWrapper = document.getElementById('clientsTableWrapper');
const elClientsDataTable = document.getElementById('clientsDataTable');
const elClientsTableHeader = document.getElementById('clientsTableHeader');
const elClientsTableBody = document.getElementById('clientsTableBody');

const elBtnToggleColumnSettings = document.getElementById('btnToggleColumnSettings');
const elMenuColumnSettings = document.getElementById('menuColumnSettings');
const elColumnCheckboxList = document.getElementById('columnCheckboxList');
const elBtnResetTableConfig = document.getElementById('btnResetTableConfig');

const elBtnViewTable = document.getElementById('btnViewTable');
const elBtnViewCards = document.getElementById('btnViewCards');

const elHeaderContextMenu = document.getElementById('headerContextMenu');
const elCtxHideColumn = document.getElementById('ctxHideColumn');
const elCtxShowAllColumns = document.getElementById('ctxShowAllColumns');
const elCtxResetConfig = document.getElementById('ctxResetConfig');

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

// ==========================================================================
// 테이블 컬럼 메타데이터 및 설정 (순서, 숨김, 너비, 정렬 영구 유지)
// ==========================================================================
const COLUMNS_DEF = [
    { id: 'status', label: '상태', minWidth: 110, defaultWidth: 140, sortable: true },
    { id: 'model', label: '제조사 / 모델명', minWidth: 160, defaultWidth: 230, sortable: true },
    { id: 'serial', label: '시리얼 번호 (S/N)', minWidth: 120, defaultWidth: 150, sortable: true },
    { id: 'network', label: 'IP / MAC 주소', minWidth: 150, defaultWidth: 190, sortable: true },
    { id: 'disk', label: '타깃 스토리지', minWidth: 160, defaultWidth: 210, sortable: true },
    { id: 'policy', label: '파티션 권장양식', minWidth: 140, defaultWidth: 175, sortable: true },
    { id: 'image', label: '매칭 WIM 이미지', minWidth: 160, defaultWidth: 210, sortable: true },
    { id: 'progress', label: '진행률 / 소요속도', minWidth: 180, defaultWidth: 250, sortable: true },
    { id: 'actions', label: '작업 제어', minWidth: 130, defaultWidth: 160, sortable: false }
];

let currentViewMode = 'TABLE'; // 'TABLE' | 'CARDS'
let draggedColId = null;
let contextTargetColId = null;
let isResizing = false;

function getDefaultTableConfig() {
    return {
        order: COLUMNS_DEF.map(c => c.id),
        hidden: [],
        widths: COLUMNS_DEF.reduce((acc, c) => ({ ...acc, [c.id]: c.defaultWidth }), {}),
        sort: { key: 'status', dir: 'ASC' }
    };
}

function loadTableConfig() {
    try {
        const saved = localStorage.getItem('multideploy_table_config_v1');
        if (saved) {
            const parsed = JSON.parse(saved);
            const def = getDefaultTableConfig();
            const validOrder = (parsed.order || []).filter(id => COLUMNS_DEF.some(c => c.id === id));
            COLUMNS_DEF.forEach(c => {
                if (!validOrder.includes(c.id)) validOrder.push(c.id);
            });
            return {
                order: validOrder,
                hidden: parsed.hidden || [],
                widths: { ...def.widths, ...(parsed.widths || {}) },
                sort: parsed.sort !== undefined ? parsed.sort : def.sort
            };
        }
    } catch (e) {
        console.warn('[-] 테이블 설정 로드 실패, 기본값 복원:', e);
    }
    return getDefaultTableConfig();
}

let tableConfig = loadTableConfig();

function saveTableConfig() {
    try {
        localStorage.setItem('multideploy_table_config_v1', JSON.stringify(tableConfig));
    } catch (e) {
        console.error('[-] 테이블 설정 저장 실패:', e);
    }
}

// 2. 렌더링 메인 루틴
function renderAll() {
    renderClientsView();
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

function getFilteredClients() {
    return Array.from(clientsMap.values()).filter(c => {
        const q = searchFilter.toLowerCase();
        const m = (c.system?.modelName || '').toLowerCase();
        const s = (c.system?.serialNumber || '').toLowerCase();
        const mac = (c.network?.primaryMac || '').toLowerCase();
        const ip = (c.network?.ipAddress || '').toLowerCase();
        const matchSearch = !q || m.includes(q) || s.includes(q) || mac.includes(q) || ip.includes(q);
        const matchStatus = (statusFilter === 'ALL') || (c.status === statusFilter);
        return matchSearch && matchStatus;
    });
}

// 3. 실시간 클라이언트 뷰 통합 렌더링 (테이블 / 카드)
function renderClientsView() {
    const clients = getFilteredClients();

    if (clients.length === 0) {
        elEmptyState.classList.remove('hidden');
        elClientsTableWrapper.classList.add('hidden');
        elClientsGrid.classList.add('hidden');
        return;
    }

    elEmptyState.classList.add('hidden');

    if (currentViewMode === 'TABLE') {
        elClientsTableWrapper.classList.remove('hidden');
        elClientsGrid.classList.add('hidden');
        renderClientsTable(clients);
    } else {
        elClientsTableWrapper.classList.add('hidden');
        elClientsGrid.classList.remove('hidden');
        renderClientsGrid(clients);
    }
}

// --------------------------------------------------------------------------
// 테이블 양식 뷰 렌더링 (헤더 순서, 숨김, 너비, 정렬)
// --------------------------------------------------------------------------
function renderClientsTable(clients) {
    renderTableHeader();
    renderTableBody(clients);
    renderColumnSettingsMenu();
}

function getVisibleColumns() {
    const hiddenSet = new Set(tableConfig.hidden || []);
    return tableConfig.order
        .filter(id => !hiddenSet.has(id))
        .map(id => COLUMNS_DEF.find(c => c.id === id))
        .filter(Boolean);
}

function renderTableHeader() {
    const visibleCols = getVisibleColumns();
    elClientsTableHeader.innerHTML = '';
    const tr = document.createElement('tr');

    visibleCols.forEach(col => {
        const th = document.createElement('th');
        th.className = 'table-col-header';
        th.dataset.colId = col.id;
        const curWidth = tableConfig.widths[col.id] || col.defaultWidth;
        th.style.width = `${curWidth}px`;
        th.draggable = true;

        // 정렬 상태 클래스
        let sortIndicator = '';
        if (col.sortable) {
            if (tableConfig.sort && tableConfig.sort.key === col.id) {
                if (tableConfig.sort.dir === 'ASC') {
                    th.classList.add('sorted-asc');
                    sortIndicator = '<span class="sort-indicator" title="오름차순 정렬 중">▲</span>';
                } else if (tableConfig.sort.dir === 'DESC') {
                    th.classList.add('sorted-desc');
                    sortIndicator = '<span class="sort-indicator" title="내림차순 정렬 중">▼</span>';
                }
            } else {
                sortIndicator = '<span class="sort-indicator" title="클릭하여 정렬">↕</span>';
            }
        }

        th.innerHTML = `
            <div class="th-inner" title="${col.sortable ? '클릭 시 정렬 토글, 드래그 시 순서 변경' : ''}">
                <span class="th-title">${escapeHtml(col.label)}</span>
                ${sortIndicator}
            </div>
            <div class="col-resizer" title="드래그하여 열 너비 변경, 더블클릭 시 자동 맞춤"></div>
        `;

        // 1. 헤더 정렬 클릭 이벤트
        th.addEventListener('click', (e) => {
            if (e.target.closest('.col-resizer') || isResizing) return;
            handleHeaderSort(col.id);
        });

        // 2. 컬럼 리사이저 드래그 & 더블클릭 이벤트
        const resizer = th.querySelector('.col-resizer');
        resizer.addEventListener('mousedown', (e) => {
            initColumnResize(th, col.id, e);
        });
        resizer.addEventListener('dblclick', (e) => {
            e.stopPropagation();
            autoFitColumnWidth(col.id);
        });

        // 3. 컬럼 드래그 앤 드롭 (순서 변경) 이벤트
        th.addEventListener('dragstart', (e) => {
            draggedColId = col.id;
            th.classList.add('col-dragging');
            e.dataTransfer.effectAllowed = 'move';
            e.dataTransfer.setData('text/plain', col.id);
        });

        th.addEventListener('dragover', (e) => {
            e.preventDefault();
            e.dataTransfer.dropEffect = 'move';
            if (!draggedColId || draggedColId === col.id) return;

            const rect = th.getBoundingClientRect();
            const midpoint = rect.left + rect.width / 2;
            if (e.clientX < midpoint) {
                th.classList.add('drop-target-left');
                th.classList.remove('drop-target-right');
            } else {
                th.classList.add('drop-target-right');
                th.classList.remove('drop-target-left');
            }
        });

        th.addEventListener('dragleave', () => {
            th.classList.remove('drop-target-left', 'drop-target-right');
        });

        th.addEventListener('drop', (e) => {
            e.preventDefault();
            th.classList.remove('drop-target-left', 'drop-target-right');
            if (!draggedColId || draggedColId === col.id) return;

            const rect = th.getBoundingClientRect();
            const isLeft = e.clientX < (rect.left + rect.width / 2);
            reorderColumns(draggedColId, col.id, isLeft);
        });

        th.addEventListener('dragend', () => {
            th.classList.remove('col-dragging');
            draggedColId = null;
            document.querySelectorAll('.table-col-header').forEach(h => {
                h.classList.remove('drop-target-left', 'drop-target-right', 'col-dragging');
            });
        });

        // 4. 헤더 우클릭 컨텍스트 메뉴 (숨김/표시/초기화)
        th.addEventListener('contextmenu', (e) => {
            e.preventDefault();
            openHeaderContextMenu(col.id, e.clientX, e.clientY);
        });

        tr.appendChild(th);
    });

    elClientsTableHeader.appendChild(tr);
}

// 헤더 정렬 토글 핸들러
function handleHeaderSort(colId) {
    const col = COLUMNS_DEF.find(c => c.id === colId);
    if (!col || !col.sortable) return;

    if (tableConfig.sort && tableConfig.sort.key === colId) {
        if (tableConfig.sort.dir === 'ASC') {
            tableConfig.sort.dir = 'DESC';
        } else if (tableConfig.sort.dir === 'DESC') {
            tableConfig.sort = null; // 정렬 해제
        }
    } else {
        tableConfig.sort = { key: colId, dir: 'ASC' };
    }

    saveTableConfig();
    renderClientsView();
}

// 컬럼 순서 변경 핸들러
function reorderColumns(sourceColId, targetColId, placeBefore) {
    const order = [...tableConfig.order];
    const fromIdx = order.indexOf(sourceColId);
    if (fromIdx === -1) return;
    order.splice(fromIdx, 1);

    let toIdx = order.indexOf(targetColId);
    if (!placeBefore) {
        toIdx += 1;
    }
    order.splice(toIdx, 0, sourceColId);

    tableConfig.order = order;
    saveTableConfig();
    renderClientsView();
}

// 컬럼 너비 리사이즈 핸들러
function initColumnResize(th, colId, e) {
    e.preventDefault();
    e.stopPropagation();
    isResizing = true;

    const startX = e.clientX;
    const startWidth = th.offsetWidth;
    const colDef = COLUMNS_DEF.find(c => c.id === colId) || { minWidth: 80 };
    const resizer = th.querySelector('.col-resizer');
    if (resizer) resizer.classList.add('resizing');

    function onMouseMove(ev) {
        const delta = ev.clientX - startX;
        const newWidth = Math.max(colDef.minWidth, startWidth + delta);
        th.style.width = `${newWidth}px`;
        tableConfig.widths[colId] = newWidth;

        // 동일 열의 모든 셀 너비 동기화
        const cells = elClientsTableBody.querySelectorAll(`td[data-col-id="${colId}"]`);
        cells.forEach(td => td.style.width = `${newWidth}px`);
    }

    function onMouseUp() {
        document.removeEventListener('mousemove', onMouseMove);
        document.removeEventListener('mouseup', onMouseUp);
        if (resizer) resizer.classList.remove('resizing');
        setTimeout(() => { isResizing = false; }, 50);
        saveTableConfig();
    }

    document.addEventListener('mousemove', onMouseMove);
    document.addEventListener('mouseup', onMouseUp);
}

// 더블클릭 시 자동 맞춤 (Auto-fit)
function autoFitColumnWidth(colId) {
    const cells = elClientsTableBody.querySelectorAll(`td[data-col-id="${colId}"]`);
    const th = elClientsTableHeader.querySelector(`th[data-col-id="${colId}"]`);
    if (!th) return;

    let maxWidth = 100;
    cells.forEach(td => {
        maxWidth = Math.max(maxWidth, td.scrollWidth + 32);
    });
    maxWidth = Math.max(maxWidth, th.scrollWidth + 32);
    maxWidth = Math.min(Math.max(maxWidth, 100), 500);

    tableConfig.widths[colId] = maxWidth;
    th.style.width = `${maxWidth}px`;
    cells.forEach(td => td.style.width = `${maxWidth}px`);
    saveTableConfig();
}

// 클라이언트 목록 정렬 루틴
function sortClients(clients) {
    if (!tableConfig.sort || !tableConfig.sort.key) return clients;
    const { key, dir } = tableConfig.sort;

    return [...clients].sort((a, b) => {
        let valA, valB;

        if (key === 'status') {
            const priority = { 'IMAGING': 0, 'READY': 1, 'CONNECTED': 1, 'SUCCESS': 2, 'FAILED': 3 };
            valA = priority[a.status] !== undefined ? priority[a.status] : 9;
            valB = priority[b.status] !== undefined ? priority[b.status] : 9;
        } else if (key === 'model') {
            valA = `${a.system?.manufacturer || ''} ${a.system?.modelName || ''}`.trim();
            valB = `${b.system?.manufacturer || ''} ${b.system?.modelName || ''}`.trim();
        } else if (key === 'serial') {
            valA = a.system?.serialNumber || '';
            valB = b.system?.serialNumber || '';
        } else if (key === 'network') {
            valA = a.network?.ipAddress || a.network?.primaryMac || '';
            valB = b.network?.ipAddress || b.network?.primaryMac || '';
        } else if (key === 'disk') {
            valA = a.disks?.[0]?.sizeGb || 0;
            valB = b.disks?.[0]?.sizeGb || 0;
        } else if (key === 'policy') {
            valA = a.matchedPolicy?.name || '';
            valB = b.matchedPolicy?.name || '';
        } else if (key === 'image') {
            valA = a.matchedImage?.targetImage?.imageFileName || a.matchedImage?.imageFileName || '';
            valB = b.matchedImage?.targetImage?.imageFileName || b.matchedImage?.imageFileName || '';
        } else if (key === 'progress') {
            valA = a.progress ? a.progress.percent : (a.status === 'SUCCESS' ? 100 : 0);
            valB = b.progress ? b.progress.percent : (b.status === 'SUCCESS' ? 100 : 0);
        } else {
            valA = '';
            valB = '';
        }

        let res = 0;
        if (typeof valA === 'number' && typeof valB === 'number') {
            res = valA - valB;
        } else {
            res = String(valA).localeCompare(String(valB), 'ko-KR', { numeric: true, sensitivity: 'base' });
        }

        return dir === 'DESC' ? -res : res;
    });
}

// 테이블 데이터 바디 행 렌더링
function renderTableBody(clients) {
    const visibleCols = getVisibleColumns();
    const sorted = sortClients(clients);
    elClientsTableBody.innerHTML = '';

    sorted.forEach(c => {
        const tr = document.createElement('tr');
        tr.className = `client-row row-${(c.status || 'ready').toLowerCase()}`;
        tr.id = `trow-${c.sessionId}`;

        const modelName = c.system?.modelName || '미확인 모델 (Generic)';
        const manufacturer = c.system?.manufacturer || '알 수 없음';
        const serial = c.system?.serialNumber || 'N/A';
        const mac = c.network?.primaryMac || '00:00:00:00:00:00';
        const ip = c.network?.ipAddress || '127.0.0.1';

        const disk0 = (c.disks && c.disks.length > 0) ? c.disks[0] : null;
        const diskSummary = disk0 
            ? `[Disk 0] ${disk0.friendlyName || 'SSD'} (${disk0.busType || 'NVMe'}, ${(disk0.sizeGb || 0).toFixed(0)} GB)` 
            : '장착된 디스크 없음';

        const policyName = c.matchedPolicy?.name || '맞춤 권장양식 자동 매칭';
        const imageName = c.matchedImage?.targetImage?.imageFileName || c.matchedImage?.imageFileName || 'Win11_Universal.wim';

        let statusText = '승인 대기';
        let badgeClass = 'badge-ready';
        if (c.status === 'IMAGING') {
            statusText = '더빙 진행 중';
            badgeClass = 'badge-imaging';
        } else if (c.status === 'SUCCESS') {
            statusText = '더빙 완료';
            badgeClass = 'badge-success';
        } else if (c.status === 'FAILED') {
            statusText = '더빙 실패';
            badgeClass = 'badge-failed';
        }

        const pct = c.progress ? Math.round(c.progress.percent) : (c.status === 'SUCCESS' ? 100 : 0);
        const stepText = c.progress ? c.progress.step : (c.status === 'SUCCESS' ? '배포 완료' : (c.status === 'FAILED' ? '오류' : '대기 중'));
        const speedText = c.progress && c.progress.speedMbps ? `${c.progress.speedMbps} MB/s` : '';
        const remainText = c.progress && c.progress.remainingSeconds > 0 ? `${c.progress.remainingSeconds}s` : '';

        visibleCols.forEach(col => {
            const td = document.createElement('td');
            td.className = 'table-col-cell';
            td.dataset.colId = col.id;
            const curWidth = tableConfig.widths[col.id] || col.defaultWidth;
            td.style.width = `${curWidth}px`;

            switch (col.id) {
                case 'status':
                    td.innerHTML = `
                        <span class="status-badge ${badgeClass}" id="tbadge-${c.sessionId}">
                            <span class="live-dot" style="width:7px;height:7px;"></span>
                            ${statusText}
                        </span>
                    `;
                    break;

                case 'model':
                    td.innerHTML = `
                        <div class="table-model-box" title="${escapeHtml(manufacturer)} ${escapeHtml(modelName)}">
                            <span class="table-mfg-tag">${escapeHtml(manufacturer)}</span>
                            <span class="table-model-name">${escapeHtml(modelName)}</span>
                        </div>
                    `;
                    break;

                case 'serial':
                    td.innerHTML = `<span class="table-mono-text" title="${escapeHtml(serial)}">${escapeHtml(serial)}</span>`;
                    break;

                case 'network':
                    td.innerHTML = `
                        <div style="display:flex;flex-direction:column;gap:1px;" title="IP: ${escapeHtml(ip)} / MAC: ${escapeHtml(mac)}">
                            <span class="table-mono-text" style="color:var(--text-main);">${escapeHtml(ip)}</span>
                            <span class="table-mono-text" style="font-size:0.75rem;">${escapeHtml(mac)}</span>
                        </div>
                    `;
                    break;

                case 'disk':
                    td.innerHTML = `<div class="table-disk-info" title="${escapeHtml(diskSummary)}">${escapeHtml(diskSummary)}</div>`;
                    break;

                case 'policy':
                    td.innerHTML = `<span class="policy-badge" title="${escapeHtml(policyName)}">${escapeHtml(policyName)}</span>`;
                    break;

                case 'image':
                    td.innerHTML = `
                        <span class="target-wim-badge" style="font-size:0.75rem;" title="${escapeHtml(imageName)}">
                            📀 ${escapeHtml(imageName)}
                        </span>
                    `;
                    break;

                case 'progress':
                    td.innerHTML = `
                        <div class="table-progress-box" id="tprog-${c.sessionId}">
                            <div class="table-progress-meta">
                                <span id="tpstep-${c.sessionId}" style="font-weight:600;color:#38bdf8;">${escapeHtml(stepText)}</span>
                                <span id="tppct-${c.sessionId}" style="font-weight:700;color:#fff;">${pct}%</span>
                            </div>
                            <div class="table-progress-bar-track">
                                <div class="table-progress-bar-fill" id="tpbar-${c.sessionId}" style="width:${pct}%;"></div>
                            </div>
                            <div class="table-progress-meta" style="font-size:0.7rem;">
                                <span id="tpspeed-${c.sessionId}">${speedText}</span>
                                <span id="tpremain-${c.sessionId}">${remainText}</span>
                            </div>
                        </div>
                    `;
                    break;

                case 'actions':
                    td.innerHTML = `
                        <div class="table-actions">
                            ${(c.status === 'READY' || c.status === 'CONNECTED') ? `
                                <button class="btn btn-primary btn-sm btn-approve" style="padding:4px 8px;font-size:0.75rem;" onclick="approveDeploy('${c.sessionId}')" title="지금 즉시 더빙을 시작합니다">
                                    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" style="width:12px;height:12px;"><polygon points="5 3 19 12 5 21 5 3"></polygon></svg>
                                    승인
                                </button>
                            ` : ''}
                            <button class="btn btn-secondary btn-sm" style="padding:4px 8px;font-size:0.75rem;" onclick="openClientLog('${c.sessionId}')">
                                로그
                            </button>
                        </div>
                    `;
                    break;
            }

            tr.appendChild(td);
        });

        elClientsTableBody.appendChild(tr);
    });
}

// 실시간 클라이언트 프로그레스 바 갱신 (테이블 & 카드 동시 반영)
function updateClientProgressUI(client) {
    const pct = Math.round(client.progress.percent);

    // 1. 카드 뷰 갱신
    const pbar = document.getElementById(`pbar-${client.sessionId}`);
    const ppct = document.getElementById(`ppct-${client.sessionId}`);
    const pstep = document.getElementById(`pstep-${client.sessionId}`);
    const pspeed = document.getElementById(`pspeed-${client.sessionId}`);
    const premain = document.getElementById(`premain-${client.sessionId}`);

    if (pbar) {
        pbar.style.width = `${pct}%`;
        if (ppct) ppct.textContent = `${pct}%`;
        if (pstep) pstep.textContent = client.progress.step;
        if (pspeed && client.progress.speedMbps) pspeed.textContent = `${client.progress.speedMbps} MB/s`;
        if (premain && client.progress.remainingSeconds > 0) premain.textContent = `남은 시간: ${client.progress.remainingSeconds}초`;
    }

    // 2. 테이블 뷰 갱신
    const tpbar = document.getElementById(`tpbar-${client.sessionId}`);
    const tppct = document.getElementById(`tppct-${client.sessionId}`);
    const tpstep = document.getElementById(`tpstep-${client.sessionId}`);
    const tpspeed = document.getElementById(`tpspeed-${client.sessionId}`);
    const tpremain = document.getElementById(`tpremain-${client.sessionId}`);

    if (tpbar) {
        tpbar.style.width = `${pct}%`;
        if (tppct) tppct.textContent = `${pct}%`;
        if (tpstep) tpstep.textContent = client.progress.step;
        if (tpspeed && client.progress.speedMbps) tpspeed.textContent = `${client.progress.speedMbps} MB/s`;
        if (tpremain && client.progress.remainingSeconds > 0) tpremain.textContent = `${client.progress.remainingSeconds}s`;
    }

    // 상태 뱃지 동기화
    const tbadge = document.getElementById(`tbadge-${client.sessionId}`);
    if (tbadge && !tbadge.classList.contains('badge-imaging')) {
        tbadge.className = 'status-badge badge-imaging';
        tbadge.innerHTML = '<span class="live-dot" style="width:7px;height:7px;"></span> 더빙 진행 중';
    }
}

// --------------------------------------------------------------------------
// 컬럼 숨김/표시 메뉴 및 우클릭 컨텍스트 메뉴
// --------------------------------------------------------------------------
function renderColumnSettingsMenu() {
    elColumnCheckboxList.innerHTML = '';
    const hiddenSet = new Set(tableConfig.hidden || []);

    COLUMNS_DEF.forEach(col => {
        const item = document.createElement('label');
        item.className = 'column-check-item';

        const isVisible = !hiddenSet.has(col.id);
        item.innerHTML = `
            <input type="checkbox" value="${col.id}" ${isVisible ? 'checked' : ''}>
            <span>${escapeHtml(col.label)}</span>
        `;

        const checkbox = item.querySelector('input');
        checkbox.addEventListener('change', () => {
            toggleColumnVisibility(col.id, checkbox.checked);
        });

        elColumnCheckboxList.appendChild(item);
    });
}

function toggleColumnVisibility(colId, visible) {
    const hiddenSet = new Set(tableConfig.hidden || []);
    if (visible) {
        hiddenSet.delete(colId);
    } else {
        // 최소 1개는 보여야 함
        const remainingCount = COLUMNS_DEF.length - hiddenSet.size;
        if (remainingCount <= 1) {
            alert('최소 하나의 열은 항상 표시되어야 합니다.');
            renderColumnSettingsMenu();
            return;
        }
        hiddenSet.add(colId);
    }

    tableConfig.hidden = Array.from(hiddenSet);
    saveTableConfig();
    renderClientsView();
}

// 헤더 우클릭 컨텍스트 메뉴
function openHeaderContextMenu(colId, x, y) {
    contextTargetColId = colId;
    elHeaderContextMenu.style.left = `${Math.min(x, window.innerWidth - 200)}px`;
    elHeaderContextMenu.style.top = `${Math.min(y, window.innerHeight - 150)}px`;
    elHeaderContextMenu.classList.remove('hidden');
}

function closeHeaderContextMenu() {
    elHeaderContextMenu.classList.add('hidden');
    contextTargetColId = null;
}

// --------------------------------------------------------------------------
// 클라이언트 카드 렌더링 (카드 뷰 모드용)
// --------------------------------------------------------------------------
function renderClientsGrid(clients) {
    elClientsGrid.innerHTML = '';
    const list = clients || getFilteredClients();

    list.forEach(c => {
        const card = createClientCardElement(c);
        elClientsGrid.appendChild(card);
    });
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
    renderClientsView();
});

elSelectStatus.addEventListener('change', (e) => {
    statusFilter = e.target.value;
    renderClientsView();
});

// 뷰 모드 토글 (테이블 양식 vs 카드 양식)
elBtnViewTable.addEventListener('click', () => {
    currentViewMode = 'TABLE';
    elBtnViewTable.classList.add('active');
    elBtnViewCards.classList.remove('active');
    renderClientsView();
});

elBtnViewCards.addEventListener('click', () => {
    currentViewMode = 'CARDS';
    elBtnViewCards.classList.add('active');
    elBtnViewTable.classList.remove('active');
    renderClientsView();
});

// 열 설정 드롭다운 메뉴 토글
elBtnToggleColumnSettings.addEventListener('click', (e) => {
    e.stopPropagation();
    elMenuColumnSettings.classList.toggle('hidden');
});

elMenuColumnSettings.addEventListener('click', (e) => {
    e.stopPropagation();
});

elBtnResetTableConfig.addEventListener('click', () => {
    if (confirm('테이블의 열 순서, 너비, 숨김 설정을 기본값으로 복원하시겠습니까?')) {
        tableConfig = getDefaultTableConfig();
        saveTableConfig();
        renderClientsView();
        elMenuColumnSettings.classList.add('hidden');
    }
});

// 헤더 우클릭 컨텍스트 메뉴 액션
elCtxHideColumn.addEventListener('click', () => {
    if (contextTargetColId) {
        toggleColumnVisibility(contextTargetColId, false);
    }
    closeHeaderContextMenu();
});

elCtxShowAllColumns.addEventListener('click', () => {
    tableConfig.hidden = [];
    saveTableConfig();
    renderClientsView();
    closeHeaderContextMenu();
});

elCtxResetConfig.addEventListener('click', () => {
    tableConfig = getDefaultTableConfig();
    saveTableConfig();
    renderClientsView();
    closeHeaderContextMenu();
});

// 외부 클릭 시 열 메뉴 및 컨텍스트 메뉴 닫기
document.addEventListener('click', (e) => {
    if (!elMenuColumnSettings.contains(e.target) && e.target !== elBtnToggleColumnSettings) {
        elMenuColumnSettings.classList.add('hidden');
    }
    closeHeaderContextMenu();
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

