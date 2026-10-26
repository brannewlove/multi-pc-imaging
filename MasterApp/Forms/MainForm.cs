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

        private bool _globalAutoApprove = true;

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

        // DataGridView
        private DataGridView _gridClients;
        private DataGridView _gridHistory;
        private ContextMenuStrip _headerContextMenu;
        private int _contextClickedColumnIndex = -1;

        // 내부 상태 캐시
        private readonly Dictionary<string, ClientSession> _sessions = new();
        private System.Windows.Forms.Timer _uiRefreshTimer;

        public MainForm()
        {
            _configManager = new ConfigManager();
            _udpService = new UdpDiscoveryService();
            _tcpService = new TcpServerService();

            InitializeComponent();
            ApplySavedUiSettings();

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
        }

        private void InitializeComponent()
        {
            this.Text = "MultiDeployOS Master - 다중 PC 이미지 배포 관제 시스템";
            this.Size = new Size(1400, 880);
            this.MinimumSize = new Size(1150, 680);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = new Font("맑은 고딕", 9.5f);
            this.BackColor = Color.FromArgb(17, 24, 39);
            this.ForeColor = Color.White;

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

            var lblLogo = new Label
            {
                Text = "⚡ MultiDeployOS Master",
                Font = new Font("맑은 고딕", 12f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248),
                AutoSize = true,
                Location = new Point(16, 16)
            };
            pnlHeader.Controls.Add(lblLogo);

            _lblServerStatus = new Label
            {
                Text = "● 서버 가동 중 (UDP: 39200 | TCP: 39201)",
                Font = new Font("맑은 고딕", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(52, 211, 153),
                AutoSize = true,
                Location = new Point(270, 20)
            };
            pnlHeader.Controls.Add(_lblServerStatus);

            var pnlTopButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false, // 절대 수직 줄바꿈 금지
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 10, 12, 0)
            };

            _btnAutoApproveToggle = CreateButton("⚡ 자동 승인: ON", Color.FromArgb(5, 150, 105));
            _btnAutoApproveToggle.Click += ToggleAutoApprove;

            _btnBatchApprove = CreateButton("✔ 대기 PC 일괄 승인", Color.FromArgb(14, 165, 233));
            _btnBatchApprove.Click += BatchApproveAll;

            var btnOpenPolicies = CreateButton("🗂️ 파티션 권장양식", Color.FromArgb(51, 65, 85));
            btnOpenPolicies.Click += (s, e) =>
            {
                using var form = new PolicyEditorForm(_configManager);
                form.ShowDialog(this);
            };

            var btnOpenImageRules = CreateButton("📀 이미지 선택 규칙", Color.FromArgb(51, 65, 85));
            btnOpenImageRules.Click += (s, e) =>
            {
                using var form = new ImageRuleEditorForm(_configManager);
                form.ShowDialog(this);
            };

            var btnExport = CreateButton("📤 설정 백업", Color.FromArgb(71, 85, 105));
            btnExport.Click += ExportSettings;

            var btnImport = CreateButton("📥 설정 복원", Color.FromArgb(71, 85, 105));
            btnImport.Click += ImportSettings;

            pnlTopButtons.Controls.Add(_btnAutoApproveToggle);
            pnlTopButtons.Controls.Add(_btnBatchApprove);
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

            // 1-3. 툴바 (탭 버튼 + 검색 + 필터 + 테이블 설정, 52px)
            var pnlToolbar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 52,
                BackColor = Color.FromArgb(30, 41, 59),
                Padding = new Padding(12, 8, 12, 8)
            };

            _btnTabLive = new Button
            {
                Text = "🖥️ 실시간 타깃 PC 관제",
                Location = new Point(12, 9),
                Size = new Size(185, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(14, 165, 233),
                ForeColor = Color.White,
                Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnTabLive.FlatAppearance.BorderSize = 0;
            _btnTabLive.Click += (s, e) => SwitchTab(true);

            _btnTabHistory = new Button
            {
                Text = "📜 배포 완료 이력 (0)",
                Location = new Point(203, 9),
                Size = new Size(175, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.FromArgb(203, 213, 225),
                Font = new Font("맑은 고딕", 9.5f),
                Cursor = Cursors.Hand
            };
            _btnTabHistory.FlatAppearance.BorderSize = 0;
            _btnTabHistory.Click += (s, e) => SwitchTab(false);

            pnlToolbar.Controls.Add(_btnTabLive);
            pnlToolbar.Controls.Add(_btnTabHistory);

            var lblSearch = new Label { Text = "🔍 검색:", AutoSize = true, Location = new Point(410, 17), ForeColor = Color.FromArgb(148, 163, 184) };
            _txtSearch = new TextBox { Location = new Point(465, 13), Width = 190, BackColor = Color.FromArgb(15, 23, 42), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Font = new Font("맑은 고딕", 9.5f) };
            _txtSearch.TextChanged += (s, e) => RefreshGridRows();

            var lblStatus = new Label { Text = "상태 필터:", AutoSize = true, Location = new Point(665, 17), ForeColor = Color.FromArgb(148, 163, 184) };
            _cboStatusFilter = new ComboBox
            {
                Location = new Point(735, 13),
                Width = 125,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _cboStatusFilter.Items.AddRange(new object[] { "전체 상태", "승인 대기", "배포 진행 중", "배포 성공", "배포 실패" });
            _cboStatusFilter.SelectedIndex = 0;
            _cboStatusFilter.SelectedIndexChanged += (s, e) => RefreshGridRows();

            var btnColumnConfig = CreateButton("⚙ 테이블 열 설정", Color.FromArgb(51, 65, 85));
            btnColumnConfig.Location = new Point(875, 10);
            btnColumnConfig.Height = 32;
            btnColumnConfig.Click += (s, e) => ShowColumnSettingsDialog();

            pnlToolbar.Controls.Add(lblSearch);
            pnlToolbar.Controls.Add(_txtSearch);
            pnlToolbar.Controls.Add(lblStatus);
            pnlToolbar.Controls.Add(_cboStatusFilter);
            pnlToolbar.Controls.Add(btnColumnConfig);

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
                MultiSelect = false,
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
            AddGridColumn("image", "적용 WIM 이미지", 200);
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
            _gridClients.ColumnHeaderMouseClick += GridClients_ColumnHeaderMouseClick;
            _gridClients.ColumnWidthChanged += (s, e) => SaveCurrentTableConfig();
            _gridClients.ColumnDisplayIndexChanged += (s, e) => SaveCurrentTableConfig();

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

            // 헤더 우클릭 컨텍스트 메뉴
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
                ReadOnly = true
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

            // 1. 너비 복원
            foreach (var kvp in tc.Widths)
            {
                if (_gridClients.Columns.Contains(kvp.Key))
                {
                    _gridClients.Columns[kvp.Key].Width = kvp.Value;
                }
            }

            // 2. 숨김 복원
            foreach (var hiddenCol in tc.Hidden)
            {
                if (_gridClients.Columns.Contains(hiddenCol))
                {
                    _gridClients.Columns[hiddenCol].Visible = false;
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

        private void SaveCurrentTableConfig()
        {
            var tc = _configManager.UiSettings.TableConfig;
            tc.Hidden = _gridClients.Columns.Cast<DataGridViewColumn>().Where(c => !c.Visible).Select(c => c.Name).ToList();
            tc.Order = _gridClients.Columns.Cast<DataGridViewColumn>().OrderBy(c => c.DisplayIndex).Select(c => c.Name).ToList();
            foreach (DataGridViewColumn c in _gridClients.Columns)
            {
                tc.Widths[c.Name] = c.Width;
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
        private void OnClientRegistered(ClientSession session)
        {
            this.BeginInvoke(new Action(() =>
            {
                // 정책 및 이미지 자동 매칭
                session.MatchedPolicy = ImageMatcher.MatchPolicy(session, _configManager.PartitionPolicies);
                session.MatchedImage = ImageMatcher.MatchRule(session, _configManager.ImageCatalog);
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
                    ImageName = session.MatchedImage?.TargetImage?.ImageFileName ?? "-",
                    Status = session.Status,
                    DurationSeconds = result.TotalDurationSeconds,
                    ErrorMessage = result.ErrorMessage,
                    CompletedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };
                _configManager.AddHistoryItem(hist);

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
        // 배포 승인 로직
        // ====================================================================
        private void ApproveClient(ClientSession session)
        {
            if (session.Status != "READY") return;

            var policy = session.MatchedPolicy ?? _configManager.PartitionPolicies[0];
            var rule = session.MatchedImage ?? _configManager.ImageCatalog[0];

            _tcpService.ApproveClient(session, policy, rule, _configManager.ServerConfig);
            RefreshGridRows();
            UpdateMetrics();
        }

        private void BatchApproveAll(object? sender, EventArgs e)
        {
            int count = 0;
            foreach (var session in _sessions.Values.ToList())
            {
                if (session.Status == "READY")
                {
                    ApproveClient(session);
                    count++;
                }
            }
            if (count > 0)
            {
                MessageBox.Show($"{count}대의 대기 PC에 배포 승인 명령을 전송했습니다.", "일괄 승인 완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show("현재 승인 대기 중인 PC가 없습니다.", "확인", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        // UI 갱신 메인 루틴
        // ====================================================================
        private void RefreshGridRows()
        {
            string keyword = _txtSearch.Text.Trim().ToLower();
            string statusFilter = _cboStatusFilter.SelectedItem?.ToString() ?? "전체 상태";

            var filtered = _sessions.Values.Where(s =>
            {
                // 상태 필터
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

            _gridClients.Rows.Clear();

            foreach (var s in filtered)
            {
                string statusText = s.Status switch
                {
                    "READY" => "승인 대기",
                    "IMAGING" => "작업 중 (" + (int)(s.Progress?.Percent ?? 0) + "%)",
                    "SUCCESS" => "완료 (성공)",
                    "FAILED" => "실패",
                    _ => s.Status
                };

                string disksText = string.Join(", ", s.Disks.Select(d => $"[{d.DiskIndex}] {(int)d.SizeGb}GB"));
                if (string.IsNullOrEmpty(disksText)) disksText = "디스크 미감지";

                string progressText = s.Status == "IMAGING"
                    ? $"[{(int)(s.Progress?.Percent ?? 0)}%] {s.Progress?.Step}"
                    : (s.Status == "SUCCESS" ? "정상 완료" : (s.Status == "FAILED" ? ("오류: " + s.Result?.ErrorMessage) : "대기 중"));

                string durationText = s.Status == "IMAGING"
                    ? $"{s.Progress?.ElapsedSeconds ?? 0}초"
                    : (s.Result != null ? $"{s.Result.TotalDurationSeconds}초" : "-");

                string actionButtonText = s.Status switch
                {
                    "READY" => "배포 승인",
                    "IMAGING" => "배포 중...",
                    "SUCCESS" => "완료됨",
                    "FAILED" => "재시도",
                    _ => "-"
                };

                int rowIdx = _gridClients.Rows.Add(
                    statusText,
                    s.IpAddress,
                    $"{s.System.Manufacturer} {s.System.ModelName}".Trim(),
                    s.System.SerialNumber,
                    s.Network.PrimaryMac,
                    disksText,
                    s.MatchedPolicy?.Name ?? "-",
                    s.MatchedImage?.TargetImage?.ImageFileName ?? "-",
                    progressText,
                    durationText,
                    actionButtonText
                );

                var row = _gridClients.Rows[rowIdx];
                row.Tag = s;

                // 상태별 배경색 하이라이트
                if (s.Status == "READY") row.Cells["status"].Style.ForeColor = Color.FromArgb(253, 224, 71);
                else if (s.Status == "IMAGING") row.Cells["status"].Style.ForeColor = Color.FromArgb(192, 132, 252);
                else if (s.Status == "SUCCESS") row.Cells["status"].Style.ForeColor = Color.FromArgb(74, 222, 128);
                else if (s.Status == "FAILED") row.Cells["status"].Style.ForeColor = Color.FromArgb(248, 113, 113);
            }

            UpdateMetrics();
        }

        private void UpdateMetrics()
        {
            int total = _sessions.Count;
            int pending = _sessions.Values.Count(s => s.Status == "READY");
            int active = _sessions.Values.Count(s => s.Status == "IMAGING");
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
            foreach (var h in _configManager.History.Take(50))
            {
                _gridHistory.Rows.Add(
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
            }
        }

        private void GridClients_CellContentClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex >= 0 && _gridClients.Columns[e.ColumnIndex].Name == "action")
            {
                var row = _gridClients.Rows[e.RowIndex];
                if (row.Tag is ClientSession session && session.Status == "READY")
                {
                    ApproveClient(session);
                }
            }
        }

        private void GridClients_ColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
        {
            SaveCurrentTableConfig();
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

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _uiRefreshTimer.Stop();
            _udpService.Stop();
            _tcpService.Stop();
            SaveCurrentTableConfig();
            base.OnFormClosing(e);
        }
    }
}
