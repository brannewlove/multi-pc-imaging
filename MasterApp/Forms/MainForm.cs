using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiDeployOS.Master.Models;
using MultiDeployOS.Master.Services;

namespace MultiDeployOS.Master.Forms
{
    public class MainForm : Form
    {
        private readonly ConfigManager _configManager;
        private readonly UdpDiscoveryService _udpService;
        private readonly TcpServerService _tcpService;
        private readonly PxeServerCoordinator _pxeCoordinator;
        private Button _btnPxeServer = null!;

        private bool _globalAutoApprove = false;

        // 헤더 및 요약 컨트롤
        private Label _lblServerStatus;
        private Button _btnAutoApproveToggle;
        private Button _btnBatchApprove;
        private Label _valTotal, _valPending, _valActive, _valSuccess, _valFailed;

        // 검색 및 필터
        private TextBox _txtSearch;
        private ComboBox _cboStatusFilter;

        // UI 패널 및 탭 제어
        private Panel _pnlContent;
        private Button _btnTabLive;
        private Button _btnTabHistory;
        private Button _btnDeleteHistory;

        // DataGridView
        private DataGridView _gridClients;
        private DataGridView _gridHistory;
        private ContextMenuStrip _headerContextMenu;
        private ContextMenuStrip _rowContextMenu;
        private ContextMenuStrip _historyContextMenu;
        private int _contextClickedColumnIndex = -1;
        private string _sortColumn = "";
        private SortOrder _sortOrder = SortOrder.None;

        // UI 설정 복원/저장 플래그 및 디바운스 타이머
        private bool _isApplyingUiSettings = true;
        private System.Windows.Forms.Timer? _columnSaveDebounceTimer;

        // 내부 상태 캐시
        private readonly Dictionary<string, ClientSession> _sessions = new();
        private System.Windows.Forms.Timer _uiRefreshTimer;

        public MainForm()
        {
            _isApplyingUiSettings = true;
            _configManager = new ConfigManager();
            _udpService = new UdpDiscoveryService();
            _tcpService = new TcpServerService();

            InitializeComponent();
            ApplySavedUiSettings();
            _isApplyingUiSettings = false;

            // 백그라운드 서버 이벤트 바인딩
            _tcpService.ClientRegistered += OnClientRegistered;
            _tcpService.ClientProgressUpdated += OnClientProgressUpdated;
            _tcpService.ClientCompleted += OnClientCompleted;
            _tcpService.ClientDisconnected += OnClientDisconnected;

            // 주기적 UI 갱신 타이머 (소요시간 등)
            _uiRefreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _uiRefreshTimer.Tick += (s, e) => RefreshGridRows();
            _uiRefreshTimer.Start();

            // 서버 구동
            _udpService.Start();
            _tcpService.Start();

            // PXE / DHCP 초고속 부팅 코디네이터 초기화 및 자동 가동 연동
            _pxeCoordinator = new PxeServerCoordinator(_configManager);
            _pxeCoordinator.ClientBootUpdated += OnPxeClientBootUpdated;
            _pxeCoordinator.RunningStateChanged += _ =>
            {
                if (this.IsHandleCreated && !this.IsDisposed)
                {
                    this.BeginInvoke(new Action(UpdateServerStatusBanner));
                }
            };

            if (_configManager.ServerConfig.Pxe.Enabled)
            {
                _pxeCoordinator.Start();
            }
            UpdateServerStatusBanner();
        }

        private void InitializeComponent()
        {
            this.Text = "MultiDeployOS Master - 다중 PC 이미지 배포 관제 시스템";
            this.Font = new Font("맑은 고딕", 9.5f);
            this.BackColor = Color.FromArgb(17, 24, 39);
            this.ForeColor = Color.White;
            this.AutoScaleMode = AutoScaleMode.Dpi;

            // 다양한 외부 모니터 및 노트북 해상도(DPI 125%/150%)에 맞게 반응형 기본 크기 산출
            Rectangle workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1366, 768);
            int targetW = Math.Min(1400, Math.Max(1024, workArea.Width - 40));
            int targetH = Math.Min(860, Math.Max(600, workArea.Height - 60));
            this.Size = new Size(targetW, targetH);
            this.MinimumSize = new Size(960, 560);
            this.StartPosition = FormStartPosition.CenterScreen;

            // 앱 아이콘 적용
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (File.Exists(iconPath))
                {
                    this.Icon = new Icon(iconPath);
                }
                else
                {
                    var exeIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                    if (exeIcon != null) this.Icon = exeIcon;
                }
            }
            catch { }

            // ====================================================================
            // 1. 최상단 통합 고정 패널 (헤더 + 메트릭 카드 + 툴바)
            // ====================================================================
            var pnlTopContainer = new Panel
            {
                Dock = DockStyle.Top,
                Height = 196,
                BackColor = Color.FromArgb(15, 23, 42)
            };

