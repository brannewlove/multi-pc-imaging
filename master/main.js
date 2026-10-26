const { app, BrowserWindow, Menu, dialog } = require('electron');
const path = require('path');
const http = require('http');

let mainWindow = null;
const SERVER_PORT = 39202;
const SERVER_URL = `http://localhost:${SERVER_PORT}`;

// 1. 마스터 서버 로드 (Electron 프로세스 내부에서 직접 구동)
function startMasterServer() {
    try {
        console.log('[Electron] 마스터 백엔드 서버 구동 중...');
        require('./server.js');
        console.log('[Electron] 마스터 백엔드 서버 구동 완료');
    } catch (e) {
        console.error('[-] 마스터 백엔드 서버 구동 오류:', e.message);
    }
}

// 2. 서버 포트 응답 대기
function waitForServer(timeoutMs = 10000) {
    return new Promise((resolve) => {
        const start = Date.now();
        const check = () => {
            http.get(SERVER_URL, (res) => {
                if (res.statusCode === 200 || res.statusCode === 304) {
                    resolve(true);
                } else if (Date.now() - start < timeoutMs) {
                    setTimeout(check, 250);
                } else {
                    resolve(false);
                }
            }).on('error', () => {
                if (Date.now() - start < timeoutMs) {
                    setTimeout(check, 250);
                } else {
                    resolve(false);
                }
            });
        };
        check();
    });
}

// 3. 메인 윈도우 생성
function createMainWindow() {
    mainWindow = new BrowserWindow({
        width: 1560,
        height: 960,
        minWidth: 1100,
        minHeight: 700,
        backgroundColor: '#07090e',
        title: 'Multi-Deploy OS | 다중 PC 이미지 더빙 관제 플랫폼 (실행 앱)',
        show: false,
        webPreferences: {
            nodeIntegration: false,
            contextIsolation: true,
            sandbox: false
        }
    });

    const menuTemplate = [
        {
            label: '보기',
            submenu: [
                { label: '새로고침', accelerator: 'CmdOrCtrl+R', click: () => mainWindow.reload() },
                { label: '강제 새로고침', accelerator: 'CmdOrCtrl+Shift+R', click: () => mainWindow.webContents.reloadIgnoringCache() },
                { type: 'separator' },
                { label: '전체 화면', accelerator: 'F11', click: () => mainWindow.setFullScreen(!mainWindow.isFullScreen()) },
                { label: '개발자 도구 토글', accelerator: 'F12', click: () => mainWindow.webContents.toggleDevTools() }
            ]
        },
        {
            label: '도움말',
            submenu: [
                {
                    label: '시스템 정보',
                    click: () => {
                        dialog.showMessageBox(mainWindow, {
                            type: 'info',
                            title: 'Multi-Deploy OS',
                            message: '다중 PC 이미지 더빙 관제 플랫폼 (데스크톱 실행 앱)',
                            detail: `버전: 1.0.0\n관제 웹 대시보드 포트: ${SERVER_PORT}\n타깃 WinPE TCP 소켓: 39201\nUDP 브로드캐스트 탐색: 39200`
                        });
                    }
                }
            ]
        }
    ];

    const menu = Menu.buildFromTemplate(menuTemplate);
    Menu.setApplicationMenu(menu);

    mainWindow.loadURL(SERVER_URL);

    mainWindow.once('ready-to-show', () => {
        mainWindow.show();
    });

    mainWindow.on('closed', () => {
        mainWindow = null;
    });
}

// 4. 앱 라이프사이클
app.whenReady().then(async () => {
    startMasterServer();
    await waitForServer(8000);
    createMainWindow();

    app.on('activate', () => {
        if (BrowserWindow.getAllWindows().length === 0) {
            createMainWindow();
        }
    });
});

app.on('window-all-closed', () => {
    if (process.platform !== 'darwin') {
        app.quit();
    }
});
