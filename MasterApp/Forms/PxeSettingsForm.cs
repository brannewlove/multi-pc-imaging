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
    public class PxeSettingsForm : Form
    {
        private readonly ConfigManager _configManager;
        private readonly PxeServerCoordinator _pxeCoordinator;

        // 상단 배너 컨트롤
        private Label _lblBannerStatus = null!;
        private Button _btnToggleServer = null!;
        private Button _btnFirewall = null!;

        // 탭 1: 네트워크 및 부트 설정 컨트롤
        private ComboBox _cboAdapters = null!;
        private ComboBox _cboDhcpMode = null!;
        private TextBox _txtBindIp = null!;
        private TextBox _txtSubnetMask = null!;
        private TextBox _txtPoolStart = null!;
        private TextBox _txtPoolEnd = null!;
        private NumericUpDown _numHttpPort = null!;
        private CheckBox _chkAutoStart = null!;

        private Label _lblAssetStatus = null!;
        private TextBox _txtBootWimPath = null!;
        private Button _btnInjectAgent = null!;
        private CheckBox _chkRamCache = null!;
        private CheckBox _chkPreventBootLoop = null!;
        private NumericUpDown _numGuardMinutes = null!;

        // 탭 2: 실시간 임대 현황 및 로그
        private DataGridView _gridLeases = null!;
        private ListBox _lstLogs = null!;
        private System.Windows.Forms.Timer _refreshTimer = null!;

        private List<NetworkAdapterInfo> _adapters = new();

        public PxeSettingsForm(ConfigManager configManager, PxeServerCoordinator pxeCoordinator)
        {
            _configManager = configManager;
            _pxeCoordinator = pxeCoordinator;

            InitializeComponent();
            LoadAdaptersAndBindConfig();
            RefreshAssetStatusUi();
            RefreshServerStateUi();
            LoadInitialLogsAndLeases();

            _pxeCoordinator.LogAdded += OnCoordinatorLogAdded;
            _pxeCoordinator.RunningStateChanged += OnRunningStateChanged;

            _refreshTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            _refreshTimer.Tick += (s, e) => RefreshLeasesGrid();
            _refreshTimer.Start();
        }

        private void InitializeComponent()
        {
            this.Text = "독립 작업대 DHCP & UEFI 64-bit iPXE 초고속 부팅 서버 설정";
            this.Font = new Font("맑은 고딕", 9.5f);
            this.BackColor = Color.FromArgb(17, 24, 39);
            this.ForeColor = Color.White;
            this.AutoScaleMode = AutoScaleMode.Dpi;

            Rectangle workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1366, 768);
            int targetW = Math.Min(1080, Math.Max(880, workArea.Width - 60));
            int targetH = Math.Min(760, Math.Max(580, workArea.Height - 60));
            this.Size = new Size(targetW, targetH);
            this.MinimumSize = new Size(860, 560);
            this.StartPosition = FormStartPosition.CenterParent;

            // ====================================================================
            // 1. 상단 서버 제어 배너
            // ====================================================================
            var pnlTopBanner = new Panel
            {
                Dock = DockStyle.Top,
                Height = 62,
                BackColor = Color.FromArgb(15, 23, 42),
                Padding = new Padding(16, 10, 16, 10)
            };

            _lblBannerStatus = new Label
            {
                Text = "○ PXE / DHCP 서버 중지됨",
                Font = new Font("맑은 고딕", 10.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(148, 163, 184),
                AutoSize = true,
                Location = new Point(16, 19)
            };

            var pnlBannerBtns = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 2, 0, 0)
            };

            _btnFirewall = CreateButton("🛡️ 방화벽 포트 자동 허용", Color.FromArgb(51, 65, 85));
            _btnFirewall.Click += OnRegisterFirewallClick;

            var btnSaveOnly = CreateButton("💾 설정 저장", Color.FromArgb(14, 165, 233));
            btnSaveOnly.Click += (s, e) =>
            {
                if (SaveFormToConfig(showValidationError: true))
                {
                    RefreshAssetStatusUi();
                    MessageBox.Show("PXE 및 DHCP 부팅 서버 설정이 저장되었습니다.", "저장 완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };

            _btnToggleServer = CreateButton("▶ PXE / DHCP 서버 시작", Color.FromArgb(16, 185, 129));
            _btnToggleServer.Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold);
            _btnToggleServer.MinimumSize = new Size(190, 36);
            _btnToggleServer.Click += OnToggleServerClick;

            pnlBannerBtns.Controls.Add(_btnFirewall);
            pnlBannerBtns.Controls.Add(btnSaveOnly);
            pnlBannerBtns.Controls.Add(_btnToggleServer);

            pnlTopBanner.Controls.Add(pnlBannerBtns);
            pnlTopBanner.Controls.Add(_lblBannerStatus);

            // ====================================================================
            // 2. 메인 탭 영역
            // ====================================================================
            var tabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                Padding = new Point(14, 6)
            };

            var tabConfig = new TabPage("⚙ 독립 작업대 DHCP & UEFI 초고속 부트 설정")
            {
                BackColor = Color.FromArgb(24, 28, 36)
            };
            InitConfigTab(tabConfig);

            var tabMonitor = new TabPage("📡 실시간 DHCP 임대 목록 & PXE 부팅 로그")
            {
                BackColor = Color.FromArgb(24, 28, 36)
            };
            InitMonitorTab(tabMonitor);

            tabControl.TabPages.Add(tabConfig);
            tabControl.TabPages.Add(tabMonitor);

            this.Controls.Add(pnlTopBanner);
            this.Controls.Add(tabControl);
            tabControl.BringToFront();
        }

        private void InitConfigTab(TabPage page)
        {
            var container = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(18, 14, 18, 14)
            };

            // --------------------------------------------------------------------
            // 섹션 A: 네트워크 어댑터(NIC) & Standalone DHCP 설정
            // --------------------------------------------------------------------
            var grpNet = CreateSectionPanel("1. 작업대 네트워크 어댑터(NIC) 및 Standalone DHCP 설정", 252);

            var tblNet = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 5,
                Padding = new Padding(8, 6, 8, 4)
            };
            tblNet.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
            tblNet.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            tblNet.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            tblNet.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

            // Row 0: 어댑터 선택
            tblNet.Controls.Add(CreateFieldLabel("바인딩 랜카드(NIC):"), 0, 0);
            _cboAdapters = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.White
            };
            _cboAdapters.SelectedIndexChanged += OnAdapterSelectionChanged;
            tblNet.Controls.Add(_cboAdapters, 1, 0);
            tblNet.SetColumnSpan(_cboAdapters, 2);

            var pnlNicBtns = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(4, 0, 0, 0) };
            var btnRefreshNic = CreateButton("🔄 새로고침", Color.FromArgb(71, 85, 105));
            btnRefreshNic.Height = 28;
            btnRefreshNic.Click += (s, e) => LoadAdaptersAndBindConfig(preserveInputs: true);

            var btnAutoPool = CreateButton("⚡ 대역 자동맞춤", Color.FromArgb(14, 165, 233));
            btnAutoPool.Height = 28;
            btnAutoPool.Click += (s, e) => ApplySuggestedPoolFromSelectedAdapter();

            pnlNicBtns.Controls.Add(btnRefreshNic);
            pnlNicBtns.Controls.Add(btnAutoPool);
            tblNet.Controls.Add(pnlNicBtns, 3, 0);

            // Row 1: DHCP 동작 모드 & 마스터 바인딩 IP
            tblNet.Controls.Add(CreateFieldLabel("DHCP 동작 모드:"), 0, 1);
            _cboDhcpMode = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.White
            };
            _cboDhcpMode.Items.Add("독립 작업대 모드 (Standalone DHCP + PXE — 공유기 없이 단독 구동) [권장]");
            _cboDhcpMode.Items.Add("공유기 병행 모드 (ProxyDHCP — 기존 공유기가 IP 할당, 부트 정보만 응답)");
            _cboDhcpMode.SelectedIndexChanged += (s, e) => UpdateDhcpPoolFieldsState();
            tblNet.Controls.Add(_cboDhcpMode, 1, 1);

            tblNet.Controls.Add(CreateFieldLabel("마스터 바인딩 IP:"), 2, 1);
            _txtBindIp = CreateDarkTextBox();
            tblNet.Controls.Add(_txtBindIp, 3, 1);

            // Row 2: 임대 시작 IP & 임대 종료 IP
            tblNet.Controls.Add(CreateFieldLabel("DHCP 임대 시작 IP:"), 0, 2);
            _txtPoolStart = CreateDarkTextBox();
            tblNet.Controls.Add(_txtPoolStart, 1, 2);

            tblNet.Controls.Add(CreateFieldLabel("DHCP 임대 종료 IP:"), 2, 2);
            _txtPoolEnd = CreateDarkTextBox();
            tblNet.Controls.Add(_txtPoolEnd, 3, 2);

            // Row 3: 서브넷 마스크 & HTTP 부트 포트
            tblNet.Controls.Add(CreateFieldLabel("서브넷 마스크:"), 0, 3);
            _txtSubnetMask = CreateDarkTextBox();
            tblNet.Controls.Add(_txtSubnetMask, 1, 3);

            tblNet.Controls.Add(CreateFieldLabel("HTTP 스트리밍 포트:"), 2, 3);
            _numHttpPort = new NumericUpDown
            {
                Minimum = 1025,
                Maximum = 65535,
                Value = 39202,
                Width = 120,
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.White
            };
            tblNet.Controls.Add(_numHttpPort, 3, 3);

            // Row 4: 자동 시작 옵션
            _chkAutoStart = new CheckBox
            {
                Text = "마스터 프로그램 실행 시 PXE / DHCP 부팅 서버 자동 가동",
                AutoSize = true,
                ForeColor = Color.FromArgb(125, 211, 252),
                Margin = new Padding(4, 6, 0, 0)
            };
            tblNet.Controls.Add(_chkAutoStart, 1, 4);
            tblNet.SetColumnSpan(_chkAutoStart, 3);

            grpNet.Controls.Add(tblNet);
            grpNet.Controls.SetChildIndex(tblNet, 0);

            // --------------------------------------------------------------------
            // 섹션 B: UEFI 64-bit iPXE + wimboot 부트 이미지(boot.wim) 설정
            // --------------------------------------------------------------------
            var grpBoot = CreateSectionPanel("2. UEFI 64-bit 전용 iPXE + HTTP 초고속 부트 이미지 (boot.wim)", 258);

            var pnlBootBody = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 6, 10, 6) };

            _lblAssetStatus = new Label
            {
                Dock = DockStyle.Top,
                Height = 28,
                Font = new Font("맑은 고딕", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(52, 211, 153),
                Text = "부트로더 에셋 점검 중..."
            };

            var tblWim = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 44,
                ColumnCount = 4
            };
            tblWim.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
            tblWim.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tblWim.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
            tblWim.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 235));

            tblWim.Controls.Add(CreateFieldLabel("WinPE boot.wim 경로:"), 0, 0);
            _txtBootWimPath = CreateDarkTextBox();
            tblWim.Controls.Add(_txtBootWimPath, 1, 0);

            var btnBrowseWim = CreateButton("📂 파일 선택", Color.FromArgb(71, 85, 105));
            btnBrowseWim.Height = 30;
            btnBrowseWim.Click += OnBrowseBootWimClick;
            tblWim.Controls.Add(btnBrowseWim, 2, 0);

            _btnInjectAgent = CreateButton("🔧 boot.wim 에이전트 자동 주입", Color.FromArgb(139, 92, 246));
            _btnInjectAgent.Height = 30;
            _btnInjectAgent.Click += OnInjectAgentClick;
            tblWim.Controls.Add(_btnInjectAgent, 3, 0);

            var pnlChecks = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 68,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(4, 6, 0, 0)
            };

            _chkRamCache = new CheckBox
            {
                Text = "boot.wim 인메모리(RAM) 캐싱 활성화 — 다중 PC 동시 초고속 부팅 시 마스터 디스크(SSD) 부하 0% 보장 [권장]",
                AutoSize = true,
                ForeColor = Color.FromArgb(226, 232, 240),
                Margin = new Padding(0, 2, 0, 6)
            };

            var pnlLoopGuard = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0)
            };
            _chkPreventBootLoop = new CheckBox
            {
                Text = "배포 완료(SUCCESS)된 PC 재부팅 시 PXE 부팅 자동 우회 (내장 SSD로 즉시 부팅, 유지 시간:",
                AutoSize = true,
                ForeColor = Color.FromArgb(226, 232, 240),
                Margin = new Padding(0, 3, 6, 0)
            };
            _numGuardMinutes = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 1440,
                Value = 30,
                Width = 60,
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.White
            };
            var lblMinSuffix = new Label
            {
                Text = "분)",
                AutoSize = true,
                ForeColor = Color.FromArgb(226, 232, 240),
                Margin = new Padding(4, 5, 0, 0)
            };
            pnlLoopGuard.Controls.Add(_chkPreventBootLoop);
            pnlLoopGuard.Controls.Add(_numGuardMinutes);
            pnlLoopGuard.Controls.Add(lblMinSuffix);

            pnlChecks.Controls.Add(_chkRamCache);
            pnlChecks.Controls.Add(pnlLoopGuard);

            var lblBiosGuide = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 46,
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.FromArgb(253, 224, 71),
                Padding = new Padding(10, 6, 10, 6),
                Text = "💡 [타깃 PC BIOS 필수 확인] ① Boot Mode: UEFI 전용 (Legacy/CSM 미지원)  ② Secure Boot(보안 부팅): Disabled(해제) 필요\n" +
                       "   독립 작업대 권장 구성: 마스터 PC 유선 랜카드에 고정 IP(예: 192.168.77.1) 설정 후 스위칭 허브에 타깃 PC들을 직결하세요."
            };

            pnlBootBody.Controls.Add(lblBiosGuide);
            pnlBootBody.Controls.Add(pnlChecks);
            pnlBootBody.Controls.Add(tblWim);
            pnlBootBody.Controls.Add(_lblAssetStatus);

            grpBoot.Controls.Add(pnlBootBody);
            grpBoot.Controls.SetChildIndex(pnlBootBody, 0);

            // 상하 배치
            grpBoot.Dock = DockStyle.Top;
            var spacer = new Panel { Dock = DockStyle.Top, Height = 12 };
            grpNet.Dock = DockStyle.Top;

            container.Controls.Add(grpBoot);
            container.Controls.Add(spacer);
            container.Controls.Add(grpNet);

            page.Controls.Add(container);
        }

        private void InitMonitorTab(TabPage page)
        {
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 230,
                BackColor = Color.FromArgb(30, 41, 59),
                Padding = new Padding(12)
            };

            // 상단: DHCP 임대 목록
            var pnlLeaseTop = new Panel { Dock = DockStyle.Top, Height = 34 };
            var lblLeaseTitle = new Label
            {
                Text = "📋 활성 DHCP IP 임대(Lease) 현황",
                Font = new Font("맑은 고딕", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248),
                AutoSize = true,
                Location = new Point(2, 6)
            };
            var btnClearGuard = CreateButton("🔄 완료 PC 재부팅 우회 목록 초기화", Color.FromArgb(71, 85, 105));
            btnClearGuard.Dock = DockStyle.Right;
            btnClearGuard.Height = 28;
            btnClearGuard.Click += (s, e) =>
            {
                _pxeCoordinator.ClearAllBootLoopGuards();
                MessageBox.Show("배포 완료 PC 우회 목록이 초기화되었습니다. 이제 완료된 PC도 다시 PXE로 부팅할 수 있습니다.", "초기화 완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            pnlLeaseTop.Controls.Add(btnClearGuard);
            pnlLeaseTop.Controls.Add(lblLeaseTitle);

            _gridLeases = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(15, 23, 42),
                BorderStyle = BorderStyle.None,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                EnableHeadersVisualStyles = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            _gridLeases.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(30, 41, 59);
            _gridLeases.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(148, 163, 184);
            _gridLeases.ColumnHeadersHeight = 32;
            _gridLeases.DefaultCellStyle.BackColor = Color.FromArgb(17, 24, 39);
            _gridLeases.DefaultCellStyle.ForeColor = Color.White;
            _gridLeases.DefaultCellStyle.SelectionBackColor = Color.FromArgb(30, 58, 138);

            _gridLeases.Columns.Add("mac", "클라이언트 MAC");
            _gridLeases.Columns.Add("ip", "할당된 IP 주소");
            _gridLeases.Columns.Add("stage", "최근 부팅 단계");
            _gridLeases.Columns.Add("guard", "재부팅 우회 보호");
            _gridLeases.Columns.Add("time", "할당 시각");

            split.Panel1.Controls.Add(_gridLeases);
            split.Panel1.Controls.Add(pnlLeaseTop);

            // 하단: 실시간 패킷/스트리밍 로그
            var pnlLogTop = new Panel { Dock = DockStyle.Top, Height = 32 };
            var lblLogTitle = new Label
            {
                Text = "⚡ 실시간 DHCP / TFTP / iPXE HTTP 스트리밍 로그",
                Font = new Font("맑은 고딕", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(52, 211, 153),
                AutoSize = true,
                Location = new Point(2, 6)
            };
            var btnClearLog = CreateButton("로그 지우기", Color.FromArgb(51, 65, 85));
            btnClearLog.Dock = DockStyle.Right;
            btnClearLog.Height = 26;
            btnClearLog.Click += (s, e) => _lstLogs.Items.Clear();
            pnlLogTop.Controls.Add(btnClearLog);
            pnlLogTop.Controls.Add(lblLogTitle);

            _lstLogs = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(11, 15, 25),
                ForeColor = Color.FromArgb(167, 243, 208),
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9f),
                HorizontalScrollbar = true
            };

            split.Panel2.Controls.Add(_lstLogs);
            split.Panel2.Controls.Add(pnlLogTop);

            page.Controls.Add(split);
        }

        private void LoadAdaptersAndBindConfig(bool preserveInputs = false)
        {
            var cfg = _configManager.ServerConfig.Pxe;
            string currentIp = preserveInputs ? _txtBindIp.Text.Trim() : cfg.BindIpAddress;

            _adapters = NetworkUtility.GetCandidateAdapters();
            _cboAdapters.Items.Clear();

            int selectedIdx = -1;
            for (int i = 0; i < _adapters.Count; i++)
            {
                _cboAdapters.Items.Add(_adapters[i]);
                if (!string.IsNullOrEmpty(currentIp) &&
                    string.Equals(_adapters[i].IpAddress, currentIp, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIdx = i;
                }
            }

            if (selectedIdx >= 0)
            {
                _cboAdapters.SelectedIndex = selectedIdx;
            }
            else if (_cboAdapters.Items.Count > 0)
            {
                _cboAdapters.SelectedIndex = 0;
            }

            if (!preserveInputs)
            {
                _cboDhcpMode.SelectedIndex = string.Equals(cfg.DhcpMode, "PROXY", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

                if (!string.IsNullOrWhiteSpace(cfg.BindIpAddress))
                {
                    _txtBindIp.Text = cfg.BindIpAddress;
                    _txtSubnetMask.Text = cfg.SubnetMask;
                    _txtPoolStart.Text = cfg.PoolStartIp;
                    _txtPoolEnd.Text = cfg.PoolEndIp;
                }
                else if (_adapters.Count > 0)
                {
                    ApplyAdapterToFields(_adapters[0]);
                }
                else
                {
                    _txtBindIp.Text = "192.168.77.1";
                    _txtSubnetMask.Text = "255.255.255.0";
                    _txtPoolStart.Text = "192.168.77.100";
                    _txtPoolEnd.Text = "192.168.77.250";
                }

                _numHttpPort.Value = Math.Clamp(cfg.HttpBootPort > 0 ? cfg.HttpBootPort : 39202, 1025, 65535);
                _chkAutoStart.Checked = cfg.Enabled;
                _txtBootWimPath.Text = cfg.BootWimPath ?? "";
                _chkRamCache.Checked = cfg.CacheBootWimInRam;
                _chkPreventBootLoop.Checked = cfg.PreventBootLoop;
                _numGuardMinutes.Value = Math.Clamp(cfg.BootLoopGuardMinutes > 0 ? cfg.BootLoopGuardMinutes : 30, 1, 1440);
                UpdateDhcpPoolFieldsState();
            }
        }

        private void OnAdapterSelectionChanged(object? sender, EventArgs e)
        {
            if (_cboAdapters.SelectedItem is NetworkAdapterInfo nic)
            {
                _txtBindIp.Text = nic.IpAddress;
                if (!string.IsNullOrEmpty(nic.SubnetMask))
                {
                    _txtSubnetMask.Text = nic.SubnetMask;
                }

                // 기존 풀 대역이 선택한 랜카드 대역과 다르면 자동으로 맞춰줌
                if (!NetworkUtility.TryIpToUint(nic.IpAddress, out uint nicUint) ||
                    !NetworkUtility.TryIpToUint(_txtSubnetMask.Text, out uint maskUint) ||
                    !NetworkUtility.TryIpToUint(_txtPoolStart.Text, out uint startUint) ||
                    !NetworkUtility.IsInSameSubnet(nicUint, startUint, maskUint))
                {
                    ApplySuggestedPoolFromSelectedAdapter();
                }
            }
        }

        private void ApplyAdapterToFields(NetworkAdapterInfo nic)
        {
            _txtBindIp.Text = nic.IpAddress;
            _txtSubnetMask.Text = string.IsNullOrEmpty(nic.SubnetMask) ? "255.255.255.0" : nic.SubnetMask;
            var range = NetworkUtility.SuggestPoolRange(_txtBindIp.Text, _txtSubnetMask.Text);
            _txtPoolStart.Text = range.StartIp;
            _txtPoolEnd.Text = range.EndIp;
        }

        private void ApplySuggestedPoolFromSelectedAdapter()
        {
            string bindIp = _txtBindIp.Text.Trim();
            string mask = string.IsNullOrWhiteSpace(_txtSubnetMask.Text) ? "255.255.255.0" : _txtSubnetMask.Text.Trim();
            var range = NetworkUtility.SuggestPoolRange(bindIp, mask);
            _txtPoolStart.Text = range.StartIp;
            _txtPoolEnd.Text = range.EndIp;
        }

        private void UpdateDhcpPoolFieldsState()
        {
            bool isStandalone = _cboDhcpMode.SelectedIndex == 0;
            _txtPoolStart.Enabled = isStandalone;
            _txtPoolEnd.Enabled = isStandalone;
            _txtSubnetMask.Enabled = isStandalone;
        }

        private bool SaveFormToConfig(bool showValidationError)
        {
            string bindIp = _txtBindIp.Text.Trim();
            if (!NetworkUtility.TryIpToUint(bindIp, out uint bindUint) || bindUint == 0)
            {
                if (showValidationError)
                    MessageBox.Show("유효한 마스터 바인딩 IPv4 주소를 입력해주세요.", "입력 확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            bool isStandalone = _cboDhcpMode.SelectedIndex == 0;
            string mask = _txtSubnetMask.Text.Trim();
            string poolStart = _txtPoolStart.Text.Trim();
            string poolEnd = _txtPoolEnd.Text.Trim();

            if (isStandalone)
            {
                if (!NetworkUtility.TryIpToUint(mask, out uint maskUint) || maskUint == 0)
                {
                    if (showValidationError)
                        MessageBox.Show("유효한 서브넷 마스크(예: 255.255.255.0)를 입력해주세요.", "입력 확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }

                if (!NetworkUtility.TryIpToUint(poolStart, out uint sUint) ||
                    !NetworkUtility.TryIpToUint(poolEnd, out uint eUint) ||
                    sUint > eUint)
                {
                    if (showValidationError)
                        MessageBox.Show("DHCP 임대 시작 IP와 종료 IP 범위를 올바르게 입력해주세요.", "입력 확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }

                if (!NetworkUtility.IsInSameSubnet(bindUint, sUint, maskUint) ||
                    !NetworkUtility.IsInSameSubnet(bindUint, eUint, maskUint))
                {
                    if (showValidationError)
                        MessageBox.Show($"DHCP 임대 IP 풀({poolStart} ~ {poolEnd})이 마스터 바인딩 IP({bindIp})와 같은 서브넷 대역에 있지 않습니다.\n[⚡ 대역 자동맞춤] 버튼을 눌러 대역을 맞춰주세요.", "서브넷 불일치", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }

            var pxe = _configManager.ServerConfig.Pxe;
            pxe.DhcpMode = isStandalone ? "STANDALONE" : "PROXY";
            pxe.BindIpAddress = bindIp;
            pxe.SubnetMask = mask;
            pxe.GatewayIp = bindIp;
            pxe.DnsIp = bindIp;
            pxe.PoolStartIp = poolStart;
            pxe.PoolEndIp = poolEnd;
            pxe.HttpBootPort = (int)_numHttpPort.Value;
            pxe.BootWimPath = _txtBootWimPath.Text.Trim().Trim('"');
            pxe.CacheBootWimInRam = _chkRamCache.Checked;
            pxe.PreventBootLoop = _chkPreventBootLoop.Checked;
            pxe.BootLoopGuardMinutes = (int)_numGuardMinutes.Value;
            pxe.Enabled = _chkAutoStart.Checked;

            _configManager.SaveServerConfig();
            return true;
        }

        private void RefreshAssetStatusUi()
        {
            var st = _pxeCoordinator.AssetManager.EnsureAssetsAndLoad(
                _txtBootWimPath.Text.Trim(),
                cacheBootWimInRam: false);

            string snp = st.SnponlyReady ? "snponly.efi ✔" : "snponly.efi ✖";
            string wb = st.WimbootReady ? "wimboot ✔" : "wimboot ✖";
            string mgfw = st.BootmgfwReady ? "bootmgfw.efi ✔" : "bootmgfw.efi ✖";
            string bcd = st.BcdReady ? "BCD ✔" : "BCD ✖";
            string sdi = st.BootSdiReady ? "boot.sdi ✔" : "boot.sdi ✖";
            string wim = st.BootWimExists
                ? $"boot.wim ✔ ({st.BootWimSizeBytes / (1024.0 * 1024.0):F0} MB)"
                : "boot.wim 미지정";

            _lblAssetStatus.Text = $"부트 에셋 상태:  {snp}  |  {wb}  |  {mgfw}  |  {bcd}  |  {sdi}  |  {wim}";
            _lblAssetStatus.ForeColor = st.FullyReadyForBoot
                ? Color.FromArgb(52, 211, 153)
                : Color.FromArgb(253, 224, 71);
        }

        private void RefreshServerStateUi()
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(RefreshServerStateUi));
                return;
            }

            if (_pxeCoordinator.IsRunning)
            {
                string modeStr = _configManager.ServerConfig.Pxe.DhcpMode == "PROXY" ? "ProxyDHCP" : "독립 작업대 DHCP";
                _lblBannerStatus.Text = $"● 가동 중: {modeStr} ({_pxeCoordinator.ActiveBindIp}) | TFTP:69 | HTTP:{_pxeCoordinator.HttpBootService.Port}";
                _lblBannerStatus.ForeColor = Color.FromArgb(52, 211, 153);

                _btnToggleServer.Text = "⏹ PXE / DHCP 서버 중지";
                _btnToggleServer.BackColor = Color.FromArgb(239, 68, 68);
            }
            else
            {
                _lblBannerStatus.Text = "○ PXE / DHCP 부팅 서버 중지됨";
                _lblBannerStatus.ForeColor = Color.FromArgb(148, 163, 184);

                _btnToggleServer.Text = "▶ PXE / DHCP 서버 시작";
                _btnToggleServer.BackColor = Color.FromArgb(16, 185, 129);
            }
        }

        private void OnToggleServerClick(object? sender, EventArgs e)
        {
            if (_pxeCoordinator.IsRunning)
            {
                _pxeCoordinator.Stop();
                _configManager.ServerConfig.Pxe.Enabled = false;
                _chkAutoStart.Checked = false;
                _configManager.SaveServerConfig();
                RefreshServerStateUi();
                return;
            }

            if (!SaveFormToConfig(showValidationError: true)) return;

            this.Cursor = Cursors.WaitCursor;
            try
            {
                var (ok, msg) = _pxeCoordinator.Start();
                RefreshAssetStatusUi();
                RefreshServerStateUi();

                if (ok)
                {
                    _chkAutoStart.Checked = true;
                    _configManager.ServerConfig.Pxe.Enabled = true;
                    _configManager.SaveServerConfig();
                }
                else
                {
                    MessageBox.Show(msg, "PXE / DHCP 서버 시작 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            finally
            {
                this.Cursor = Cursors.Default;
            }
        }

        private void OnBrowseBootWimClick(object? sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog
            {
                Title = "UEFI 64-bit WinPE 부트 이미지(boot.wim) 선택",
                Filter = "Windows PE WIM 이미지 (*.wim)|*.wim|모든 파일 (*.*)|*.*"
            };

            if (!string.IsNullOrWhiteSpace(_txtBootWimPath.Text))
            {
                string? dir = Path.GetDirectoryName(_txtBootWimPath.Text);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    ofd.InitialDirectory = dir;
                }
            }

            if (ofd.ShowDialog(this) == DialogResult.OK)
            {
                _txtBootWimPath.Text = ofd.FileName;
                RefreshAssetStatusUi();
            }
        }

        private async void OnInjectAgentClick(object? sender, EventArgs e)
        {
            string wimPath = _txtBootWimPath.Text.Trim();
            if (string.IsNullOrEmpty(wimPath) || !File.Exists(wimPath))
            {
                MessageBox.Show("먼저 패치할 순정 WinPE boot.wim 파일을 선택해주세요.", "boot.wim 선택 필요", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (MessageBox.Show(
                $"선택한 boot.wim 파일에 자동 실행 스크립트(startnet.cmd)와 PeImagingAgent.exe를 주입하시겠습니까?\n\n대상: {wimPath}\n\n※ DISM 마운트 및 저장에 약 20~60초가 소요됩니다.",
                "WinPE 에이전트 자동 주입 확인",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            _btnInjectAgent.Enabled = false;
            _btnToggleServer.Enabled = false;
            string origText = _btnInjectAgent.Text;
            _btnInjectAgent.Text = "⏳ DISM 패치 진행 중...";

            try
            {
                var (ok, msg) = await _pxeCoordinator.AssetManager.InjectAgentIntoBootWimAsync(wimPath, statusLine =>
                {
                    if (!this.IsDisposed)
                    {
                        this.BeginInvoke(new Action(() =>
                        {
                            _lblAssetStatus.Text = $"🔧 {statusLine}";
                        }));
                    }
                });

                RefreshAssetStatusUi();
                MessageBox.Show(msg, ok ? "에이전트 자동 주입 완료" : "주입 실패", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            }
            finally
            {
                if (!this.IsDisposed)
                {
                    _btnInjectAgent.Text = origText;
                    _btnInjectAgent.Enabled = true;
                    _btnToggleServer.Enabled = true;
                }
            }
        }

        private async void OnRegisterFirewallClick(object? sender, EventArgs e)
        {
            _btnFirewall.Enabled = false;
            try
            {
                var (ok, msg) = await NetworkUtility.RegisterFirewallRulesAsync((int)_numHttpPort.Value);
                MessageBox.Show(msg, ok ? "방화벽 허용 완료" : "방화벽 설정 알림", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            finally
            {
                if (!this.IsDisposed) _btnFirewall.Enabled = true;
            }
        }

        private void LoadInitialLogsAndLeases()
        {
            foreach (var line in _pxeCoordinator.GetRecentLogsSnapshot())
            {
                _lstLogs.Items.Add(line);
            }
            if (_lstLogs.Items.Count > 0)
            {
                _lstLogs.TopIndex = _lstLogs.Items.Count - 1;
            }
            RefreshLeasesGrid();
        }

        private void RefreshLeasesGrid()
        {
            if (this.IsDisposed || !_gridLeases.IsHandleCreated) return;

            var leases = _pxeCoordinator.DhcpService.LeasesByMac.Values
                .OrderByDescending(l => l.AllocatedAt)
                .ToList();

            _gridLeases.Rows.Clear();
            foreach (var l in leases)
            {
                bool guarded = _pxeCoordinator.IsMacInBootLoopGuard(l.MacAddress);
                _gridLeases.Rows.Add(
                    l.MacAddress,
                    l.IpAddress,
                    l.Stage,
                    guarded ? "우회 활성 (완료됨)" : "-",
                    l.AllocatedAt.ToString("HH:mm:ss"));
            }
        }

        private void OnCoordinatorLogAdded(string line)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            this.BeginInvoke(new Action(() =>
            {
                if (this.IsDisposed) return;
                _lstLogs.Items.Add(line);
                if (_lstLogs.Items.Count > 500) _lstLogs.Items.RemoveAt(0);
                _lstLogs.TopIndex = _lstLogs.Items.Count - 1;
            }));
        }

        private void OnRunningStateChanged(bool running)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            RefreshServerStateUi();
        }

        private static Panel CreateSectionPanel(string title, int height)
        {
            var pnl = new Panel
            {
                Height = height,
                BackColor = Color.FromArgb(17, 24, 39),
                Padding = new Padding(12, 8, 12, 8)
            };
            var lbl = new Label
            {
                Text = title,
                Dock = DockStyle.Top,
                Height = 28,
                Font = new Font("맑은 고딕", 10.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248)
            };
            pnl.Controls.Add(lbl);
            return pnl;
        }

        private static Label CreateFieldLabel(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                ForeColor = Color.FromArgb(203, 213, 225)
            };
        }

        private static TextBox CreateDarkTextBox()
        {
            return new TextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
        }

        private static Button CreateButton(string text, Color backColor)
        {
            var btn = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Height = 34,
                Padding = new Padding(10, 0, 10, 0),
                FlatStyle = FlatStyle.Flat,
                BackColor = backColor,
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Margin = new Padding(4, 2, 4, 2)
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
            _pxeCoordinator.LogAdded -= OnCoordinatorLogAdded;
            _pxeCoordinator.RunningStateChanged -= OnRunningStateChanged;
            base.OnFormClosing(e);
        }
    }
}