            // 1-1. 헤더 바 (로고, 서버 상태, 전역 조작 버튼)
            var pnlHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 58,
                BackColor = Color.FromArgb(15, 23, 42),
                Padding = new Padding(16, 8, 16, 8)
            };

            var pnlHeaderLeft = new FlowLayoutPanel
            {
                Dock = DockStyle.Left,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 4, 0, 0)
            };

            var lblLogo = new Label
            {
                Text = "⚡ MultiDeployOS Master",
                Font = new Font("맑은 고딕", 12f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248),
                AutoSize = true,
                Margin = new Padding(0, 6, 16, 0)
            };
            _lblServerStatus = new Label
            {
                Text = "● 에이전트: 39200/39201 | PXE 부팅 서버: 중지됨",
                Font = new Font("맑은 고딕", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(52, 211, 153),
                AutoSize = true,
                Margin = new Padding(0, 10, 0, 0)
            };
            pnlHeaderLeft.FlowDirection = FlowDirection.TopDown;
            pnlHeaderLeft.Padding = new Padding(0, 0, 0, 0);
            lblLogo.Margin = new Padding(0, 0, 0, 1);
            _lblServerStatus.Font = new Font("맑은 고딕", 8.25f, FontStyle.Bold);
            _lblServerStatus.Margin = new Padding(2, 0, 0, 0);
            pnlHeaderLeft.Controls.Add(lblLogo);
            pnlHeaderLeft.Controls.Add(_lblServerStatus);
            pnlHeader.Controls.Add(pnlHeaderLeft);

            var pnlTopButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false, // 수직 줄바꿈 방지
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 6, 0, 0)
            };

            _btnAutoApproveToggle = CreateButton("⚪ 자동 승인: OFF", Color.FromArgb(71, 85, 105));
            _btnAutoApproveToggle.Click += ToggleAutoApprove;

            _btnBatchApprove = CreateButton("✔ 대기 PC 일괄 승인", Color.FromArgb(14, 165, 233));
            _btnBatchApprove.Click += BatchApproveAll;

            var btnSelectImage = CreateButton("📀 이미지 별도 선택", Color.FromArgb(16, 185, 129));
            btnSelectImage.Click += (s, e) => OpenSelectImageDialog();

            var btnOpenPolicies = CreateButton("🗂️ 파티션 권장양식", Color.FromArgb(51, 65, 85));
            btnOpenPolicies.Click += (s, e) =>
            {
                using var form = new PolicyEditorForm(_configManager);
                form.ShowDialog(this);
            };

            var btnOpenImageRules = CreateButton("🎯 이미지 선택 규칙", Color.FromArgb(51, 65, 85));
            btnOpenImageRules.Click += (s, e) =>
            {
                using var form = new ImageRuleEditorForm(_configManager);
                form.ShowDialog(this);
            };

            var btnExport = CreateButton("📤 설정 백업", Color.FromArgb(71, 85, 105));
            btnExport.Click += ExportSettings;

            var btnImport = CreateButton("📥 설정 복원", Color.FromArgb(71, 85, 105));
            btnImport.Click += ImportSettings;

            _btnPxeServer = CreateButton("🌐 PXE 서버: OFF", Color.FromArgb(51, 65, 85));
            _btnPxeServer.Click += (s, e) => OpenPxeSettingsDialog();

            pnlTopButtons.Controls.Add(_btnPxeServer);
            pnlTopButtons.Controls.Add(_btnAutoApproveToggle);
            pnlTopButtons.Controls.Add(_btnBatchApprove);
            pnlTopButtons.Controls.Add(btnSelectImage);
            pnlTopButtons.Controls.Add(btnOpenPolicies);
            pnlTopButtons.Controls.Add(btnOpenImageRules);
            pnlTopButtons.Controls.Add(btnExport);
            pnlTopButtons.Controls.Add(btnImport);

            pnlHeader.Controls.Add(pnlTopButtons);

            // 1-2. 메트릭 요약 카드 바 (5개 지표 균등 배치, 숫자 잘림 방지 86px 확보)
            var pnlMetrics = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 86,
                ColumnCount = 5,
                BackColor = Color.FromArgb(24, 30, 44),
                Padding = new Padding(12, 6, 12, 6)
            };
            for (int i = 0; i < 5; i++) pnlMetrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));

            pnlMetrics.Controls.Add(CreateMetricCard("총 연결 PC", _valTotal = new Label(), Color.FromArgb(147, 197, 253)), 0, 0);
            pnlMetrics.Controls.Add(CreateMetricCard("승인 대기", _valPending = new Label(), Color.FromArgb(253, 224, 71)), 1, 0);
            pnlMetrics.Controls.Add(CreateMetricCard("이미지 더빙 진행", _valActive = new Label(), Color.FromArgb(192, 132, 252)), 2, 0);
            pnlMetrics.Controls.Add(CreateMetricCard("배포 완료 (성공)", _valSuccess = new Label(), Color.FromArgb(74, 222, 128)), 3, 0);
            pnlMetrics.Controls.Add(CreateMetricCard("오류 / 실패", _valFailed = new Label(), Color.FromArgb(248, 113, 113)), 4, 0);

            // 1-3. 툴바 (탭 버튼 좌측 도킹, 검색/필터 우측 도킹 반응형)
            var pnlToolbar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 52,
                BackColor = Color.FromArgb(30, 41, 59),
                Padding = new Padding(12, 6, 12, 6)
            };

            var pnlToolbarLeft = new FlowLayoutPanel
            {
                Dock = DockStyle.Left,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 2, 0, 0)
            };

            _btnTabLive = new Button
            {
                Text = "🖥️ 실시간 타깃 PC 관리",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(210, 34),
                Padding = new Padding(14, 0, 14, 0),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(14, 165, 233),
                ForeColor = Color.White,
                Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 8, 0)
            };
            _btnTabLive.FlatAppearance.BorderSize = 0;
            _btnTabLive.Click += (s, e) => SwitchTab(true);

            _btnTabHistory = new Button
            {
                Text = "📜 배포 완료 이력 (0)",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(190, 34),
                Padding = new Padding(14, 0, 14, 0),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.FromArgb(203, 213, 225),
                Font = new Font("맑은 고딕", 9.5f),
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 8, 0)
            };
            _btnTabHistory.FlatAppearance.BorderSize = 0;
            _btnTabHistory.Click += (s, e) => SwitchTab(false);

            pnlToolbarLeft.Controls.Add(_btnTabLive);
            pnlToolbarLeft.Controls.Add(_btnTabHistory);

            var pnlToolbarRight = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 4, 0, 0)
            };

            _btnDeleteHistory = CreateButton("🗑️ 선택 이력 삭제", Color.FromArgb(239, 68, 68));
            _btnDeleteHistory.Height = 32;
            _btnDeleteHistory.Visible = false;
            _btnDeleteHistory.Margin = new Padding(0, 0, 12, 0);
            _btnDeleteHistory.Click += (s, e) => DeleteSelectedHistory();

            var lblSearch = new Label
            {
                Text = "🔍 검색:",
                AutoSize = true,
                ForeColor = Color.FromArgb(148, 163, 184),
                Margin = new Padding(0, 6, 4, 0)
            };
            _txtSearch = new TextBox
            {
                Width = 180,
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("맑은 고딕", 9.5f),
                Margin = new Padding(0, 2, 12, 0)
            };
            _txtSearch.TextChanged += (s, e) => RefreshGridRows();

            var lblStatus = new Label
            {
                Text = "상태 필터:",
                AutoSize = true,
                ForeColor = Color.FromArgb(148, 163, 184),
                Margin = new Padding(0, 6, 4, 0)
            };
            _cboStatusFilter = new ComboBox
            {
                Width = 125,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(0, 2, 12, 0)
            };
            _cboStatusFilter.Items.AddRange(new object[] { "전체 상태", "PXE 부팅 중", "승인 대기", "배포 진행 중", "배포 성공", "배포 실패" });
            _cboStatusFilter.SelectedIndex = 0;
            _cboStatusFilter.SelectedIndexChanged += (s, e) => RefreshGridRows();

            var btnColumnConfig = CreateButton("⚙ 테이블 열 설정", Color.FromArgb(51, 65, 85));
            btnColumnConfig.Height = 32;
            btnColumnConfig.Margin = new Padding(0, 0, 0, 0);
            btnColumnConfig.Click += (s, e) => ShowColumnSettingsDialog();

            pnlToolbarRight.Controls.Add(_btnDeleteHistory);
            pnlToolbarRight.Controls.Add(lblSearch);
            pnlToolbarRight.Controls.Add(_txtSearch);
            pnlToolbarRight.Controls.Add(lblStatus);
            pnlToolbarRight.Controls.Add(_cboStatusFilter);
            pnlToolbarRight.Controls.Add(btnColumnConfig);

            pnlToolbar.Controls.Add(pnlToolbarRight);
            pnlToolbar.Controls.Add(pnlToolbarLeft);

            // TopContainer에 순서대로 Docking: Toolbar가 아래, Metrics가 중간, Header가 최상단
            pnlTopContainer.Controls.Add(pnlToolbar);
            pnlTopContainer.Controls.Add(pnlMetrics);
            pnlTopContainer.Controls.Add(pnlHeader);

            // ====================================================================
            // 2. 메인 데이터 그리드 영역 (DockStyle.Fill)
            // ====================================================================
            _pnlContent = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(15, 23, 42)
            };

            InitLiveGrid(_pnlContent);
            InitHistoryGrid(_pnlContent);

            // 초기 뷰 설정
            _gridClients.Visible = true;
            _gridHistory.Visible = false;

            // ★ 핵심: WinForms 도킹 계산 시 DockStyle.Fill 컨트롤이 상단 패널 아래로 내려앉도록
            // pnlTopContainer를 먼저 추가하고, _pnlContent를 추가한 뒤 _pnlContent.BringToFront() 호출!
            this.Controls.Add(pnlTopContainer);
            this.Controls.Add(_pnlContent);
            _pnlContent.BringToFront();

            // 헤더 우클릭 컨텍스트 메뉴 생성
            _headerContextMenu = new ContextMenuStrip();
            var itemHide = _headerContextMenu.Items.Add("이 열 숨기기");
            itemHide.Click += (s, e) => HideClickedColumn();

            var itemShowAll = _headerContextMenu.Items.Add("모든 열 다시 표시");
            itemShowAll.Click += (s, e) => ShowAllColumns();

            _headerContextMenu.Items.Add(new ToolStripSeparator());
            var itemReset = _headerContextMenu.Items.Add("열 순서 및 너비 초기화");
            itemReset.Click += (s, e) => ResetColumnConfig();

            // 클라이언트 행 우클릭 컨텍스트 메뉴 생성
            _rowContextMenu = new ContextMenuStrip();
            var itemApprove = _rowContextMenu.Items.Add("▶ 배포 승인 / 재시도");
            itemApprove.Click += (s, e) =>
            {
                if (_gridClients.SelectedRows.Count > 0 && _gridClients.SelectedRows[0].Tag is ClientSession session)
                {
                    ApproveClient(session);
                }
            };

            var itemResetState = _rowContextMenu.Items.Add("🔄 승인 대기(READY)로 상태 초기화");
            itemResetState.Click += (s, e) =>
            {
                if (_gridClients.SelectedRows.Count > 0 && _gridClients.SelectedRows[0].Tag is ClientSession session)
                {
                    session.Status = "READY";
                    _pxeCoordinator.ClearMacDeployCompleted(session.Network.PrimaryMac);
                    session.Result = null;
                    session.Progress = null;
                    RefreshGridRows();
                    UpdateMetrics();
                }
            };

            _rowContextMenu.Items.Add(new ToolStripSeparator());

            var itemSelectImage = _rowContextMenu.Items.Add("📀 배포 이미지 별도 선택 (수동 변경)...");
            itemSelectImage.Click += (s, e) => OpenSelectImageDialog();

            var itemResetImage = _rowContextMenu.Items.Add("⚡ 기본 자동선택으로 재설정 (디폴트 복귀)");
            itemResetImage.Click += (s, e) => ResetSelectedClientsToAutoImage();

            _rowContextMenu.Items.Add(new ToolStripSeparator());

            var itemDetail = _rowContextMenu.Items.Add("🔍 상세 오류 및 진행 상태 확인");
            itemDetail.Click += (s, e) =>
            {
                if (_gridClients.SelectedRows.Count > 0 && _gridClients.SelectedRows[0].Tag is ClientSession session)
                {
                    string errMsg = string.IsNullOrEmpty(session.Result?.ErrorMessage) ? "(오류 메시지 없음)" : session.Result.ErrorMessage;
                    string customTag = session.IsCustomImageSelected ? $" [별도지정: {session.CustomImageFileName}]" : " [자동선택: 최신 WIM]";
                    string info = $"[PC 정보]\nIP: {session.IpAddress}\n모델: {session.System.Manufacturer} {session.System.ModelName}\n시리얼: {session.System.SerialNumber}\nMAC: {session.Network.PrimaryMac}\n\n[배포 상태]\n현재 상태: {session.Status}\n진행 단계: {session.Progress?.Step ?? "-"}\n진행률: {(int)(session.Progress?.Percent ?? 0)}%\n적용 이미지: {session.EffectiveImageFileName}{customTag}\n\n[오류 상세 내용]\n{errMsg}";
                    MessageBox.Show(info, $"배포 상태 상세 - {session.System.ModelName}", MessageBoxButtons.OK, session.Status == "FAILED" ? MessageBoxIcon.Error : MessageBoxIcon.Information);
                }
            };

            _rowContextMenu.Items.Add(new ToolStripSeparator());

            var itemRemove = _rowContextMenu.Items.Add("❌ 목록에서 세션 제거");
            itemRemove.Click += (s, e) =>
            {
                if (_gridClients.SelectedRows.Count > 0 && _gridClients.SelectedRows[0].Tag is ClientSession session)
                {
                    _sessions.Remove(session.SessionId);
                    RefreshGridRows();
                    UpdateMetrics();
                }
            };
        }

        private void OpenSelectImageDialog()
        {
            var selectedSessions = _gridClients.SelectedRows
                .Cast<DataGridViewRow>()
                .Select(r => r.Tag as ClientSession)
                .Where(s => s != null)
                .Cast<ClientSession>()
                .ToList();

            if (selectedSessions.Count == 0)
            {
                MessageBox.Show("이미지를 선택할 대상 PC를 목록에서 먼저 선택해주세요.", "PC 선택 필요", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var dlg = new SelectImageForm(_configManager, selectedSessions);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                RefreshGridRows();
                UpdateMetrics();
            }
        }

        private void ResetSelectedClientsToAutoImage()
        {
            var selectedSessions = _gridClients.SelectedRows
                .Cast<DataGridViewRow>()
                .Select(r => r.Tag as ClientSession)
                .Where(s => s != null)
                .Cast<ClientSession>()
                .ToList();

            if (selectedSessions.Count == 0)
            {
                MessageBox.Show("기본 자동선택으로 재설정할 대상 PC를 목록에서 먼저 선택해주세요.", "PC 선택 필요", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            foreach (var s in selectedSessions)
            {
                s.ResetToAutoImage();
            }

            RefreshGridRows();
            UpdateMetrics();
            MessageBox.Show($"{selectedSessions.Count}대의 PC 이미지가 기본 자동 선택 모드로 재설정되었습니다.", "설정 완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void SwitchTab(bool isLive)
        {
            if (isLive)
            {
                _btnTabLive.BackColor = Color.FromArgb(14, 165, 233);
                _btnTabLive.ForeColor = Color.White;
                _btnTabLive.Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold);

                _btnTabHistory.BackColor = Color.FromArgb(51, 65, 85);
                _btnTabHistory.ForeColor = Color.FromArgb(203, 213, 225);
                _btnTabHistory.Font = new Font("맑은 고딕", 9.5f);

                _gridHistory.Visible = false;
                _gridClients.Visible = true;
                _gridClients.BringToFront();
                if (_btnDeleteHistory != null) _btnDeleteHistory.Visible = false;
            }
            else
            {
                _btnTabHistory.BackColor = Color.FromArgb(14, 165, 233);
                _btnTabHistory.ForeColor = Color.White;
                _btnTabHistory.Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold);

                _btnTabLive.BackColor = Color.FromArgb(51, 65, 85);
                _btnTabLive.ForeColor = Color.FromArgb(203, 213, 225);
                _btnTabLive.Font = new Font("맑은 고딕", 9.5f);

                _gridClients.Visible = false;
                _gridHistory.Visible = true;
                _gridHistory.BringToFront();
                if (_btnDeleteHistory != null) _btnDeleteHistory.Visible = true;
                RefreshHistoryGrid();
            }
        }

        private Panel CreateMetricCard(string title, Label valLabel, Color valColor)
        {
            var pnl = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(15, 23, 42),
                Margin = new Padding(5, 3, 5, 3),
                Padding = new Padding(0, 4, 0, 4)
            };
            var lblTitle = new Label
            {
                Text = title,
                Dock = DockStyle.Top,
                Height = 22,
                Font = new Font("맑은 고딕", 9f),
                ForeColor = Color.FromArgb(148, 163, 184),
                TextAlign = ContentAlignment.MiddleCenter
            };
            valLabel.Text = "0";
            valLabel.Dock = DockStyle.Fill;
            valLabel.Font = new Font("맑은 고딕", 17f, FontStyle.Bold);
            valLabel.ForeColor = valColor;
            valLabel.TextAlign = ContentAlignment.MiddleCenter;

            pnl.Controls.Add(valLabel);
            pnl.Controls.Add(lblTitle);
            return pnl;
        }

        private Button CreateButton(string text, Color backColor)
        {
            return new Button
            {
                Text = text,
                BackColor = backColor,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                FlatAppearance = { BorderSize = 0 },
                Cursor = Cursors.Hand,
                AutoSize = true,
                Height = 32,
                Margin = new Padding(0, 0, 6, 0)
            };
        }

        private void InitLiveGrid(Control parent)
        {
            _gridClients = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.White,
                GridColor = Color.FromArgb(40, 48, 62),
                BorderStyle = BorderStyle.None,
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToOrderColumns = true, // 드래그 앤 드롭 컬럼 순서 변경 지원!
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true, // 여러 PC 일괄 선택 및 이미지 일괄 지정 지원
                AutoGenerateColumns = false,
                RowTemplate = { Height = 40 }
            };

            _gridClients.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(30, 41, 59);
            _gridClients.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(226, 232, 240);
            _gridClients.ColumnHeadersDefaultCellStyle.Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold);
            _gridClients.ColumnHeadersHeight = 40;
            _gridClients.EnableHeadersVisualStyles = false;

            _gridClients.DefaultCellStyle.BackColor = Color.FromArgb(15, 23, 42);
            _gridClients.DefaultCellStyle.SelectionBackColor = Color.FromArgb(30, 58, 95);
            _gridClients.DefaultCellStyle.SelectionForeColor = Color.White;
            _gridClients.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(20, 29, 48);
            _gridClients.AlternatingRowsDefaultCellStyle.ForeColor = Color.White;

            // 기본 컬럼 정의
            AddGridColumn("status", "배포 상태", 110);
            AddGridColumn("ip", "IP 주소", 120);
            AddGridColumn("model", "제조사 / 모델명", 180);
            AddGridColumn("serial", "시리얼 번호", 140);
            AddGridColumn("mac", "MAC 주소", 140);
            AddGridColumn("disks", "디스크 사양", 180);
            AddGridColumn("policy", "적용 파티션 양식", 180);
            AddGridColumn("image", "적용 WIM 이미지", 210);
            AddGridColumn("progress", "배포 진행 상태", 220);
            AddGridColumn("duration", "경과 시간", 100);

            var colAction = new DataGridViewButtonColumn
            {
                Name = "action",
                HeaderText = "배포 관리",
                Width = 120,
                FlatStyle = FlatStyle.Flat
            };
            _gridClients.Columns.Add(colAction);

            // 이벤트 바인딩
            _gridClients.CellContentClick += GridClients_CellContentClick;
            _gridClients.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex >= 0)
                {
                    OpenSelectImageDialog();
                }
            };
            _gridClients.ColumnHeaderMouseClick += GridClients_ColumnHeaderMouseClick;
            _gridClients.ColumnWidthChanged += (s, e) => ScheduleSaveTableConfig();
            _gridClients.ColumnDisplayIndexChanged += (s, e) => ScheduleSaveTableConfig();

            // 빈 테이블 상태 안내 메시지 그리기
            _gridClients.Paint += (s, e) =>
            {
                if (_gridClients.Rows.Count == 0)
                {
                    string emptyMsg = "📡 현재 연결된 타깃 PC가 없습니다.\nWinPE 클라이언트가 부팅되면 자동으로 감지되어 이곳에 등록됩니다.";
                    using var brush = new SolidBrush(Color.FromArgb(148, 163, 184));
                    using var font = new Font("맑은 고딕", 11f);
                    using var format = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center
                    };
                    var rect = new Rectangle(0, _gridClients.ColumnHeadersHeight, _gridClients.Width, Math.Max(100, _gridClients.Height - _gridClients.ColumnHeadersHeight));
                    e.Graphics.DrawString(emptyMsg, font, brush, rect, format);
                }
            };

            // 헤더 및 행 우클릭 컨텍스트 메뉴
            _gridClients.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                {
                    var hit = _gridClients.HitTest(e.X, e.Y);
                    if (hit.Type == DataGridViewHitTestType.ColumnHeader)
                    {
                        _contextClickedColumnIndex = hit.ColumnIndex;
                        _headerContextMenu.Show(_gridClients, e.Location);
                    }
                    else if (hit.Type == DataGridViewHitTestType.Cell || hit.Type == DataGridViewHitTestType.RowHeader)
                    {
                        if (hit.RowIndex >= 0 && hit.RowIndex < _gridClients.Rows.Count)
                        {
                            if (!_gridClients.Rows[hit.RowIndex].Selected)
                            {
                                _gridClients.ClearSelection();
                                _gridClients.Rows[hit.RowIndex].Selected = true;
                            }
                            _rowContextMenu.Show(_gridClients, e.Location);
                        }
                    }
                }
            };

            parent.Controls.Add(_gridClients);
        }

        private void AddGridColumn(string name, string headerText, int width)
        {
            var col = new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = headerText,
                Width = width,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.Programmatic
            };
            _gridClients.Columns.Add(col);
        }

        private void InitHistoryGrid(Control parent)
        {
            _gridHistory = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.White,
                GridColor = Color.FromArgb(40, 48, 62),
                BorderStyle = BorderStyle.None,
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                ReadOnly = true,
                RowTemplate = { Height = 36 }
            };

            _gridHistory.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(30, 41, 59);
            _gridHistory.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(226, 232, 240);
            _gridHistory.ColumnHeadersDefaultCellStyle.Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold);
            _gridHistory.ColumnHeadersHeight = 40;
            _gridHistory.EnableHeadersVisualStyles = false;

            _gridHistory.DefaultCellStyle.BackColor = Color.FromArgb(15, 23, 42);
            _gridHistory.DefaultCellStyle.SelectionBackColor = Color.FromArgb(30, 58, 95);
            _gridHistory.DefaultCellStyle.SelectionForeColor = Color.White;
            _gridHistory.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(20, 29, 48);
            _gridHistory.AlternatingRowsDefaultCellStyle.ForeColor = Color.White;

            _gridHistory.Columns.Add("time", "완료 일시");
            _gridHistory.Columns.Add("status", "상태");
            _gridHistory.Columns.Add("model", "모델명");
            _gridHistory.Columns.Add("serial", "시리얼 번호");
            _gridHistory.Columns.Add("mac", "MAC 주소");
            _gridHistory.Columns.Add("policy", "적용 정책");
            _gridHistory.Columns.Add("image", "적용 WIM 이미지");
            _gridHistory.Columns.Add("duration", "소요 시간");
            _gridHistory.Columns.Add("error", "비고 (에러 내용)");

            _gridHistory.Columns["time"].Width = 140;
            _gridHistory.Columns["status"].Width = 90;
            _gridHistory.Columns["model"].Width = 160;
            _gridHistory.Columns["serial"].Width = 130;
            _gridHistory.Columns["mac"].Width = 130;
            _gridHistory.Columns["policy"].Width = 160;
            _gridHistory.Columns["image"].Width = 180;
            _gridHistory.Columns["duration"].Width = 90;
            _gridHistory.Columns["error"].Width = 200;

            // 이력 테이블 우클릭 메뉴
            _historyContextMenu = new ContextMenuStrip();
            var itemDelHist = _historyContextMenu.Items.Add("🗑️ 선택한 이력 삭제");
            itemDelHist.Click += (s, e) => DeleteSelectedHistory();

            _historyContextMenu.Items.Add(new ToolStripSeparator());
            var itemClearHist = _historyContextMenu.Items.Add("🧹 전체 이력 모두 비우기");
            itemClearHist.Click += (s, e) => ClearAllHistory();

            _gridHistory.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                {
                    var hit = _gridHistory.HitTest(e.X, e.Y);
                    if (hit.RowIndex >= 0 && hit.RowIndex < _gridHistory.Rows.Count)
                    {
                        if (!_gridHistory.Rows[hit.RowIndex].Selected)
                        {
                            _gridHistory.ClearSelection();
                            _gridHistory.Rows[hit.RowIndex].Selected = true;
                        }
                        _historyContextMenu.Show(_gridHistory, e.Location);
                    }
                }
            };

            // 키보드 Delete 키로 삭제 지원
            _gridHistory.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete)
                {
                    DeleteSelectedHistory();
                }
            };

            _gridHistory.Paint += (s, e) =>
            {
                if (_gridHistory.Rows.Count == 0)
                {
                    string emptyMsg = "📜 완료된 배포 이력이 없습니다.";
                    using var brush = new SolidBrush(Color.FromArgb(148, 163, 184));
                    using var font = new Font("맑은 고딕", 11f);
                    using var format = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center
                    };
                    var rect = new Rectangle(0, _gridHistory.ColumnHeadersHeight, _gridHistory.Width, Math.Max(100, _gridHistory.Height - _gridHistory.ColumnHeadersHeight));
                    e.Graphics.DrawString(emptyMsg, font, brush, rect, format);
                }
            };

            parent.Controls.Add(_gridHistory);
            RefreshHistoryGrid();
        }

        // ====================================================================
        // 테이블 레이아웃(순서, 숨김, 너비) 설정 동기화
        // ====================================================================
        private void ApplySavedUiSettings()
        {
            var tc = _configManager.UiSettings.TableConfig;
            if (tc == null) return;

            bool prev = _isApplyingUiSettings;
            _isApplyingUiSettings = true;
            try
            {
                _gridClients.SuspendLayout();

                // 1. 너비 복원
                if (tc.Widths != null)
                {
                    foreach (var kvp in tc.Widths)
                    {
                        if (_gridClients.Columns.Contains(kvp.Key) && kvp.Value > 20)
                        {
                            _gridClients.Columns[kvp.Key].Width = kvp.Value;
                        }
                    }
                }

                // 2. 숨김 복원
                if (tc.Hidden != null)
                {
                    foreach (var hiddenCol in tc.Hidden)
                    {
                        if (_gridClients.Columns.Contains(hiddenCol))
                        {
                            _gridClients.Columns[hiddenCol].Visible = false;
                        }
                    }
                }

                // 3. 컬럼 표시 순서 복원
                if (tc.Order != null && tc.Order.Count > 0)
                {
                    int displayIndex = 0;
                    foreach (var colName in tc.Order)
                    {
                        if (_gridClients.Columns.Contains(colName))
                        {
                            _gridClients.Columns[colName].DisplayIndex = Math.Min(displayIndex++, _gridClients.Columns.Count - 1);
                        }
                    }
                }
            }
            finally
            {
                _gridClients.ResumeLayout();
                _isApplyingUiSettings = prev;
            }
        }

        private void ScheduleSaveTableConfig()
        {
            if (_isApplyingUiSettings) return;

            _columnSaveDebounceTimer?.Stop();
            _columnSaveDebounceTimer?.Dispose();
            _columnSaveDebounceTimer = new System.Windows.Forms.Timer { Interval = 350 };
            _columnSaveDebounceTimer.Tick += (s, e) =>
            {
                _columnSaveDebounceTimer?.Stop();
                _columnSaveDebounceTimer?.Dispose();
                _columnSaveDebounceTimer = null;
                SaveCurrentTableConfig();
            };
            _columnSaveDebounceTimer.Start();
        }

        private void SaveCurrentTableConfig()
        {
            if (_isApplyingUiSettings) return;
            if (this.WindowState == FormWindowState.Minimized) return;

            var tc = _configManager.UiSettings.TableConfig;
            tc.Hidden = _gridClients.Columns.Cast<DataGridViewColumn>().Where(c => !c.Visible).Select(c => c.Name).ToList();
            tc.Order = _gridClients.Columns.Cast<DataGridViewColumn>().OrderBy(c => c.DisplayIndex).Select(c => c.Name).ToList();
            foreach (DataGridViewColumn c in _gridClients.Columns)
            {
                if (c.Width > 20)
                {
                    tc.Widths[c.Name] = c.Width;
                }
            }
            _configManager.SaveUiSettings();
        }

        private void HideClickedColumn()
        {
            if (_contextClickedColumnIndex >= 0 && _contextClickedColumnIndex < _gridClients.Columns.Count)
            {
                _gridClients.Columns[_contextClickedColumnIndex].Visible = false;
                SaveCurrentTableConfig();
            }
        }

        private void ShowAllColumns()
        {
            foreach (DataGridViewColumn c in _gridClients.Columns)
            {
                c.Visible = true;
            }
            SaveCurrentTableConfig();
        }

        private void ResetColumnConfig()
        {
            _configManager.UiSettings.TableConfig = new TableConfig();
            ApplySavedUiSettings();
            SaveCurrentTableConfig();
        }

        private void ShowColumnSettingsDialog()
        {
            using var dlg = new Form
            {
                Text = "테이블 열 표시 / 숨김 설정",
                Size = new Size(350, 420),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.FromArgb(24, 28, 36),
                ForeColor = Color.White
            };

            var chkList = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(15, 20, 28),
                ForeColor = Color.White,
                CheckOnClick = true
            };

            foreach (DataGridViewColumn col in _gridClients.Columns.Cast<DataGridViewColumn>().OrderBy(c => c.DisplayIndex))
            {
                chkList.Items.Add(new ColumnListItem { Name = col.Name, Text = col.HeaderText }, col.Visible);
            }

            var pnlBottom = new Panel { Dock = DockStyle.Bottom, Height = 45, Padding = new Padding(10) };
            var btnOk = new Button { Text = "적용하기", Dock = DockStyle.Right, Width = 90, BackColor = Color.FromArgb(14, 165, 233), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            btnOk.Click += (s, e) =>
            {
                for (int i = 0; i < chkList.Items.Count; i++)
                {
                    var item = (ColumnListItem)chkList.Items[i];
                    _gridClients.Columns[item.Name].Visible = chkList.GetItemChecked(i);
                }
                SaveCurrentTableConfig();
                dlg.Close();
            };
            pnlBottom.Controls.Add(btnOk);

            dlg.Controls.Add(chkList);
            dlg.Controls.Add(pnlBottom);
            dlg.ShowDialog(this);
        }

        private class ColumnListItem
        {
            public string Name { get; set; } = "";
            public string Text { get; set; } = "";
            public override string ToString() => Text;
        }

        // ====================================================================
        // 통신 이벤트 수신 및 자동 매칭 / 자동 승인
        // ====================================================================
        private void OpenPxeSettingsDialog()
        {
            using var dlg = new PxeSettingsForm(_configManager, _pxeCoordinator);
            dlg.ShowDialog(this);
            UpdateServerStatusBanner();
        }

        private void UpdateServerStatusBanner()
        {
            if (_pxeCoordinator == null) return;

            if (_pxeCoordinator.IsRunning)
            {
                string modeTag = _configManager.ServerConfig.Pxe.DhcpMode == "PROXY" ? "ProxyDHCP" : "Standalone DHCP";
                _lblServerStatus.Text = $"● Agent:39201 | PXE 가동 중 ({_pxeCoordinator.ActiveBindIp}, {modeTag})";
                _lblServerStatus.ForeColor = Color.FromArgb(52, 211, 153);

                _btnPxeServer.Text = "🌐 PXE 서버: ON";
                _btnPxeServer.BackColor = Color.FromArgb(2, 132, 199);

                _udpService.PreferredMasterIp = _pxeCoordinator.ActiveBindIp;
                _udpService.HttpPort = _pxeCoordinator.HttpBootService.Port;
            }
            else
            {
                _lblServerStatus.Text = "● Agent 가동 중 (39200/39201) | PXE 서버: OFF";
                _lblServerStatus.ForeColor = Color.FromArgb(148, 163, 184);

                _btnPxeServer.Text = "🌐 PXE 서버: OFF";
                _btnPxeServer.BackColor = Color.FromArgb(51, 65, 85);

                _udpService.PreferredMasterIp = "";
            }
        }

        private void OnPxeClientBootUpdated(PxeClientBootUpdate update)
        {
            if (!this.IsHandleCreated || this.IsDisposed) return;

            this.BeginInvoke(new Action(() =>
            {
                string normMac = NetworkUtility.NormalizeMac(update.MacAddress);
                if (string.IsNullOrEmpty(normMac)) return;

                // 이미 WinPE 에이전트가 TCP로 정식 연결된 활성 세션이 있으면 덮어쓰지 않음
                bool hasLiveAgent = _sessions.Values.Any(s =>
                    s.Status != "PXE_BOOT" &&
                    string.Equals(NetworkUtility.NormalizeMac(s.Network.PrimaryMac), normMac, StringComparison.OrdinalIgnoreCase));
                if (hasLiveAgent) return;

                string pxeSessionId = "PXE-" + normMac;
                if (!_sessions.TryGetValue(pxeSessionId, out var session))
                {
                    session = new ClientSession
                    {
                        SessionId = pxeSessionId,
                        Status = "PXE_BOOT",
                        ConnectedAt = DateTime.Now
                    };
                    session.Network.PrimaryMac = normMac;
                    _sessions[pxeSessionId] = session;
                }

                if (!string.IsNullOrWhiteSpace(update.IpAddress))
                {
                    session.IpAddress = update.IpAddress;
                    session.Network.IpAddress = update.IpAddress;
                }
                if (!string.IsNullOrWhiteSpace(update.Manufacturer))
                    session.System.Manufacturer = update.Manufacturer;
                if (!string.IsNullOrWhiteSpace(update.ModelName))
                    session.System.ModelName = update.ModelName;
                if (!string.IsNullOrWhiteSpace(update.SerialNumber))
                    session.System.SerialNumber = update.SerialNumber;
                if (!string.IsNullOrWhiteSpace(update.Uuid))
                    session.System.Uuid = update.Uuid;
                session.System.IsUefi = true;

                // iPXE 단계에서 모델명이 수집되면 배포 예정 이미지도 미리 매칭하여 표시
                if (!string.IsNullOrWhiteSpace(session.System.ModelName) && session.MatchedImage == null)
                {
                    session.MatchedImage = ImageMatcher.MatchRule(session, _configManager.ImageCatalog);
                    string rawPattern = session.MatchedImage?.TargetImage?.ImageFileName ?? "*.wim";
                    string mDir = _configManager.ServerConfig.MasterImageDir ?? @"C:\images";
                    var resolved = WimScanner.ResolveLatestImageFile(mDir, rawPattern, session.System.ModelName);
                    session.ResolvedImageFileName = resolved?.FileName;
                }

                session.Progress = new ProgressInfo
                {
                    Percent = update.Percent,
                    Step = update.StepMessage,
                    SpeedMbps = update.SpeedMbps,
                    ElapsedSeconds = (int)Math.Max(0, (DateTime.Now - session.ConnectedAt).TotalSeconds)
                };

                RefreshGridRows();
                UpdateMetrics();
            }));
        }

        private void MergePxePreSessionsOnUiThread(ClientSession session)
        {
            string normMac = NetworkUtility.NormalizeMac(session.Network.PrimaryMac);
            var prePxeSessions = _sessions.Values
                .Where(s => s.Status == "PXE_BOOT" &&
                            ((!string.IsNullOrEmpty(normMac) && string.Equals(NetworkUtility.NormalizeMac(s.Network.PrimaryMac), normMac, StringComparison.OrdinalIgnoreCase)) ||
                             (!string.IsNullOrEmpty(session.IpAddress) && string.Equals(s.IpAddress, session.IpAddress, StringComparison.OrdinalIgnoreCase))))
                .ToList();

            foreach (var pre in prePxeSessions)
            {
                if (pre.IsCustomImageSelected && !string.IsNullOrEmpty(pre.CustomImageFileName))
                {
                    session.SetCustomImage(pre.CustomImageFileName, pre.CustomImageIndex ?? 1);
                }
                _sessions.Remove(pre.SessionId);
            }
        }

        private void OnClientRegistered(ClientSession session)
        {
            this.BeginInvoke(new Action(() =>
            {
                // 정책 및 이미지 자동 매칭
                MergePxePreSessionsOnUiThread(session);
                session.MatchedPolicy = ImageMatcher.MatchPolicy(session, _configManager.PartitionPolicies);
                session.MatchedImage = ImageMatcher.MatchRule(session, _configManager.ImageCatalog);

                // 와일드카드(*) 및 모델명 기준 최신 WIM 이미지 미리 해결
                string rawPattern = session.MatchedImage?.TargetImage?.ImageFileName ?? "*.wim";
                string mDir = _configManager.ServerConfig.MasterImageDir ?? @"C:\images";
                var resolved = WimScanner.ResolveLatestImageFile(mDir, rawPattern, session.System.ModelName);
                if (resolved == null && !string.IsNullOrWhiteSpace(_configManager.ServerConfig.NetworkSharePath))
                {
                    resolved = WimScanner.ResolveLatestImageFile(_configManager.ServerConfig.NetworkSharePath, rawPattern, session.System.ModelName);
                }
                session.ResolvedImageFileName = resolved?.FileName;

                _sessions[session.SessionId] = session;

                RefreshGridRows();
                UpdateMetrics();

                // 자동 승인 조건 검사
                if (_globalAutoApprove && session.MatchedPolicy != null && session.MatchedPolicy.AutoApprove)
                {
                    Task.Delay(1000).ContinueWith(_ =>
                    {
                        this.BeginInvoke(new Action(() =>
                        {
                            ApproveClient(session);
                        }));
                    });
                }
            }));
        }

        private void OnClientProgressUpdated(ClientSession session)
        {
            this.BeginInvoke(new Action(() =>
            {
                _sessions[session.SessionId] = session;
                RefreshGridRows();
                UpdateMetrics();
            }));
        }

        private void OnClientCompleted(ClientSession session, ResultInfo result)
        {
            this.BeginInvoke(new Action(() =>
            {
                _sessions[session.SessionId] = session;

                // 이력 추가
                var hist = new DeployHistoryItem
                {
                    Id = "HIST-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    SessionId = session.SessionId,
                    ModelName = session.System.ModelName,
                    SerialNumber = session.System.SerialNumber,
                    MacAddress = session.Network.PrimaryMac,
                    PolicyName = session.MatchedPolicy?.Name ?? "-",
                    ImageName = session.EffectiveImageFileName,
                    Status = session.Status,
                    DurationSeconds = result.TotalDurationSeconds,
                    ErrorMessage = result.ErrorMessage,
                    CompletedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };
                _configManager.AddHistoryItem(hist);

                if (session.Status == "SUCCESS")
                {
                    _pxeCoordinator.MarkMacDeployCompleted(session.Network.PrimaryMac);
                }

                RefreshGridRows();
                RefreshHistoryGrid();
                UpdateMetrics();
            }));
        }

        private void OnClientDisconnected(ClientSession session)
        {
            this.BeginInvoke(new Action(() =>
            {
                _sessions.Remove(session.SessionId);
                RefreshGridRows();
                UpdateMetrics();
            }));
        }

        // ====================================================================
        // 배포 승인 로직 (대기 상태 READY 뿐만 아니라 실패 FAILED 재시도도 허용)
        // ====================================================================
        private void ApproveClient(ClientSession session)
        {
            if (session.Status != "READY" && session.Status != "FAILED" && session.Status != "SUCCESS") return;

            // 이전 실패 결과 초기화
            session.Result = null;
            session.Progress = new ProgressInfo
            {
                Percent = 0,
                Step = "배포 명령 승인됨 (작업 준비 중)",
                SpeedMbps = 0,
                ElapsedSeconds = 0
            };

            var policy = session.MatchedPolicy ?? _configManager.PartitionPolicies.FirstOrDefault() ?? new PartitionPolicy();
            var rule = session.MatchedImage ?? _configManager.ImageCatalog.FirstOrDefault() ?? new ImageRule();

            _pxeCoordinator.ClearMacDeployCompleted(session.Network.PrimaryMac);
            bool ok = _tcpService.ApproveClient(session, policy, rule, _configManager.ServerConfig);
            if (!ok)
            {
                MessageBox.Show($"클라이언트({session.IpAddress})와의 통신 소켓이 닫혀있거나 응답하지 않습니다.\nWinPE 클라이언트가 켜져 있는지 확인해주세요.", "배포 승인 알림", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            RefreshGridRows();
            UpdateMetrics();
        }

        private void BatchApproveAll(object? sender, EventArgs e)
        {
            int count = 0;
            foreach (var session in _sessions.Values.ToList())
            {
                if (session.Status == "READY" || session.Status == "FAILED")
                {
                    ApproveClient(session);
                    count++;
                }
            }
            if (count > 0)
            {
                MessageBox.Show($"{count}대의 대기/실패 PC에 배포(재시도) 승인 명령을 전송했습니다.", "일괄 승인 완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show("현재 승인 대기 또는 재시도 가능한 PC가 없습니다.", "확인", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void ToggleAutoApprove(object? sender, EventArgs e)
        {
            _globalAutoApprove = !_globalAutoApprove;
            if (_globalAutoApprove)
            {
                _btnAutoApproveToggle.Text = "⚡ 자동 승인: ON";
                _btnAutoApproveToggle.BackColor = Color.FromArgb(5, 150, 105);
            }
            else
            {
                _btnAutoApproveToggle.Text = "⚪ 자동 승인: OFF";
                _btnAutoApproveToggle.BackColor = Color.FromArgb(71, 85, 105);
            }
        }

        // ====================================================================
        // 컬럼 헤더 정렬 로직 (클릭 시 포커스/스크롤 초기화 없이 인플레이스 정렬)
        // ====================================================================
        private List<ClientSession> ApplySort(List<ClientSession> list, string colName, SortOrder order)
        {
            if (order == SortOrder.None) return list;

            Func<ClientSession, object?> keySelector = colName switch
            {
                "status" => s => s.Status,
                "ip" => s => s.IpAddress,
                "model" => s => $"{s.System.Manufacturer} {s.System.ModelName}",
                "serial" => s => s.System.SerialNumber,
                "mac" => s => s.Network.PrimaryMac,
                "disks" => s => s.Disks.Sum(d => d.SizeGb),
                "policy" => s => s.MatchedPolicy?.Name ?? "",
                "image" => s => s.EffectiveImageFileName,
                "progress" => s => s.Progress?.Percent ?? 0,
                "duration" => s => s.Progress?.ElapsedSeconds ?? s.Result?.TotalDurationSeconds ?? 0,
                _ => s => s.SessionId
            };

            return order == SortOrder.Ascending
                ? list.OrderBy(keySelector).ToList()
                : list.OrderByDescending(keySelector).ToList();
        }

        // ====================================================================
        // UI 갱신 메인 루틴 (Rows.Clear() 제거: 수평 스크롤 위치 및 셀 포커스 영구 보존)
        // ====================================================================
        private void RefreshGridRows()
        {
            if (_gridClients.InvokeRequired)
            {
                _gridClients.BeginInvoke(new Action(RefreshGridRows));
                return;
            }

            string keyword = _txtSearch.Text.Trim().ToLower();
            string statusFilter = _cboStatusFilter.SelectedItem?.ToString() ?? "전체 상태";

            var filtered = _sessions.Values.Where(s =>
            {
                // 상태 필터
                if (statusFilter == "PXE 부팅 중" && s.Status != "PXE_BOOT") return false;
                if (statusFilter == "승인 대기" && s.Status != "READY") return false;
                if (statusFilter == "배포 진행 중" && s.Status != "IMAGING") return false;
                if (statusFilter == "배포 성공" && s.Status != "SUCCESS") return false;
                if (statusFilter == "배포 실패" && s.Status != "FAILED") return false;

                // 검색어 필터
                if (!string.IsNullOrEmpty(keyword))
                {
                    string combined = $"{s.System.ModelName} {s.System.SerialNumber} {s.Network.PrimaryMac} {s.IpAddress}".ToLower();
                    if (!combined.Contains(keyword)) return false;
                }
                return true;
            }).ToList();

            if (!string.IsNullOrEmpty(_sortColumn) && _sortOrder != SortOrder.None)
            {
                filtered = ApplySort(filtered, _sortColumn, _sortOrder);
            }

            // 1. 현재 수평 스크롤 위치 및 선택된 세션 ID 보존 (헤더 점프/리셋 완벽 방지)
            int savedHScroll = _gridClients.HorizontalScrollingOffset;
            string? selectedSessionId = null;
            if (_gridClients.SelectedRows.Count > 0 && _gridClients.SelectedRows[0].Tag is ClientSession selS)
            {
                selectedSessionId = selS.SessionId;
            }

            // 2. 기존 행을 SessionId 기준으로 맵핑
            var existingRowMap = new Dictionary<string, DataGridViewRow>();
            foreach (DataGridViewRow r in _gridClients.Rows)
            {
                if (r.Tag is ClientSession s)
                {
                    existingRowMap[s.SessionId] = r;
                }
            }

            // 3. 필터링 대상에서 제외된 세션 행만 안전하게 제거
            var targetSessionIds = new HashSet<string>(filtered.Select(s => s.SessionId));
            for (int i = _gridClients.Rows.Count - 1; i >= 0; i--)
            {
                var r = _gridClients.Rows[i];
                if (r.Tag is ClientSession s && !targetSessionIds.Contains(s.SessionId))
                {
                    _gridClients.Rows.RemoveAt(i);
                }
            }

            // 4. 세션 목록 순서대로 추가 또는 기존 행 셀 내용만 갱신 (In-Place Update)
            for (int i = 0; i < filtered.Count; i++)
            {
                var s = filtered[i];

                string statusText = s.Status switch
                {
                    "PXE_BOOT" => $"PXE 부팅 ({(int)(s.Progress?.Percent ?? 0)}%)",
                    "READY" => "승인 대기",
                    "IMAGING" => "작업 중 (" + (int)(s.Progress?.Percent ?? 0) + "%)",
                    "SUCCESS" => "완료 (성공)",
                    "FAILED" => "실패",
                    _ => s.Status
                };

                string disksText = string.Join(", ", s.Disks.Select(d => $"[{d.DiskIndex}] {(int)d.SizeGb}GB"));
                if (string.IsNullOrEmpty(disksText)) disksText = s.Status == "PXE_BOOT" ? "WinPE 부팅 후 스캔" : "디스크 미감지";

                string progressText = (s.Status == "IMAGING" || s.Status == "PXE_BOOT")
                    ? $"[{(int)(s.Progress?.Percent ?? 0)}%] {s.Progress?.Step}"
                    : (s.Status == "SUCCESS" ? "정상 완료" : (s.Status == "FAILED" ? ("오류: " + (s.Result?.ErrorMessage ?? "배포 실패")) : "대기 중"));

                string durationText = (s.Status == "IMAGING" || s.Status == "PXE_BOOT")
                    ? $"{s.Progress?.ElapsedSeconds ?? 0}초"
                    : (s.Result != null ? $"{s.Result.TotalDurationSeconds}초" : "-");

                string actionButtonText = s.Status switch
                {
                    "PXE_BOOT" => "부팅 중...",
                    "READY" => "배포 승인",
                    "IMAGING" => "배포 중...",
                    "SUCCESS" => "완료됨",
                    "FAILED" => "재시도",
                    _ => "-"
                };

                Color statusColor = s.Status switch
                {
                    "PXE_BOOT" => Color.FromArgb(56, 189, 248),
                    "READY" => Color.FromArgb(253, 224, 71),
                    "IMAGING" => Color.FromArgb(192, 132, 252),
                    "SUCCESS" => Color.FromArgb(74, 222, 128),
                    "FAILED" => Color.FromArgb(248, 113, 113),
                    _ => Color.White
                };

                string imageDisplayText;
                Color imageColor;

                if (s.IsCustomImageSelected && !string.IsNullOrEmpty(s.CustomImageFileName))
                {
                    imageDisplayText = $"[별도선택] {s.CustomImageFileName} (Idx:{s.CustomImageIndex ?? 1})";
                    imageColor = Color.FromArgb(56, 189, 248); // 별도 수동 선택 시 산뜻한 하늘색
                }
                else
                {
                    string rawImg = s.MatchedImage?.TargetImage?.ImageFileName ?? "-";
                    string baseImg = s.ResolvedImageFileName ?? rawImg;
                    if (!string.IsNullOrEmpty(s.ResolvedImageFileName) && (rawImg.Contains('*') || rawImg.Contains('?')))
                    {
                        imageDisplayText = $"[자동선택] {s.ResolvedImageFileName} [최신]";
                    }
                    else
                    {
                        imageDisplayText = $"[자동선택] {baseImg}";
                    }
                    imageColor = Color.FromArgb(52, 211, 153); // 자동선택 기본값은 에메랄드 그린
                }

                if (existingRowMap.TryGetValue(s.SessionId, out var existingRow))
                {
                    // 기존 행 내용만 수정하여 깜빡임 및 포커스 손실 방지
                    SetCellText(existingRow.Cells["status"], statusText);
                    SetCellText(existingRow.Cells["ip"], s.IpAddress);
                    SetCellText(existingRow.Cells["model"], $"{s.System.Manufacturer} {s.System.ModelName}".Trim());
                    SetCellText(existingRow.Cells["serial"], s.System.SerialNumber);
                    SetCellText(existingRow.Cells["mac"], s.Network.PrimaryMac);
                    SetCellText(existingRow.Cells["disks"], disksText);
                    SetCellText(existingRow.Cells["policy"], s.MatchedPolicy?.Name ?? "-");
                    SetCellText(existingRow.Cells["image"], imageDisplayText);
                    SetCellText(existingRow.Cells["progress"], progressText);
                    SetCellText(existingRow.Cells["duration"], durationText);
                    SetCellText(existingRow.Cells["action"], actionButtonText);

                    existingRow.Tag = s;
                    if (existingRow.Cells["status"].Style.ForeColor != statusColor)
                    {
                        existingRow.Cells["status"].Style.ForeColor = statusColor;
                    }
                    if (existingRow.Cells["image"].Style.ForeColor != imageColor)
                    {
                        existingRow.Cells["image"].Style.ForeColor = imageColor;
                    }
                }
                else
                {
                    int rowIdx = _gridClients.Rows.Add(
                        statusText,
                        s.IpAddress,
                        $"{s.System.Manufacturer} {s.System.ModelName}".Trim(),
                        s.System.SerialNumber,
                        s.Network.PrimaryMac,
                        disksText,
                        s.MatchedPolicy?.Name ?? "-",
                        imageDisplayText,
                        progressText,
                        durationText,
                        actionButtonText
                    );

                    var newRow = _gridClients.Rows[rowIdx];
                    newRow.Tag = s;
                    newRow.Cells["status"].Style.ForeColor = statusColor;
                    newRow.Cells["image"].Style.ForeColor = imageColor;
                }
            }

            // 5. 이전 선택 행 복원
            if (!string.IsNullOrEmpty(selectedSessionId))
            {
                foreach (DataGridViewRow r in _gridClients.Rows)
                {
                    if (r.Tag is ClientSession s && s.SessionId == selectedSessionId)
                    {
                        r.Selected = true;
                        break;
                    }
                }
            }

            // 6. 스크롤 위치 복원 (헤더 클릭 또는 자동 갱신 시 왼쪽 처음 헤더로 튀는 현상 원천 차단)
            if (savedHScroll >= 0)
            {
                try
                {
                    _gridClients.HorizontalScrollingOffset = savedHScroll;
                }
                catch { }
            }

            UpdateMetrics();
        }

        private static void SetCellText(DataGridViewCell cell, string text)
        {
            if (cell.Value as string != text)
            {
                cell.Value = text;
            }
        }

        private void UpdateMetrics()
        {
            int total = _sessions.Count;
            int pending = _sessions.Values.Count(s => s.Status == "READY");
            int active = _sessions.Values.Count(s => s.Status == "IMAGING" || s.Status == "PXE_BOOT");
            int success = _sessions.Values.Count(s => s.Status == "SUCCESS") + _configManager.History.Count(h => h.Status == "SUCCESS");
            int failed = _sessions.Values.Count(s => s.Status == "FAILED") + _configManager.History.Count(h => h.Status == "FAILED");

            _valTotal.Text = total.ToString();
            _valPending.Text = pending.ToString();
            _valActive.Text = active.ToString();
            _valSuccess.Text = success.ToString();
            _valFailed.Text = failed.ToString();
        }

        private void RefreshHistoryGrid()
        {
            if (_btnTabHistory != null)
            {
                _btnTabHistory.Text = $"📜 배포 완료 이력 ({_configManager.History.Count})";
            }

            _gridHistory.Rows.Clear();
            foreach (var h in _configManager.History.Take(100))
            {
                int rowIndex = _gridHistory.Rows.Add(
                    h.CompletedAt,
                    h.Status == "SUCCESS" ? "성공" : "실패",
                    h.ModelName,
                    h.SerialNumber,
                    h.MacAddress,
                    h.PolicyName,
                    h.ImageName,
                    $"{h.DurationSeconds}초",
                    string.IsNullOrEmpty(h.ErrorMessage) ? "정상 완료" : h.ErrorMessage
                );
                _gridHistory.Rows[rowIndex].Tag = h;
            }
        }

        private void DeleteSelectedHistory()
        {
            if (_gridHistory.SelectedRows.Count == 0)
            {
                MessageBox.Show("삭제할 배포 이력 항목을 선택해 주세요.", "이력 삭제 안내", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int count = _gridHistory.SelectedRows.Count;
            var confirm = MessageBox.Show($"선택한 {count}개의 배포 이력을 삭제하시겠습니까?", "이력 삭제 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            var toRemove = new List<DeployHistoryItem>();
            foreach (DataGridViewRow row in _gridHistory.SelectedRows)
            {
                if (row.Tag is DeployHistoryItem item)
                {
                    toRemove.Add(item);
                }
            }

            foreach (var item in toRemove)
            {
                _configManager.History.Remove(item);
            }

            _configManager.SaveHistory();
            RefreshHistoryGrid();
            UpdateMetrics();
        }

        private void ClearAllHistory()
        {
            if (_configManager.History.Count == 0)
            {
                MessageBox.Show("삭제할 배포 이력이 없습니다.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var confirm = MessageBox.Show("모든 배포 완료 이력을 완전히 삭제하시겠습니까?\n이 작업은 되돌릴 수 없습니다.", "전체 이력 비우기 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            _configManager.History.Clear();
            _configManager.SaveHistory();
            RefreshHistoryGrid();
            UpdateMetrics();
        }

        private void GridClients_CellContentClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex >= 0 && _gridClients.Columns[e.ColumnIndex].Name == "action")
            {
                var row = _gridClients.Rows[e.RowIndex];
                if (row.Tag is ClientSession session)
                {
                    if (session.Status == "READY" || session.Status == "FAILED")
                    {
                        ApproveClient(session);
                    }
                    else if (session.Status == "SUCCESS")
                    {
                        var ask = MessageBox.Show($"이미 성공적으로 배포 완료된 PC({session.IpAddress})입니다.\n다시 배포 작업을 진행하시겠습니까?", "재배포 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                        if (ask == DialogResult.Yes)
                        {
                            ApproveClient(session);
                        }
                    }
                }
            }
        }

        private void GridClients_ColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (e.ColumnIndex < 0 || e.ColumnIndex >= _gridClients.Columns.Count) return;

            var col = _gridClients.Columns[e.ColumnIndex];
            if (col.Name == "action") return;

            // 정렬 방향 토글 (오름차순 -> 내림차순 -> 해제)
            if (_sortColumn == col.Name)
            {
                _sortOrder = _sortOrder == SortOrder.Ascending ? SortOrder.Descending : (_sortOrder == SortOrder.Descending ? SortOrder.None : SortOrder.Ascending);
            }
            else
            {
                _sortColumn = col.Name;
                _sortOrder = SortOrder.Ascending;
            }

            // 헤더 화살표 아이콘 갱신
            foreach (DataGridViewColumn c in _gridClients.Columns)
            {
                c.HeaderCell.SortGlyphDirection = (c.Name == _sortColumn) ? _sortOrder : SortOrder.None;
            }

            RefreshGridRows();
        }

        // ====================================================================
        // 전체 설정 1클릭 내보내기 / 가져오기
        // ====================================================================
        private void ExportSettings(object? sender, EventArgs e)
        {
            using var sfd = new SaveFileDialog
            {
                Filter = "MultiDeploy 설정 백업 파일 (*.json)|*.json",
                FileName = $"multideploy_settings_backup_{DateTime.Now:yyyyMMdd_HHmm}.json"
            };
            if (sfd.ShowDialog(this) == DialogResult.OK)
            {
                try
                {
                    _configManager.ExportFullSettings(sfd.FileName);
                    MessageBox.Show("모든 설정(파티션 권장양식, 이미지 선택 규칙, 테이블 레이아웃, 서버 설정)이 성공적으로 백업되었습니다.", "내보내기 완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("내보내기 실패: " + ex.Message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ImportSettings(object? sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog
            {
                Filter = "MultiDeploy 설정 백업 파일 (*.json)|*.json"
            };
            if (ofd.ShowDialog(this) == DialogResult.OK)
            {
                if (MessageBox.Show($"선택하신 백업 파일('{Path.GetFileName(ofd.FileName)}')로 시스템 설정을 복원하시겠습니까?", "복원 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    try
                    {
                        if (_configManager.ImportFullSettings(ofd.FileName))
                        {
                            ApplySavedUiSettings();
                            RefreshGridRows();
                            RefreshHistoryGrid();
                            MessageBox.Show("설정이 성공적으로 복원되었습니다!", "복원 완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            MessageBox.Show("설정 파일을 불러오지 못했습니다.", "오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("가져오기 실패: " + ex.Message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 폼 화면 렌더링 및 DPI 레이아웃 완료 후 저장된 너비/순서 재확인 적용
            ApplySavedUiSettings();
            _isApplyingUiSettings = false;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _uiRefreshTimer.Stop();
            _columnSaveDebounceTimer?.Stop();
            _columnSaveDebounceTimer?.Dispose();
            _columnSaveDebounceTimer = null;
            _udpService.Stop();
            _tcpService.Stop();
            _pxeCoordinator?.Dispose();
            if (!_isApplyingUiSettings && this.WindowState != FormWindowState.Minimized)
            {
                SaveCurrentTableConfig();
            }
            base.OnFormClosing(e);
        }
    }
}
